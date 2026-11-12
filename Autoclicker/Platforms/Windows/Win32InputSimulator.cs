using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autoclicker.Core;
using Autoclicker.Services;

namespace Autoclicker.Platforms.Windows;

public sealed partial class Win32InputSimulator : IInputSimulator
{
    [Flags]
    public enum MouseEventFlags : uint
    {
        Move = 0x0001,
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        RightDown = 0x0008,
        RightUp = 0x0010,
        MiddleDown = 0x0020,
        MiddleUp = 0x0040,
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int smIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint lpPoint);

    private int _lastX;
    private int _lastY;
    private bool _hasLastPosition;

    public Task InitializeAsync()
    {
        // Windows does not require negotiating asynchronous interactive permissions like Wayland
        return Task.CompletedTask;
    }

    public Task MoveToAsync(int x, int y)
    {
        int screenWidth = Math.Max(1, GetSystemMetrics(0));  // SM_CXSCREEN
        int screenHeight = Math.Max(1, GetSystemMetrics(1)); // SM_CYSCREEN

        // Normalized absolute coordinates span 0..65535 across the primary display
        int absoluteX = (int)Math.Round(x * 65535.0 / screenWidth);
        int absoluteY = (int)Math.Round(y * 65535.0 / screenHeight);

        Input[] inputs = new Input[1];
        inputs[0].type = 0; // 0 = INPUT_MOUSE
        inputs[0].mi.dx = absoluteX;
        inputs[0].mi.dy = absoluteY;
        inputs[0].mi.dwFlags = MouseEventFlags.Move | MouseEventFlags.Absolute;

        SendInput(1, inputs, Marshal.SizeOf<Input>());

        _lastX = x;
        _lastY = y;
        _hasLastPosition = true;
        return Task.CompletedTask;
    }

    public Task ButtonAsync(MouseButtonKind button, bool isDown)
    {
        MouseEventFlags flag = (button, isDown) switch
        {
            (MouseButtonKind.Left, true) => MouseEventFlags.LeftDown,
            (MouseButtonKind.Left, false) => MouseEventFlags.LeftUp,
            (MouseButtonKind.Right, true) => MouseEventFlags.RightDown,
            (MouseButtonKind.Right, false) => MouseEventFlags.RightUp,
            (MouseButtonKind.Middle, true) => MouseEventFlags.MiddleDown,
            (MouseButtonKind.Middle, false) => MouseEventFlags.MiddleUp,
            _ => MouseEventFlags.LeftDown
        };

        Input[] inputs = new Input[1];
        inputs[0].type = 0; // 0 = INPUT_MOUSE
        inputs[0].mi.dwFlags = flag;

        SendInput(1, inputs, Marshal.SizeOf<Input>());
        return Task.CompletedTask;
    }

    public bool TryGetCursorPosition(out int x, out int y)
    {
        if (GetCursorPos(out var pt))
        {
            x = pt.X;
            y = pt.Y;
            return true;
        }

        x = _lastX;
        y = _lastY;
        return _hasLastPosition;
    }
}
