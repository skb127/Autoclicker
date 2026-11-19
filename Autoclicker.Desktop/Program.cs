using System;
using Autoclicker.Logging;
using Avalonia;

namespace Autoclicker.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        AppLogging.Initialize();
        GlobalExceptionHooks.RegisterProcessHooks();

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLogging.LogUnhandledTermination(ex);
            throw;
        }
        finally
        {
            AppLogging.Shutdown();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { EnableIme = false })
            .WithInterFont()
            .LogToSerilog();
}