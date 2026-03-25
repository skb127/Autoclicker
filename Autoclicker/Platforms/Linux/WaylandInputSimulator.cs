using System;
using Autoclicker.Services;

namespace Autoclicker.Platforms.Linux;

public class WaylandInputSimulator : IInputSimulator
{
    public void SimulateClick(int x, int y)
    {
        Console.WriteLine("Simulated click at X: " + x + ", Y: " + y);
    }
}