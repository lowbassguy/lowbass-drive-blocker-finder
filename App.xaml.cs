// =============================================================================
//  Title:   Drive Blocker Finder - App Code-Behind
//  Author:  Joshua "lowbass" Sommerfeldt
//  Date:    2026-05-14
//  Purpose: Top-level WPF Application class. Hooks global exception handlers and
//           ensures the Logger flushes on app exit. This is our "signal handler"
//           equivalent on Windows GUI - SIGINT/SIGTERM don't apply the same way,
//           but window-close, app-exit, and crashes all funnel through here.
// =============================================================================

using System;
using System.Windows;
using System.Windows.Threading;

namespace DriveBlockerFinder;

public partial class App : Application
{
    // ---------------------------------------------------------------------
    // OnStartup: wire global exception handlers BEFORE any window appears,
    // so a crash during MainWindow construction still gets logged.
    // ---------------------------------------------------------------------
    protected override void OnStartup(StartupEventArgs e)
    {
        // Catches background-thread exceptions that would otherwise crash silently
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Catches UI-thread exceptions; we mark Handled=true to keep the app alive
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Catches unobserved Task exceptions (fire-and-forget gone wrong)
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException +=
            (s, args) =>
            {
                Logger.Instance.Error("App", "UnobservedTask", "CAUGHT",
                    $"exception={args.Exception.Message}");
                args.SetObserved(); // Don't let it tear down the process
            };

        Logger.Instance.Info("App", "Startup", "READY",
            $"version=1.0.0 process={Environment.ProcessId} user={Environment.UserName}");

        base.OnStartup(e);
    }

    // ---------------------------------------------------------------------
    // Background-thread crashes. By the time this fires, the runtime is
    // usually already tearing things down, so we just log what we can.
    // ---------------------------------------------------------------------
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Logger.Instance.Error("App", "UnhandledException", "CRASH",
            $"terminating={e.IsTerminating} exception={ex?.GetType().Name} msg={ex?.Message}");
    }

    // ---------------------------------------------------------------------
    // UI-thread exception handler. We mark Handled=true so the app survives
    // (most of the time). User sees a polite dialog rather than a crash.
    // ---------------------------------------------------------------------
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Instance.Error("App", "DispatcherException", "CAUGHT",
            $"exception={e.Exception.GetType().Name} msg={e.Exception.Message}");

        MessageBox.Show(
            $"Something went sideways:\n\n{e.Exception.Message}\n\nThe app will try to continue.",
            "Unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }

    // ---------------------------------------------------------------------
    // OnExit: centralized shutdown hook. Flushes logs and releases the
    // Logger's file handle. This runs on normal exit AND on most close paths.
    // ---------------------------------------------------------------------
    protected override void OnExit(ExitEventArgs e)
    {
        Logger.Instance.Success("App", "Shutdown", "GOODBYE",
            "👋 Flushing logs and releasing resources - see you next time!");
        Logger.Instance.Dispose();
        base.OnExit(e);
    }
}
