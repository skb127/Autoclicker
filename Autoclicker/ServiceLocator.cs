using System;
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
            services.AddSingleton<ICoordinatePicker, Win32CoordinatePicker>();
            services.AddSingleton<IInputSimulator, Win32InputSimulator>();
            services.AddSingleton<IHotkeyListener, Win32HotkeyListener>();
        }
        else if (OperatingSystem.IsLinux())
        {
            // We read the standard Linux environment variable to detect the graphics server
            var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")?.ToLower()?? "";

            if (sessionType == "wayland")
            {
                // If the user is using Wayland, we inject the secure D-Bus-based implementations (The implementation is not yet complete, this may be addressed in the future)
                services.AddSingleton<ICoordinatePicker, WaylandCoordinatePicker>();
                services.AddSingleton<IInputSimulator, WaylandInputSimulator>();
                services.AddSingleton<IHotkeyListener, WaylandHotkeyListener>();
            }
            else 
            {
                // If it's X11 (or any other fallback), we know the transparent window works perfectly
                services.AddSingleton<ICoordinatePicker, Win32CoordinatePicker>();
                services.AddSingleton<IInputSimulator, X11InputSimulator>();
                services.AddSingleton<IHotkeyListener, X11HotkeyListener>();
            }
        }

        Provider = services.BuildServiceProvider();
    }
}