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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow()
    {
        InitializeComponent();

        // Bind the grid to our observable collection
        ProcessGrid.ItemsSource = _blockers;

        // Mirror every log line emitted by the Logger into our TextBox
        Logger.Instance.LogEmitted += OnLogEmitted;

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
        // Logger fires this on whatever thread did the log call. The TextBox
        // is UI-thread-only, so we marshal via the Dispatcher. BeginInvoke (not
        // Invoke) avoids any chance of cross-thread deadlocks.
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);

            // Trim old lines if we're over the cap
            if (LogBox.LineCount > MaxLogLinesInUi)
            {
                var lines = LogBox.Text.Split(Environment.NewLine);
                LogBox.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - MaxLogLinesInUi));
            }

            LogBox.ScrollToEnd();
        });
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

    private void EjectButton_Click(object sender, RoutedEventArgs e)
    {
        var driveLetter = GetSelectedDriveLetter();
        if (string.IsNullOrEmpty(driveLetter)) return;

        var confirm = MessageBox.Show(
            $"Lock, dismount, and eject {driveLetter}?\n\n" +
            "If anything is still using it, this will fail with no data loss.",
            "Confirm eject", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        bool ok = _ejector.TryEject(driveLetter);

        if (ok)
        {
            MessageBox.Show($"{driveLetter} ejected successfully ✅",
                "Result", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshDrives();
        }
        else
        {
            MessageBox.Show(
                "Could not eject. Most likely something is still holding the drive — " +
                "click Scan to see what.",
                "Could not eject", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        LogBox.Clear();
        Logger.Instance.Info("UI", "ClearLog", "DONE", "cleared in-UI log buffer (file unaffected)");
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

        // Unhook the log subscription so we don't try to write to a torn-down TextBox
        Logger.Instance.LogEmitted -= OnLogEmitted;

        Logger.Instance.Success("UI", "Shutdown", "BYE",
            "👋 thanks for using lowbass' Drive Blocker Finder!");
        // App.OnExit will fire next and flush the log file
    }
}
