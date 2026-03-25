using System;
using Autoclicker.Services;

namespace Autoclicker.Platforms.Linux;

public class WaylandHotkeyListener : IHotkeyListener
{
    // The event required by the interface
    public event Action? OnStopRequested;

    public void StartListening()
    {
        Console.WriteLine("Listening to keyboard...");
    }

    public void StopListening()
    {
        Console.WriteLine("Stopped listening to keyboard.");
    }
}