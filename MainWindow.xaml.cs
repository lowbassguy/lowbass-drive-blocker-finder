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
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LowbassDriveBlockerFinder.Models;

namespace LowbassDriveBlockerFinder;

public partial class MainWindow : Window
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
