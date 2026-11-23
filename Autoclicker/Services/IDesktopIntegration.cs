using System.Threading.Tasks;
using Autoclicker.Core;

namespace Autoclicker.Services;

/// <summary>
/// Abstraction over desktop integration capabilities (clipboard, file manager / browser launcher,
/// theme switching, and window always-on-top state) so ViewModels remain platform- and UI-agnostic.
/// </summary>
public interface IDesktopIntegration
{
    /// <summary>Copies plain text to the system clipboard. Returns false if no clipboard is available.</summary>
    Task<bool> CopyTextAsync(string text);

    /// <summary>Opens a folder in the system file manager. Returns false on failure.</summary>
    Task<bool> OpenFolderAsync(string path);

    /// <summary>Opens an external URL in the user's default web browser. Returns false on failure.</summary>
    Task<bool> OpenUrlAsync(string url);

    /// <summary>Applies the requested color theme variant to the running application.</summary>
    void ApplyTheme(ThemePreference theme);

    /// <summary>Sets whether the main application window stays on top of other windows.</summary>
    void SetAlwaysOnTop(bool alwaysOnTop);
}

/// <summary>
/// No-op fallback implementation of <see cref="IDesktopIntegration"/> used when constructing
/// ViewModels outside a live desktop lifetime (e.g. unit tests that do not supply a fake).
/// </summary>
internal sealed class NullDesktopIntegration : IDesktopIntegration
{
    public static NullDesktopIntegration Instance { get; } = new();

    private NullDesktopIntegration()
    {
    }

    public Task<bool> CopyTextAsync(string text) => Task.FromResult(false);
    public Task<bool> OpenFolderAsync(string path) => Task.FromResult(false);
    public Task<bool> OpenUrlAsync(string url) => Task.FromResult(false);

    public void ApplyTheme(ThemePreference theme)
    {
    }

    public void SetAlwaysOnTop(bool alwaysOnTop)
    {
    }
}
