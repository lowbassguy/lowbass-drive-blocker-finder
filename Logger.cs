// =============================================================================
//  Title:   Drive Blocker Finder - Logger
//  Author:  Joshua "lowbass" Sommerfeldt
//  Date:    2026-05-14
//  Purpose: Thread-safe structured logger. Emits every line in the format:
//
//             [yyyy-MM-dd HH:mm:ss.fff] {emoji?} [LEVEL] [Component] [Action] [Outcome] details
//
//           - Writes to a rolling-per-launch file in %TEMP%\DriveBlockerFinder\
//           - Raises a LogEmitted event so the UI can display lines live
//           - Emojis are OPTIONAL (toggleable) and placed BEFORE the structured
//             segment so log parsers can strip them without breaking columns.
//           - 'Verbose' toggle suppresses DEBUG lines when off.
// =============================================================================

using System;
using System.IO;

namespace DriveBlockerFinder;

public sealed class Logger : IDisposable
{
    // Lazy singleton so the first .Info() call from anywhere "just works"
    private static readonly Lazy<Logger> _instance = new(() => new Logger());
    public static Logger Instance => _instance.Value;

    // The file we append to for this run. Null if we couldn't open one.
    private readonly StreamWriter? _fileWriter;

    // Lock that protects the file writer. Logger is hammered from many threads.
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>If false, DEBUG-level lines are dropped (default: ON for examples).</summary>
    public bool Verbose { get; set; } = true;

    /// <summary>If false, no emoji prefix is emitted (keeps plain-text alignment).</summary>
    public bool UseEmojis { get; set; } = true;

    /// <summary>Absolute path to the current log file, or null if not opened.</summary>
    public string? LogFilePath { get; }

    /// <summary>UI subscribes to this to mirror log lines into a TextBox/console.</summary>
    public event Action<string>? LogEmitted;

    // -----------------------------------------------------------------------
    // Private constructor - use Logger.Instance. Tries to open a per-run log
    // file under %TEMP%\DriveBlockerFinder\. If that fails we still work, just
    // without a file sink.
    // -----------------------------------------------------------------------
    private Logger()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "DriveBlockerFinder");
            Directory.CreateDirectory(dir);
            LogFilePath = Path.Combine(dir, $"log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            _fileWriter = new StreamWriter(LogFilePath, append: true) { AutoFlush = true };
        }
        catch
        {
            // Swallowed on purpose - logging must never crash the app
            _fileWriter = null;
            LogFilePath = null;
        }
    }

    // ---- Convenience methods, one per level ------------------------------
    // The emoji choices are deliberate and small. They're all in BMP + emoji
    // ranges so any modern Windows font renders them; if the user disables
    // emojis the lines still parse cleanly.

    public void Debug  (string component, string action, string outcome, string details = "")
        => Write("DEBUG",   "🔍", component, action, outcome, details, debugOnly: true);

    public void Info   (string component, string action, string outcome, string details = "")
        => Write("INFO",    "ℹ️", component, action, outcome, details);

    public void Warn   (string component, string action, string outcome, string details = "")
        => Write("WARN",    "⚠️", component, action, outcome, details);

    public void Error  (string component, string action, string outcome, string details = "")
        => Write("ERROR",   "❌", component, action, outcome, details);

    public void Success(string component, string action, string outcome, string details = "")
        => Write("OK",      "✅", component, action, outcome, details);

    // -----------------------------------------------------------------------
    // The actual writer. Builds the line, writes to the file under lock, then
    // raises the UI event OUTSIDE the lock so the UI thread can't deadlock us.
    // -----------------------------------------------------------------------
    private void Write(string level, string emoji, string component, string action,
                       string outcome, string details, bool debugOnly = false)
    {
        // Suppress DEBUG lines entirely when verbose is off
        if (debugOnly && !Verbose) return;

        // High-precision timestamp; fff = milliseconds. Consistent column width.
        var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        // Emoji prefix is optional and goes BEFORE the structured segment,
        // so stripping it doesn't shift any other column positions.
        var emojiPrefix = UseEmojis ? $"{emoji} " : "";

        var line = $"[{ts}] {emojiPrefix}[{level,-5}] [{component}] [{action}] [{outcome}] {details}"
                   .TrimEnd();

        // File write is the only thing that needs the lock
        lock (_lock)
        {
            if (_disposed) return;
            try { _fileWriter?.WriteLine(line); }
            catch { /* never let logging crash the app */ }
        }

        // Fire the UI event outside the lock; subscribers should marshal to UI thread
        try { LogEmitted?.Invoke(line); }
        catch { /* UI may already be torn down during shutdown */ }
    }

    // -----------------------------------------------------------------------
    // Disposal: flushes the file writer and marks the logger inert. Safe to
    // call multiple times (App.OnExit calls it, and the GC finalizer is a
    // last-resort fallback).
    // -----------------------------------------------------------------------
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _fileWriter?.Flush();
                _fileWriter?.Dispose();
            }
            catch { /* nothing useful to do here */ }
        }
    }
}
