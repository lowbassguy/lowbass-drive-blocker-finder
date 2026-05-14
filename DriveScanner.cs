// =============================================================================
//  Title:   lowbass' Drive Blocker Finder - DriveScanner
//  Author:  Joshua "lowbass" Sommerfeldt
//  Date:    2026-05-14
//  Purpose: The brains of the operation. Two responsibilities:
//             1. Enumerate available drives on the machine.
//             2. Given a drive letter, find processes that appear to be
//                blocking it from ejecting.
//
//           Detection strategy is layered (each strategy is best-effort, and
//           results are merged & deduplicated by PID):
//             (a) Process.MainModule.FileName -> running from the drive
//             (b) Process.Modules             -> a DLL on the drive is loaded
//             (c) Restart Manager API         -> register volume root and ask
//                                                Windows who holds it
//
//           Known limitations (documented in README under "Future work"):
//             - Doesn't enumerate raw kernel file handles. That requires
//               NtQuerySystemInformation + NtQueryObject, which can hang on
//               certain handle types and needs careful threading. We may add
//               it in v2.
//             - Process.Modules from a 64-bit host can be incomplete when the
//               target is a 32-bit process. Run as admin to maximize coverage.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LowbassDriveBlockerFinder.Models;

namespace LowbassDriveBlockerFinder;

public class DriveScanner
{
    // Logger.Instance reads cleanly inline; we cache it here purely for brevity.
    private readonly Logger _log = Logger.Instance;

    // =========================================================================
    //  Restart Manager P/Invoke surface (rstrtmgr.dll)
    //
    //  Reference: https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/
    //
    //  The RM API is what the Windows Installer (and the "files in use" dialog
    //  you see during upgrades) uses to figure out which processes need to be
    //  shut down to release given resources. It's the cleanest first-party
    //  answer to "who's holding this?"
    // =========================================================================

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(
        out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles, string[]? rgsFilenames,
        uint nApplications, [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    // Sizes are dictated by the Windows headers; do not change.
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;
        public uint ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    // =========================================================================
    //  Public API
    // =========================================================================

    /// <summary>
    /// Returns every drive Windows currently knows about, with type + size.
    /// </summary>
    public List<DriveItem> EnumerateDrives()
    {
        _log.Info("DriveScanner", "EnumerateDrives", "START", "listing all drives");
        var result = new List<DriveItem>();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                var item = new DriveItem
                {
                    DriveLetter = d.Name,
                    DriveType   = d.DriveType.ToString(),
                    VolumeLabel = d.IsReady ? d.VolumeLabel : "(not ready)",
                    TotalSize   = d.IsReady ? FormatBytes(d.TotalSize) : "?"
                };
                result.Add(item);
                _log.Debug("DriveScanner", "EnumerateDrives", "FOUND",
                    $"drive={item.DriveLetter} type={item.DriveType} label='{item.VolumeLabel}'");
            }
            catch (Exception ex)
            {
                _log.Warn("DriveScanner", "EnumerateDrives", "SKIP",
                    $"drive={d.Name} error={ex.Message}");
            }
        }

        _log.Success("DriveScanner", "EnumerateDrives", "DONE", $"count={result.Count}");
        return result;
    }

    /// <summary>
    /// Find every process plausibly blocking the given drive from ejection.
    /// Cancellable - this can take a few seconds on systems with many processes.
    /// </summary>
    public List<ProcessUsage> FindBlockingProcesses(string driveLetter, CancellationToken ct)
    {
        _log.Info("DriveScanner", "FindBlockingProcesses", "START", $"drive={driveLetter}");

        // Normalize: we want "E:" (no trailing slash) for prefix-matching paths,
        // because absolute file paths come back as "E:\Foo\bar.dll".
        string drivePrefix = driveLetter.TrimEnd('\\').ToUpperInvariant();
        if (!drivePrefix.EndsWith(":")) drivePrefix += ":";

        // Keyed by PID so the two strategies dedupe automatically
        var results = new Dictionary<int, ProcessUsage>();

        // Strategy 1: walk every process, check its main module + loaded modules
        ScanProcessModules(drivePrefix, results, ct);

        // Strategy 2: ask Restart Manager. Best-effort - works well for some
        // resource types and not others when given a volume root.
        ScanWithRestartManager(driveLetter, results, ct);

        _log.Success("DriveScanner", "FindBlockingProcesses", "DONE",
            $"drive={driveLetter} blockers={results.Count}");
        return results.Values.OrderBy(p => p.ProcessName).ToList();
    }

    // =========================================================================
    //  Strategy 1: process module inspection
    // =========================================================================

    private void ScanProcessModules(
        string drivePrefix,
        Dictionary<int, ProcessUsage> results,
        CancellationToken ct)
    {
        _log.Debug("DriveScanner", "ScanProcessModules", "START", $"prefix={drivePrefix}");

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            _log.Error("DriveScanner", "ScanProcessModules", "FAIL_ENUM", $"error={ex.Message}");
            return;
        }

        int inspected = 0, matched = 0, denied = 0;

        foreach (var p in processes)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                bool blocked = false;
                string reason = "";
                var blockingFiles = new List<string>();
                string mainModulePath = "";

                // (a) Main module / .exe path. Frequently denied for protected processes.
                try
                {
                    mainModulePath = p.MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(mainModulePath) &&
                        mainModulePath.StartsWith(drivePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        blocked = true;
                        blockingFiles.Add(mainModulePath);
                        reason = "Executable is on drive";
                    }
                }
                catch
                {
                    // Likely Access Denied. Not a real error - many system processes
                    // refuse module queries unless we're elevated.
                    denied++;
                }

                // (b) Loaded modules - catches any DLL loaded from the drive
                try
                {
                    foreach (ProcessModule mod in p.Modules)
                    {
                        var path = mod.FileName ?? "";
                        if (path.StartsWith(drivePrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            blocked = true;
                            if (!blockingFiles.Contains(path)) blockingFiles.Add(path);
                            if (string.IsNullOrEmpty(reason)) reason = "Has module(s) loaded from drive";
                        }
                    }
                }
                catch
                {
                    // Same deal - protected process, or 32/64-bit mismatch
                    denied++;
                }

                if (blocked)
                {
                    matched++;
                    results[p.Id] = new ProcessUsage
                    {
                        Pid          = p.Id,
                        ProcessName  = p.ProcessName,
                        MainModule   = mainModulePath,
                        Reason       = reason,
                        BlockingFiles = blockingFiles
                    };
                    _log.Debug("DriveScanner", "ScanProcessModules", "MATCH",
                        $"pid={p.Id} name={p.ProcessName} files={blockingFiles.Count}");
                }

                inspected++;
            }
            catch (Exception ex)
            {
                _log.Debug("DriveScanner", "ScanProcessModules", "ERR_PROC",
                    $"pid={p.Id} error={ex.Message}");
            }
            finally
            {
                // Process is IDisposable - releases the underlying OS handle
                try { p.Dispose(); } catch { }
            }
        }

        _log.Info("DriveScanner", "ScanProcessModules", "DONE",
            $"inspected={inspected} matched={matched} access_denied_events={denied}");
    }

    // =========================================================================
    //  Strategy 2: Restart Manager
    // =========================================================================

    private void ScanWithRestartManager(
        string driveLetter,
        Dictionary<int, ProcessUsage> results,
        CancellationToken ct)
    {
        _log.Debug("DriveScanner", "RestartManager", "START", $"drive={driveLetter}");

        uint handle = 0;
        try
        {
            // Session key just needs to be unique-per-session; a GUID is plenty.
            int rc = RmStartSession(out handle, 0, Guid.NewGuid().ToString());
            if (rc != 0)
            {
                _log.Warn("DriveScanner", "RestartManager", "FAIL_START", $"rc={rc}");
                return;
            }

            // Register the volume root as a resource. RM is happiest with specific
            // file paths; volume roots are best-effort. We still try it because
            // it'll occasionally find things our module scan misses.
            var files = new[] { driveLetter };
            rc = RmRegisterResources(handle, (uint)files.Length, files, 0, null, 0, null);
            if (rc != 0)
            {
                _log.Warn("DriveScanner", "RestartManager", "FAIL_REGISTER", $"rc={rc}");
                return;
            }

            // Two-call pattern: first call tells us how big a buffer we need.
            uint procInfoNeeded = 0;
            uint procInfo = 0;
            uint rebootReasons = 0;

            rc = RmGetList(handle, out procInfoNeeded, ref procInfo, null, ref rebootReasons);

            if (rc != 0 && rc != ERROR_MORE_DATA)
            {
                _log.Debug("DriveScanner", "RestartManager", "EMPTY", $"rc={rc}");
                return;
            }

            if (procInfoNeeded == 0)
            {
                _log.Debug("DriveScanner", "RestartManager", "NO_BLOCKERS", "");
                return;
            }

            // Second call with a buffer of the right size
            var procInfos = new RM_PROCESS_INFO[procInfoNeeded];
            procInfo = procInfoNeeded;
            rc = RmGetList(handle, out procInfoNeeded, ref procInfo, procInfos, ref rebootReasons);
            if (rc != 0)
            {
                _log.Warn("DriveScanner", "RestartManager", "FAIL_GETLIST", $"rc={rc}");
                return;
            }

            for (int i = 0; i < procInfo; i++)
            {
                ct.ThrowIfCancellationRequested();
                var pi = procInfos[i];
                int pid = pi.Process.dwProcessId;

                if (results.TryGetValue(pid, out var existing))
                {
                    // Strategy 1 already found this - just enrich the reason
                    if (!existing.Reason.Contains("Restart Manager"))
                        existing.Reason += $"; Restart Manager: {pi.strAppName}";
                }
                else
                {
                    results[pid] = new ProcessUsage
                    {
                        Pid         = pid,
                        ProcessName = string.IsNullOrEmpty(pi.strAppName) ? "(unknown)" : pi.strAppName,
                        Reason      = "Detected by Restart Manager"
                    };
                }

                _log.Debug("DriveScanner", "RestartManager", "MATCH",
                    $"pid={pid} name={pi.strAppName}");
            }
        }
        catch (Exception ex)
        {
            _log.Error("DriveScanner", "RestartManager", "EXCEPTION", $"error={ex.Message}");
        }
        finally
        {
            if (handle != 0)
            {
                try { RmEndSession(handle); } catch { /* nothing we can do */ }
            }
        }
    }

    // =========================================================================
    //  Helpers
    // =========================================================================

    /// <summary>Pretty-print a byte count as KB/MB/GB/TB.</summary>
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:F2} {units[unit]}";
    }
}
