using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autoclicker.Services;

namespace Autoclicker.Platforms.Windows;

public class Win32InputSimulator: IInputSimulator
{
    // --- Windows API definitions (P/Invoke) ---
    [Flags]
    public enum MouseEventFlags : uint
    {
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        Absolute = 0x8000
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public MouseEventFlags dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public MouseInput mi;
    }
   
    [DllImport( "user32.dll")]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);
    
    [DllImport( "user32.dll")]
    private static extern int GetSystemMetrics(int smIndex);

    // --- Our Interface Implementation ---

    public Task InitializeAsync()
    {
        // Windows doesn't require negotiating asynchronous interactive permissions like Wayland
        return Task.CompletedTask; 
    }

    public void SimulateClick(int x, int y)
    {
        // 1. Get the primary monitor size
        int screenWidth = GetSystemMetrics(0); // SM_CXSCREEN
        int screenHeight = GetSystemMetrics(1); // SM_CYSCREEN

        // 2. Convert pixels to normalized absolute coordinates (0 - 65535)
        int absoluteX = (x * 65536) / screenWidth;
        int absoluteY = (y * 65536) / screenHeight;

        // 3. Prepare the 3-instruction packet: Move, Press, Release
        Input[] inputs = new Input[3];

        // Instruction 1: Move the cursor
        inputs[0].type = 0; // 0 = INPUT_MOUSE
        inputs[0].mi.dx = absoluteX;
        inputs[0].mi.dy = absoluteY;
        inputs[0].mi.dwFlags = MouseEventFlags.Move | MouseEventFlags.Absolute;

        // Instruction 2: Press left button
        inputs[1].type = 0;
        inputs[1].mi.dwFlags = MouseEventFlags.LeftDown;

        // Instruction 3: Release left button
        inputs[2].type = 0;
        inputs[2].mi.dwFlags = MouseEventFlags.LeftUp;

        // 4. Inject events into the operating system
        SendInput(3, inputs, Marshal.SizeOf(typeof(Input)));
    }
}