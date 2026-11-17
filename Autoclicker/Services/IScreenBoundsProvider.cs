using System.Collections.Generic;

namespace Autoclicker.Services;

/// <summary>
/// Physical pixel bounds of a connected display in the shared virtual desktop coordinate space.
/// </summary>
public readonly record struct ScreenArea(string Name, int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(int x, int y) =>
        x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// Provides the physical screen rectangles available in the current desktop session.
/// </summary>
public interface IScreenBoundsProvider
{
    IReadOnlyList<ScreenArea> GetScreens();
}
