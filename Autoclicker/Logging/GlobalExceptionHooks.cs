using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Serilog;

namespace Autoclicker.Logging;

/// <summary>
/// Registers process-wide and UI-dispatcher unhandled exception hooks that record crashes to the log.
/// </summary>
public static class GlobalExceptionHooks
{
    private static int _processHooksRegistered;
    private static int _uiHookRegistered;

    /// <summary>
    /// Attaches handlers for <see cref="AppDomain.UnhandledException"/> and
    /// <see cref="TaskScheduler.UnobservedTaskException"/>. Safe to call multiple times.
    /// </summary>
    public static void RegisterProcessHooks()
    {
        if (Interlocked.Exchange(ref _processHooksRegistered, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Attaches a handler for <see cref="Dispatcher.UnhandledException"/> on the Avalonia UI thread
    /// without marking exceptions as handled. Safe to call multiple times.
    /// </summary>
    public static void RegisterUiDispatcherHook()
    {
        if (Interlocked.Exchange(ref _uiHookRegistered, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.UnhandledException += OnUiThreadUnhandledException;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        ILogger logger = Log.ForContext(typeof(GlobalExceptionHooks));

        if (e.ExceptionObject is Exception exception)
        {
            if (e.IsTerminating)
            {
                logger.Fatal(exception, "Unhandled AppDomain exception (process terminating)");
            }
            else
            {
                logger.Error(exception, "Unhandled AppDomain exception");
            }
        }
        else
        {
            if (e.IsTerminating)
            {
                logger.Fatal("Unhandled non-Exception object thrown (process terminating): {ExceptionObject}", e.ExceptionObject);
            }
            else
            {
                logger.Error("Unhandled non-Exception object thrown: {ExceptionObject}", e.ExceptionObject);
            }
        }

        if (e.IsTerminating)
        {
            Log.CloseAndFlush();
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.ForContext(typeof(GlobalExceptionHooks))
            .Error(e.Exception, "Unobserved task exception");
    }

    private static void OnUiThreadUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.ForContext(typeof(GlobalExceptionHooks))
            .Error(e.Exception, "Unhandled UI dispatcher exception");
    }
}
