using System;
using Autoclicker.Platforms.Common;
using Autoclicker.Platforms.Linux;
using Autoclicker.Platforms.Windows;
using Autoclicker.Services;
using Autoclicker.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Autoclicker;

public sealed class ServiceLocator
{
    public static IServiceProvider? Provider { get; private set; }

    public static void ConfigureServices()
    {
        var services = new ServiceCollection();

        // 1. Register logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: false);
        });

        // 2. Register cross-platform application services and ViewModels
        services.AddSingleton<ISettingsStore, SettingsStore>();
        services.AddSingleton<IScreenBoundsProvider, AvaloniaScreenBoundsProvider>();
        services.AddTransient<MainViewModel>();

        string? linuxSessionType = null;

        // 3. Register native implementations depending on the OS
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ICoordinatePicker, CrossPlatformCoordinatePicker>();
            services.AddSingleton<IInputSimulator, Win32InputSimulator>();
            services.AddSingleton<IHotkeyListener, Win32HotkeyListener>();
        }
        else if (OperatingSystem.IsLinux())
        {
            // Read the standard Linux environment variable to detect the graphics server
            linuxSessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.ToLowerInvariant() ?? "";

            if (linuxSessionType == "wayland")
            {
                // Wayland: D-Bus-based implementations with hybrid hotkey strategy
                services.AddSingleton<WaylandDbusStopService>();
                services.AddSingleton<ICoordinatePicker, CrossPlatformCoordinatePicker>();
                services.AddSingleton<IInputSimulator, WaylandInputSimulator>();
                services.AddSingleton<IHotkeyListener, WaylandHotkeyListener>();
            }
            else
            {
                // X11: the transparent window approach works cleanly
                services.AddSingleton<ICoordinatePicker, CrossPlatformCoordinatePicker>();
                services.AddSingleton<IInputSimulator, X11InputSimulator>();
                services.AddSingleton<IHotkeyListener, X11HotkeyListener>();
            }
        }

        Provider = services.BuildServiceProvider();

        var logger = Provider.GetRequiredService<ILogger<ServiceLocator>>();
        if (OperatingSystem.IsWindows())
        {
            logger.LogInformation(
                "Configured native services for Windows (InputSimulator={InputSimulator}, HotkeyListener={HotkeyListener})",
                nameof(Win32InputSimulator),
                nameof(Win32HotkeyListener));
        }
        else if (OperatingSystem.IsLinux())
        {
            if (linuxSessionType == "wayland")
            {
                logger.LogInformation(
                    "Configured native services for Linux Wayland (XDG_SESSION_TYPE={SessionType}, InputSimulator={InputSimulator}, HotkeyListener={HotkeyListener})",
                    linuxSessionType,
                    nameof(WaylandInputSimulator),
                    nameof(WaylandHotkeyListener));
            }
            else
            {
                logger.LogInformation(
                    "Configured native services for Linux X11 (XDG_SESSION_TYPE={SessionType}, InputSimulator={InputSimulator}, HotkeyListener={HotkeyListener})",
                    string.IsNullOrEmpty(linuxSessionType) ? "(unset)" : linuxSessionType,
                    nameof(X11InputSimulator),
                    nameof(X11HotkeyListener));
            }
        }
    }
}
