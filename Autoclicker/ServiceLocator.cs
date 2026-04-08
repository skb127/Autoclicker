using System;
using Autoclicker.Platforms.Common;
using Autoclicker.Platforms.Linux;
using Autoclicker.Platforms.Windows;
using Autoclicker.Services;
using Autoclicker.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Autoclicker;

public sealed class ServiceLocator
{
    public static IServiceProvider? Provider { get; private set; }

    public static void ConfigureServices()
    {
        var services = new ServiceCollection();

        // 1. Register our ViewModels
        services.AddTransient<MainViewModel>();

        // 2. Register native implementations depending on the OS
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ICoordinatePicker, CrossPlatformCoordinatePicker>();
            services.AddSingleton<IInputSimulator, Win32InputSimulator>();
            services.AddSingleton<IHotkeyListener, Win32HotkeyListener>();
        }
        else if (OperatingSystem.IsLinux())
        {
            // We read the standard Linux environment variable to detect the graphics server
            var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.ToLower() ?? "";

            if (sessionType == "wayland")
            {
                // Wayland: D-Bus-based implementations with hybrid hotkey strategy
                services.AddSingleton<WaylandDbusStopService>();
                services.AddSingleton<ICoordinatePicker, CrossPlatformCoordinatePicker>();
                services.AddSingleton<IInputSimulator, WaylandInputSimulator>();
                services.AddSingleton<IHotkeyListener, WaylandHotkeyListener>();
            }
            else
            {
                // X11: the transparent window approach works perfectly
                services.AddSingleton<ICoordinatePicker, CrossPlatformCoordinatePicker>();
                services.AddSingleton<IInputSimulator, X11InputSimulator>();
                services.AddSingleton<IHotkeyListener, X11HotkeyListener>();
            }
        }

        Provider = services.BuildServiceProvider();
    }
}