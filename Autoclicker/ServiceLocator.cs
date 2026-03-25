using System;
using Autoclicker.Platforms.Linux;
using Autoclicker.Platforms.Windows;
using Autoclicker.Services;
using Autoclicker.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Autoclicker;

public class ServiceLocator
{
    public static IServiceProvider Provider { get; private set; }
    
    public static void ConfigureServices()
    {
        var services = new ServiceCollection();

        // 1. Register our ViewModels
        services.AddTransient<MainViewModel>();

        // 2. Register native implementations depending on the OS
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ICoordinatePicker, Win32CoordinatePicker>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<ICoordinatePicker, WaylandCoordinatePicker>();
            services.AddSingleton<IInputSimulator, WaylandInputSimulator>();
            services.AddSingleton<IHotkeyListener, WaylandHotkeyListener>();
        }

        Provider = services.BuildServiceProvider();
    }
}