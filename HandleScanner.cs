// =============================================================================
//  Title:   lowbass' Drive Blocker Finder - HandleScanner
//  Author:  Joshua "lowbass" Sommerfeldt
//  Date:    2026-05-14
//  Purpose: Strategy 3 - enumerate every open kernel HANDLE in the system, find
//           the ones pointing at files on the target drive, and report which
//           process owns them. This is what catches "Notepad has a .txt open
//           on the USB" - the case the module-scan strategy can't see.
//
//           Pipeline:
//             1. NtQuerySystemInformation(SystemExtendedHandleInformation)
//                -> array of (pid, handle, type-index) for every handle
//             2. Filter to handles of type "File" (figured out at runtime by
//                probing our own log-file handle)
//             3. DuplicateHandle the handle into THIS process so we can ask
//                Windows what it points at
//             4. NtQueryObject(NAME) -> "\Device\HarddiskVolume3\Pictures\..."
//                (run on a worker thread with a 100ms timeout - this call
//                can hang forever on certain handle types, mostly named pipes)
//             5. Translate "\Device\HarddiskVolume3" -> "D:\" via QueryDosDevice
//             6. Filter to paths on the target drive
//
//           Caveats:
//             - Without admin, OpenProcess(PROCESS_DUP_HANDLE) is denied for
//               processes owned by other users / SYSTEM. We silently skip
//               those - the user sees handles owned by their own processes.
//             - If a NtQueryObject call hangs, the worker thread is abandoned
//               and a fresh one is created for the next query. The abandoned
//               thread will exit when (if) NtQueryObject returns.
// =============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LowbassDriveBlockerFinder.Models;

namespace LowbassDriveBlockerFinder;

public sealed class HandleScanner
{
    private readonly Logger _log = Logger.Instance;

    // \Device\HarddiskVolume3 -> D:\ (case-insensitive)
    private readonly Dictionary<string, string> _deviceToDrive;

    // Filled in lazily on first scan by probing our own log-file handle
    private ushort? _fileTypeIndex;

    // Worker thread used to call NtQueryObject with a timeout. Re-created
    // any time a query hangs (the old one stays stuck until the OS unblocks it).
    private QueryWorker _worker = new();

    public HandleScanner()
    {
        _deviceToDrive = BuildDeviceMap();
    }

    // =========================================================================
    //  Public API
    // =========================================================================

    /// <summary>
    /// Returns a dictionary keyed by PID, with each value being the list of
    /// BlockingFile entries (path + source handle) that this process has open
    /// against the target drive. Carries the source-process handle value so the
    /// UI can offer a per-handle "Close" action.
    /// </summary>
    public Dictionary<int, List<BlockingFile>> FindHandlesOnDrive(string driveLetter, CancellationToken ct)
    {
        _log.Info("HandleScanner", "FindHandles", "START", $"drive={driveLetter}");

        // Normalize "E:\" / "E:" to "E:\"
        string drivePrefix = driveLetter.TrimEnd('\\');
        if (!drivePrefix.EndsWith(":")) drivePrefix += ":";
        drivePrefix += "\\";

        var results = new Dictionary<int, List<BlockingFile>>();

        // Step 1+2: get all handles in the system, optionally with a probe-file
        // handle open BEFORE the snapshot (only on first scan, to learn which
        // ObjectTypeIndex means "File"). On subsequent scans we just snapshot.
        SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX[] handles;
        try
        {
            if (_fileTypeIndex == null)
            {
                // Open the probe FIRST so its handle is included in the snapshot.
                using var probe = TryOpenTypeProbe();
                IntPtr probeHandle = probe?.SafeFileHandle?.DangerousGetHandle() ?? IntPtr.Zero;

                handles = QueryAllSystemHandles();

                if (probeHandle != IntPtr.Zero)
                {
                    int ourPidForProbe = Environment.ProcessId;
                    foreach (var h in handles)
                    {
                        if ((int)h.UniqueProcessId.ToInt64() == ourPidForProbe &&
                            h.HandleValue == probeHandle)
                        {
                            _fileTypeIndex = h.ObjectTypeIndex;
                            break;
                        }
                    }
                }

                if (_fileTypeIndex == null)
                {
                    _log.Warn("HandleScanner", "TypeIndex", "UNKNOWN",
                        "could not determine File type index; scanning all handles (slower)");
                }
                else
                {
                    _log.Debug("HandleScanner", "TypeIndex", "DETERMINED",
                        $"fileTypeIndex={_fileTypeIndex}");
                }
            }
            else
            {
                handles = QueryAllSystemHandles();
            }
        }
        catch (Exception ex)
        {
            _log.Error("HandleScanner", "QuerySystem", "FAIL", $"error={ex.Message}");
            return results;
        }
        _log.Debug("HandleScanner", "QuerySystem", "GOT", $"handle_count={handles.Length}");

        // Step 3: walk handles, grouped by PID so we can amortize the OpenProcess call
        int ourPid = Environment.ProcessId;
        IntPtr ourProcess = GetCurrentProcess();

        // Stats for the final log line
        int processesInspected = 0, processesDenied = 0, handlesQueried = 0;
        int handlesHung = 0, handlesNamed = 0, handlesMatched = 0;

        // Group on the fly to avoid a LINQ allocation for huge handle counts
        var byPid = new Dictionary<int, List<int>>(); // pid -> indices into `handles`
        for (int i = 0; i < handles.Length; i++)
        {
            // Filter by type if we know the File index; otherwise keep everything
            if (_fileTypeIndex.HasValue && handles[i].ObjectTypeIndex != _fileTypeIndex.Value)
                continue;

            int pid = (int)handles[i].UniqueProcessId.ToInt64();
            if (pid == 0 || pid == 4) continue; // System Idle and System - can't query these

            if (!byPid.TryGetValue(pid, out var list))
            {
                list = new List<int>();
                byPid[pid] = list;
            }
            list.Add(i);
        }

        foreach (var kv in byPid)
        {
            ct.ThrowIfCancellationRequested();

            int pid = kv.Key;
            var indices = kv.Value;

            // Open the source process so we can DuplicateHandle out of it
            IntPtr hSource = OpenProcess(PROCESS_DUP_HANDLE, false, (uint)pid);
            if (hSource == IntPtr.Zero)
            {
                // Almost always ERROR_ACCESS_DENIED for processes we don't own
                processesDenied++;
                continue;
            }
            processesInspected++;

            try
            {
                foreach (int idx in indices)
                {
                    handlesQueried++;
                    IntPtr srcHandle = handles[idx].HandleValue;

                    // Step 4a: copy the handle into our process
                    if (!DuplicateHandle(
                            hSource, srcHandle,
                            ourProcess, out IntPtr dupHandle,
                            0, false, DUPLICATE_SAME_ACCESS))
                    {
                        continue;
                    }

                    try
                    {
                        // Step 4b: ask Windows what file the handle points to.
                        // Worker thread + 100ms timeout protects against the
                        // (rare but real) case where this call hangs forever.
                        string? ntPath = QueryNameWithTimeout(dupHandle, timeoutMs: 100);

                        if (ntPath == null)
                        {
                            handlesHung++;
                            continue;
                        }
                        if (ntPath.Length == 0) continue;
                        handlesNamed++;

                        // Step 5: translate \Device\HarddiskVolumeN\... -> drive-letter path
                        string? dosPath = TranslateDevicePath(ntPath);
                        if (dosPath == null) continue;

                        // Step 6: is it on the target drive?
                        if (!dosPath.StartsWith(drivePrefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        handlesMatched++;
                        if (!results.TryGetValue(pid, out var fileList))
                        {
                            fileList = new List<BlockingFile>();
                            results[pid] = fileList;
                        }
                        // Tiny per-process dedup - one process can have the same file
                        // open multiple times (think: tabs of the same file in an editor).
                        // Dedup by path so the user sees one row per file, but remember
                        // we'd lose handle-close granularity for duplicates. Acceptable
                        // trade for cleaner UI.
                        bool alreadyTracked = false;
                        foreach (var existing in fileList)
                        {
                            if (string.Equals(existing.Path, dosPath, StringComparison.OrdinalIgnoreCase))
                            {
                                alreadyTracked = true;
                                break;
                            }
                        }
                        if (!alreadyTracked)
                        {
                            fileList.Add(new BlockingFile
                            {
                                Path     = dosPath,
                                OwnerPid = pid,
                                Handle   = srcHandle  // value in the SOURCE process
                            });
                        }
                    }
                    finally
                    {
                        CloseHandle(dupHandle);
                    }
                }
            }
            finally
            {
                CloseHandle(hSource);
            }
        }

        _log.Info("HandleScanner", "FindHandles", "DONE",
            $"pids_inspected={processesInspected} pids_denied={processesDenied} " +
            $"handles_queried={handlesQueried} named={handlesNamed} hung={handlesHung} " +
            $"matched={handlesMatched} blocking_pids={results.Count}");

        if (processesDenied > 0 && processesInspected < processesDenied)
        {
            _log.Warn("HandleScanner", "Access", "PARTIAL",
                $"denied for {processesDenied} processes - run as Administrator " +
                "to see handles owned by other users / system services");
        }

        return results;
    }

    // =========================================================================
    //  Step 1 - enumerate every handle in the system
    // =========================================================================

    private static SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX[] QueryAllSystemHandles()
    {
        // Start with 1 MB; if too small the call returns STATUS_INFO_LENGTH_MISMATCH
        // and we double + retry. 10 attempts is way more than we'd ever need.
        int size = 1 << 20;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            int returnedLen = 0;
            int status = 0;

            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);

                status = NtQuerySystemInformation(
                    SystemExtendedHandleInformation,
                    buffer, size, out returnedLen);

                if (status == 0) break;
                if ((uint)status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    // returnedLen is a hint at the required size; pad it a bit because
                    // new handles may open between our two calls.
                    size = returnedLen > 0 ? returnedLen + (64 * 1024) : size * 2;
                    continue;
                }
                throw new Win32Exception($"NtQuerySystemInformation failed: 0x{status:X8}");
            }

            if (status != 0)
                throw new Win32Exception($"NtQuerySystemInformation gave up after retries: 0x{status:X8}");

            // Buffer layout: [IntPtr NumberOfHandles][IntPtr Reserved][entries...]
            long n = Marshal.ReadIntPtr(buffer).ToInt64();
            var entries = new SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX[n];

            int structSize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
            IntPtr p = IntPtr.Add(buffer, IntPtr.Size * 2);

            for (long i = 0; i < n; i++)
            {
                entries[i] = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(p);
                p = IntPtr.Add(p, structSize);
            }

            return entries;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    // =========================================================================
    //  Step 2 - probe file used to learn which ObjectTypeIndex means "File"
    //
    //  ObjectTypeIndex is just a small integer assigned by the kernel at boot.
    //  The number for "File" is stable for the boot session but can change
    //  across boots / Windows versions, so we determine it dynamically by
    //  opening a known file, snapshotting the system handle table, and reading
    //  the type index off our probe's entry.
    //
    //  Critical: the caller MUST open the probe BEFORE snapshotting the handle
    //  table, or the probe's handle won't be in the snapshot.
    // =========================================================================

    private static FileStream? TryOpenTypeProbe()
    {
        string probePath = Logger.Instance.LogFilePath
            ?? Path.Combine(Path.GetTempPath(), "lowbass-drive-blocker-finder-probe.tmp");
        try
        {
            return File.Open(probePath, FileMode.OpenOrCreate,
                FileAccess.Read, FileShare.ReadWrite);
        }
        catch
        {
            return null;
        }
    }

    // =========================================================================
    //  Step 5 - device-name <-> drive-letter map
    //
    //  QueryDosDevice("C:") returns something like "\Device\HarddiskVolume3".
    //  We invert that map so we can translate an NT path back to a DOS path.
    // =========================================================================

    private static Dictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            // drive.Name is like "C:\" - we want "C:" for QueryDosDevice
            string letter = drive.Name.TrimEnd('\\');
            var sb = new StringBuilder(1024);
            if (QueryDosDevice(letter, sb, sb.Capacity) > 0)
            {
                map[sb.ToString()] = letter + "\\";
            }
        }
        return map;
    }

    private string? TranslateDevicePath(string ntPath)
    {
        foreach (var kv in _deviceToDrive)
        {
            // Need the trailing "\" check so "\Device\HarddiskVolume1" doesn't
            // accidentally match "\Device\HarddiskVolume10".
            if (ntPath.Length > kv.Key.Length &&
                ntPath[kv.Key.Length] == '\\' &&
                ntPath.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value + ntPath.Substring(kv.Key.Length + 1);
            }
        }
        return null;
    }

    // =========================================================================
    //  Step 4b - NtQueryObject(NAME) with a hang-safe timeout
    // =========================================================================

    private string? QueryNameWithTimeout(IntPtr dupHandle, int timeoutMs)
    {
        if (!_worker.IsAlive)
        {
            _worker = new QueryWorker();
        }
        var (completed, name) = _worker.Query(dupHandle, timeoutMs);
        if (!completed)
        {
            // The worker is stuck inside NtQueryObject. We can't safely close
            // dupHandle from here (the worker still references it) and we can't
            // safely terminate the thread either. Abandon both and move on -
            // they'll get cleaned up by the OS when the call eventually returns
            // or the process exits.
            _worker.Abandon();
            _worker = new QueryWorker();
            return null;
        }
        return name;
    }

    /// <summary>
    /// Long-lived worker that calls NtQueryObject on one handle at a time. The
    /// main thread can wait with a timeout and abandon the worker if it hangs.
    /// </summary>
    private sealed class QueryWorker
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _done = new(false);
        private IntPtr _handle;
        private string? _result;
        private volatile bool _alive = true;
        private volatile bool _abandoned;

        public QueryWorker()
        {
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "HandleScanner.QueryWorker"
            };
            _thread.Start();
        }

        public bool IsAlive => _alive && !_abandoned;

        public (bool Completed, string? Name) Query(IntPtr handle, int timeoutMs)
        {
            _handle = handle;
            _result = null;
            _done.Reset();
            _ready.Set();

            bool completed = _done.Wait(timeoutMs);
            return (completed, completed ? _result : null);
        }

        public void Abandon()
        {
            // Worker is stuck inside the P/Invoke call. We can't unstick it -
            // mark it so it knows not to touch shared state if it ever finishes.
            _abandoned = true;
            _alive = false;
        }

        private void Loop()
        {
            while (_alive)
            {
                _ready.Wait();
                _ready.Reset();
                if (!_alive) return;

                string? local = null;
                try { local = QueryNameNative(_handle); }
                catch { /* ignored - any failure means "no name" */ }

                if (_abandoned)
                {
                    // Main thread already gave up on us. Don't touch _result -
                    // a new worker owns that slot now.
                    return;
                }

                _result = local;
                _done.Set();
            }
        }
    }

    // The actual P/Invoke wrapper for NtQueryObject(NAME). Allocates a buffer,
    // calls the API, parses the UNICODE_STRING result.
    private static string? QueryNameNative(IntPtr handle)
    {
        int size = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            int status = NtQueryObject(
                handle, ObjectInformationClass.ObjectNameInformation,
                buffer, size, out int returnedLen);

            // Buffer too small? Resize once and retry.
            if ((uint)status == STATUS_INFO_LENGTH_MISMATCH ||
                (uint)status == STATUS_BUFFER_OVERFLOW)
            {
                size = returnedLen > 0 ? returnedLen : size * 2;
                Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
                status = NtQueryObject(
                    handle, ObjectInformationClass.ObjectNameInformation,
                    buffer, size, out _);
            }

            if (status != 0) return null;

            // The result is an OBJECT_NAME_INFORMATION at the start of the buffer,
            // which is a UNICODE_STRING. Its Buffer field points further into our
            // same allocation.
            var us = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
            if (us.Length == 0 || us.Buffer == IntPtr.Zero) return string.Empty;
            return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // =========================================================================
    //  Native types
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    private enum ObjectInformationClass
    {
        ObjectBasicInformation = 0,
        ObjectNameInformation  = 1,
        ObjectTypeInformation  = 2
    }

    // =========================================================================
    //  Native constants
    // =========================================================================

    private const int SystemExtendedHandleInformation = 64;

    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    private const uint STATUS_BUFFER_OVERFLOW      = 0x80000005;

    private const uint PROCESS_DUP_HANDLE   = 0x0040;
    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

    // =========================================================================
    //  P/Invoke surface
    // =========================================================================

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformation,
        int SystemInformationLength,
        out int ReturnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(
        IntPtr Handle,
        ObjectInformationClass ObjectInformationClass,
        IntPtr ObjectInformation,
        int ObjectInformationLength,
        out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(
        string lpDeviceName,
        StringBuilder lpTargetPath,
        int ucchMax);
}
