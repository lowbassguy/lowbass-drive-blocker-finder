// =============================================================================
//  Title:   lowbass' Drive Blocker Finder - Main Window Code-Behind
//  Author:  Joshua "lowbass" Sommerfeldt
//  Date:    2026-05-14
//  Purpose: Wires up the UI. Runs scans on a background task so the UI stays
//           responsive. Subscribes to Logger.LogEmitted to mirror lines into
//           the log panel. Handles graceful shutdown via Closing event:
//           cancels in-flight scans, unhooks events, lets App.OnExit flush.
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LowbassDriveBlockerFinder.Models;

namespace LowbassDriveBlockerFinder;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    // Service objects. Cheap to construct, no shared mutable state, no need for DI.
    private readonly DriveScanner _scanner = new();
    private readonly EjectService _ejector = new();

    // Bound to the DataGrid. ObservableCollection -> UI updates automatically.
    private readonly ObservableCollection<ProcessUsage> _blockers = new();

    // Token source for the current scan, if any. Cancelled on close or re-scan.
    private CancellationTokenSource? _cts;

    // Cap the log panel so it doesn't grow without bound during long sessions.
    private const int MaxLogLinesInUi = 2000;

    // Lock-free producer queue: Logger can fire from any thread, we drain on UI thread.
    private readonly ConcurrentQueue<string> _pendingLogLines = new();

    // UI-thread-only window of lines currently shown. Capped at MaxLogLinesInUi.
    private readonly Queue<string> _uiLines = new();

    // Batches log flushes so a verbose scan doesn't post hundreds of Dispatcher
    // jobs and force a TextBox layout per line.
    private DispatcherTimer? _logFlushTimer;

    public MainWindow()
    {
        InitializeComponent();

        // Bind the grid to our observable collection
        ProcessGrid.ItemsSource = _blockers;

        // Mirror every log line emitted by the Logger into our TextBox
        Logger.Instance.LogEmitted += OnLogEmitted;

        // Drain the log queue ~10 times a second. Background priority so genuine
        // UI work (clicks, scrolling) gets serviced first.
        _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _logFlushTimer.Tick += (_, _) => FlushLogLines();
        _logFlushTimer.Start();

        // Run our cleanup on window close
        Closing += MainWindow_Closing;

        Logger.Instance.Info("UI", "Startup", "READY", "MainWindow initialized");
        RefreshDrives();
    }

    // -------------------------------------------------------------------------
    //  Log mirroring
    // -------------------------------------------------------------------------

    private void OnLogEmitted(string line)
    {
        // Producer side - any thread. Just enqueue; the UI timer drains and renders.
        _pendingLogLines.Enqueue(line);
    }

    private void FlushLogLines()
    {
        if (_pendingLogLines.IsEmpty) return;

        // Drain everything queued at this instant
        var batch = new List<string>();
        while (_pendingLogLines.TryDequeue(out var line))
            batch.Add(line);

        bool overflowed = false;
        foreach (var line in batch)
        {
            _uiLines.Enqueue(line);
            while (_uiLines.Count > MaxLogLinesInUi)
            {
                _uiLines.Dequeue();
                overflowed = true;
            }
        }

        if (overflowed)
        {
            // Trim happened: rebuild the visible text from the in-memory window.
            // This O(N) cost only pays out when we cross the cap, not per line.
            LogBox.Text = string.Join(Environment.NewLine, _uiLines) + Environment.NewLine;
        }
        else
        {
            // Cheap append - layout invalidates once for the whole batch.
            LogBox.AppendText(string.Join(Environment.NewLine, batch) + Environment.NewLine);
        }

        LogBox.ScrollToEnd();
    }

    // -------------------------------------------------------------------------
    //  Drive list
    // -------------------------------------------------------------------------

    private void RefreshDrives()
    {
        var drives = _scanner.EnumerateDrives();
        DriveCombo.ItemsSource = drives;       // ToString() is what the ComboBox renders
        DriveCombo.SelectedIndex = drives.Count > 0 ? 0 : -1;
        StatusText.Text = $"Loaded {drives.Count} drive(s). Pick one and hit Scan.";
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshDrives();

    // -------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------

    /// <summary>Pulls "E:\" out of the selected combobox item.</summary>
    private string? GetSelectedDriveLetter()
    {
        return (DriveCombo.SelectedItem as DriveItem)?.DriveLetter;
    }

    // -------------------------------------------------------------------------
    //  Scan
    // -------------------------------------------------------------------------

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var driveLetter = GetSelectedDriveLetter();
        if (string.IsNullOrEmpty(driveLetter))
        {
            Logger.Instance.Warn("UI", "Scan", "NO_DRIVE", "user clicked Scan with no drive selected");
            return;
        }

        // Clear previous results & disable the action buttons while we work
        _blockers.Clear();
        SetActionsEnabled(false);
        StatusText.Text = $"Scanning {driveLetter}...";

        // Cancel any prior in-flight scan
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            // Run the scan off the UI thread - it touches every process and
            // can take a few seconds on busy systems
            var results = await Task.Run(
                () => _scanner.FindBlockingProcesses(driveLetter, token),
                token);

            foreach (var r in results) _blockers.Add(r);

            if (results.Count == 0)
            {
                StatusText.Text = $"✅ {driveLetter} looks clear. Should be safe to eject.";
                Logger.Instance.Success("UI", "Scan", "CLEAR",
                    $"drive={driveLetter} no blockers detected 🎉");
            }
            else
            {
                StatusText.Text = $"⚠ Found {results.Count} process(es) holding {driveLetter}.";
            }
        }
        catch (OperationCanceledException)
        {
            Logger.Instance.Warn("UI", "Scan", "CANCELLED", $"drive={driveLetter}");
            StatusText.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("UI", "Scan", "FAIL", ex.Message);
            StatusText.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        ScanButton.IsEnabled    = enabled;
        EjectButton.IsEnabled   = enabled;
        RefreshButton.IsEnabled = enabled;
    }

    // -------------------------------------------------------------------------
    //  Eject
    // -------------------------------------------------------------------------

    private async void EjectButton_Click(object sender, RoutedEventArgs e)
    {
        var driveLetter = GetSelectedDriveLetter();
        if (string.IsNullOrEmpty(driveLetter)) return;

        var confirm = MessageBox.Show(
            $"Lock, dismount, and eject {driveLetter}?\n\n" +
            "If anything is still using it, this will fail with no data loss.",
            "Confirm eject", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        // Run the kernel ioctls on a background thread - the LOCK call can block
        // for a noticeable moment on busy volumes, and we don't want the window
        // to go "Not Responding."
        SetActionsEnabled(false);
        StatusText.Text = $"Ejecting {driveLetter}...";

        EjectResult result;
        try
        {
            result = await Task.Run(() => _ejector.TryEject(driveLetter));
        }
        finally
        {
            SetActionsEnabled(true);
        }

        switch (result)
        {
            case EjectResult.Ok:
                StatusText.Text = $"✅ {driveLetter} ejected.";
                MessageBox.Show($"{driveLetter} ejected successfully ✅",
                    "Result", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshDrives();
                break;

            case EjectResult.StillInUse:
                StatusText.Text = $"⚠ {driveLetter} is still in use.";
                MessageBox.Show(
                    "Could not lock the volume — something is still holding it.\n\n" +
                    "Click Scan to see which processes have it open.",
                    "Still in use", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;

            case EjectResult.NotEjectable:
                StatusText.Text = $"ℹ {driveLetter} dismounted, but the device doesn't support physical eject.";
                MessageBox.Show(
                    $"{driveLetter} was dismounted successfully, but the device " +
                    "doesn't support a physical eject. This is normal for fixed " +
                    "disks, network shares, and optical drives.",
                    "Dismounted (no eject)", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshDrives();
                break;

            case EjectResult.OpenFailed:
                StatusText.Text = $"❌ Could not open {driveLetter}.";
                MessageBox.Show(
                    $"Could not open {driveLetter} for eject. The drive letter may " +
                    "be invalid or unavailable. Check the log for the Win32 error code.",
                    "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
                break;

            case EjectResult.DismountFailed:
                StatusText.Text = $"❌ Dismount failed for {driveLetter}.";
                MessageBox.Show(
                    "The volume was locked but dismount failed. Check the log for the " +
                    "Win32 error code.",
                    "Dismount failed", MessageBoxButton.OK, MessageBoxImage.Error);
                break;

            case EjectResult.Exception:
            default:
                StatusText.Text = "❌ Eject failed unexpectedly.";
                MessageBox.Show(
                    "Eject failed unexpectedly. See the log for details.",
                    "Eject failed", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }
    }

    // -------------------------------------------------------------------------
    //  Kill process
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    //  Per-file: show the window that's holding THIS specific file
    // -------------------------------------------------------------------------

    private void ShowFileWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not BlockingFile bf) return;

        try
        {
            using var p = Process.GetProcessById(bf.OwnerPid);

            // Enumerate the process's visible top-level windows and pick the best
            // match for this file. For Explorer in particular this is essential -
            // MainWindowHandle for explorer.exe is usually NOT a user-visible
            // Explorer window.
            IntPtr hwnd = FindBestWindowForFile(bf.OwnerPid, bf.Path);

            // Fall back to MainWindowHandle if nothing scored well
            if (hwnd == IntPtr.Zero) hwnd = p.MainWindowHandle;

            if (hwnd == IntPtr.Zero)
            {
                Logger.Instance.Warn("UI", "ShowWindow", "NO_WINDOW",
                    $"pid={bf.OwnerPid} name={p.ProcessName} has no visible main window");
                MessageBox.Show(
                    $"{p.ProcessName} doesn't have a visible window to show — " +
                    "it's likely a background service or has no UI.",
                    "No window", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BringWindowForward(hwnd);

            Logger.Instance.Success("UI", "ShowWindow", "DONE",
                $"pid={bf.OwnerPid} name={p.ProcessName} file={System.IO.Path.GetFileName(bf.Path)}");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("UI", "ShowWindow", "FAIL",
                $"pid={bf.OwnerPid} error={ex.Message}");
            MessageBox.Show($"Could not show window: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -------------------------------------------------------------------------
    //  Per-file: close just this kernel handle in the owner process
    // -------------------------------------------------------------------------

    private void CloseFileHandle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.Tag is not BlockingFile bf) return;

        if (!bf.CanCloseHandle)
        {
            // Strategy 1 finds (loaded DLLs/EXEs) don't have a real kernel handle
            // value - the only way to release a loaded module is to kill the process.
            MessageBox.Show(
                "This entry is a loaded module (DLL/EXE), not an open file handle. " +
                "There's no way to release it without terminating the process - " +
                "use the Kill button instead.",
                "Cannot close module", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Force-close this handle in PID {bf.OwnerPid}?\n\n" +
            $"File: {bf.Path}\n\n" +
            "WARNING: closing a handle out from under another process can cause it " +
            "to crash or behave unpredictably the next time it tries to use that " +
            "handle. Standard Windows tools (Task Manager, Resource Monitor) don't " +
            "expose this. Proceed only if you understand the risk.",
            "Confirm close handle", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        // DuplicateHandle with DUPLICATE_CLOSE_SOURCE - copies the handle into our
        // process AND closes the source. We then close our copy. Net effect: handle
        // is gone from the source process.
        IntPtr hSource = NativeMethods.OpenProcess(NativeMethods.PROCESS_DUP_HANDLE, false, (uint)bf.OwnerPid);
        if (hSource == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Logger.Instance.Error("UI", "CloseHandle", "FAIL_OPEN",
                $"pid={bf.OwnerPid} win32err={err}");
            MessageBox.Show(
                $"Could not open PID {bf.OwnerPid} (Win32 error {err}). " +
                "If this is a system service or process owned by another user, " +
                "you'll need to run this app as Administrator.",
                "Access denied", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            if (NativeMethods.DuplicateHandle(
                    hSource, bf.Handle,
                    NativeMethods.GetCurrentProcess(), out IntPtr dup,
                    0, false, NativeMethods.DUPLICATE_CLOSE_SOURCE))
            {
                NativeMethods.CloseHandle(dup);
                Logger.Instance.Success("UI", "CloseHandle", "DONE",
                    $"pid={bf.OwnerPid} file={bf.Path}");

                // Update the UI - find this entry and remove it from its parent's list
                foreach (var proc in _blockers)
                {
                    if (proc.Pid == bf.OwnerPid)
                    {
                        proc.BlockingFiles.Remove(bf);
                        // If the process has no remaining files, drop the whole row
                        if (proc.BlockingFiles.Count == 0)
                            _blockers.Remove(proc);
                        break;
                    }
                }
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                Logger.Instance.Error("UI", "CloseHandle", "FAIL_DUP",
                    $"pid={bf.OwnerPid} win32err={err}");
                MessageBox.Show(
                    $"DuplicateHandle failed (Win32 error {err}). The handle may " +
                    "already be closed or be a protected kernel object.",
                    "Close failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(hSource);
        }
    }

    // -------------------------------------------------------------------------
    //  Helpers - smart window finding + robust foreground
    // -------------------------------------------------------------------------

    // Enumerate the process's top-level visible windows and pick the one whose
    // title looks most relevant to the given file path. Returns IntPtr.Zero if
    // nothing scored above zero.
    private static IntPtr FindBestWindowForFile(int pid, string filePath)
    {
        string leaf       = System.IO.Path.GetFileName(filePath);
        string parentDir  = System.IO.Path.GetDirectoryName(filePath) ?? "";
        string parentLeaf = System.IO.Path.GetFileName(parentDir);
        string drive      = filePath.Length >= 2 ? filePath.Substring(0, 2) : "";

        IntPtr best = IntPtr.Zero;
        int bestScore = 0;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            // Same process?
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
            if (windowPid != (uint)pid) return true; // keep enumerating

            // Must be visible to be useful to the user
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len == 0) return true;

            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;

            // Score by how specific the match is. Higher is better.
            int score = 1; // baseline for "visible window with a title"
            if (title.IndexOf(filePath,  StringComparison.OrdinalIgnoreCase) >= 0) score = 100;
            else if (!string.IsNullOrEmpty(leaf)       && title.IndexOf(leaf,       StringComparison.OrdinalIgnoreCase) >= 0) score = 80;
            else if (!string.IsNullOrEmpty(parentLeaf) && title.IndexOf(parentLeaf, StringComparison.OrdinalIgnoreCase) >= 0) score = 50;
            else if (!string.IsNullOrEmpty(drive)      && title.IndexOf(drive,      StringComparison.OrdinalIgnoreCase) >= 0) score = 20;

            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }
            return true;
        }, IntPtr.Zero);

        return best;
    }

    // SetForegroundWindow alone is blocked by Windows' focus-stealing rules in
    // many cases (especially for explorer.exe). The reliable workaround is to
    // briefly attach our input queue to the target window's thread - that gives
    // us the right to set foreground, BringWindowToTop kicks the window up, and
    // then we detach. Used by every "focus stealer" in the wild.
    private static void BringWindowForward(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        else
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);

        uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        uint ourThread    = NativeMethods.GetCurrentThreadId();

        if (targetThread == 0 || targetThread == ourThread)
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
            return;
        }

        bool attached = NativeMethods.AttachThreadInput(ourThread, targetThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                NativeMethods.AttachThreadInput(ourThread, targetThread, false);
        }
    }

    private void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        // The Tag binding gives us the PID without needing the row's DataContext
        if (sender is not Button btn) return;
        if (btn.Tag is not int pid) return;

        var confirm = MessageBox.Show(
            $"Force-kill PID {pid}?\n\nThis may cause data loss in that process.",
            "Confirm kill", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            // Process is IDisposable - using ensures the handle is released
            using var p = Process.GetProcessById(pid);
            p.Kill();
            Logger.Instance.Success("UI", "KillProcess", "DONE", $"pid={pid} name={p.ProcessName}");

            // Remove the row from the grid so the user sees instant feedback
            var item = _blockers.FirstOrDefault(b => b.Pid == pid);
            if (item != null) _blockers.Remove(item);
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("UI", "KillProcess", "FAIL", $"pid={pid} error={ex.Message}");
            MessageBox.Show($"Failed to kill process: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // -------------------------------------------------------------------------
    //  Log panel buttons
    // -------------------------------------------------------------------------

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        // Record the action to the file *before* we nuke UI state. We then drop
        // anything still in the queue (including the line we just emitted) so the
        // panel ends genuinely empty instead of containing a single "cleared" line.
        Logger.Instance.Info("UI", "ClearLog", "DONE", "cleared in-UI log buffer (file unaffected)");

        while (_pendingLogLines.TryDequeue(out _)) { }
        _uiLines.Clear();
        LogBox.Clear();
    }

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        var path = Logger.Instance.LogFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show("No log file is available.", "Log file",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            // UseShellExecute=true means Windows picks the default text editor
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("UI", "OpenLogFile", "FAIL", ex.Message);
        }
    }

    // -------------------------------------------------------------------------
    //  Toggles
    // -------------------------------------------------------------------------

    private void Verbose_Toggle(object sender, RoutedEventArgs e)
    {
        // The handler runs before InitializeComponent finishes if XAML sets defaults,
        // so guard against null Logger access - except it's a singleton, so safe.
        Logger.Instance.Verbose = VerboseCheck.IsChecked == true;
        Logger.Instance.Info("UI", "VerboseToggle", "CHANGED",
            $"verbose={Logger.Instance.Verbose}");
    }

    private void Emoji_Toggle(object sender, RoutedEventArgs e)
    {
        Logger.Instance.UseEmojis = EmojiCheck.IsChecked == true;
        Logger.Instance.Info("UI", "EmojiToggle", "CHANGED",
            $"emojis={Logger.Instance.UseEmojis}");
    }

    // -------------------------------------------------------------------------
    //  Graceful shutdown
    // -------------------------------------------------------------------------

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        Logger.Instance.Info("UI", "Shutdown", "CLOSING", "user closed window");

        // Cancel any in-flight scan so the Task can clean up promptly
        try { _cts?.Cancel(); } catch { }

        // Emit the friendly goodbye BEFORE unsubscribing so it makes it into the
        // visible log panel - otherwise the line only lands in the file sink.
        Logger.Instance.Success("UI", "Shutdown", "BYE",
            "👋 thanks for using lowbass' Drive Blocker Finder!");

        // One last manual flush so the goodbye renders before the window tears down
        FlushLogLines();

        // Stop the timer and unhook the log subscription so we don't try to write
        // to a torn-down TextBox during App.OnExit's final logging.
        _logFlushTimer?.Stop();
        Logger.Instance.LogEmitted -= OnLogEmitted;
        // App.OnExit will fire next and flush the log file
    }
}

// =============================================================================
//  Native methods used only by the UI (window focus / minimize state + per-handle
//  close). Kept here rather than in a service class because they're UI concerns.
// =============================================================================
internal static class NativeMethods
{
    public const int SW_SHOW    = 5;   // Show window in current size and position
    public const int SW_RESTORE = 9;   // Un-minimize / un-maximize back to "normal"

    // For DuplicateHandle - DUPLICATE_CLOSE_SOURCE closes the source handle as a
    // side effect of the duplicate. That's the documented way to forcibly close
    // a handle in another process.
    public const uint PROCESS_DUP_HANDLE    = 0x0040;
    public const uint DUPLICATE_CLOSE_SOURCE = 0x00000001;
    public const uint DUPLICATE_SAME_ACCESS  = 0x00000002;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle, out IntPtr lpTargetHandle, uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);
}
