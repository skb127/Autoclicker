using System;
using System.IO;
using System.Threading.Tasks;
using Autoclicker.Core;
using Autoclicker.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Autoclicker.Platforms.Common;

/// <summary>
/// Avalonia-backed implementation of <see cref="IDesktopIntegration"/> using <see cref="TopLevel"/>
/// clipboard/launcher services, <see cref="Application.RequestedThemeVariant"/>, and <see cref="Window.Topmost"/>.
/// </summary>
public sealed class AvaloniaDesktopIntegration : IDesktopIntegration
{
    private readonly ILogger<AvaloniaDesktopIntegration> _logger;

    public AvaloniaDesktopIntegration(ILogger<AvaloniaDesktopIntegration>? logger = null)
    {
        _logger = logger ?? NullLogger<AvaloniaDesktopIntegration>.Instance;
    }

    public async Task<bool> CopyTextAsync(string text)
    {
        try
        {
            var clipboard = GetTopLevel()?.Clipboard;
            if (clipboard is null)
            {
                _logger.LogWarning("Cannot copy text to clipboard: TopLevel clipboard is unavailable");
                return false;
            }

            await clipboard.SetTextAsync(text ?? string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to copy text to clipboard");
            return false;
        }
    }

    public async Task<bool> OpenFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(path);
            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists)
            {
                _logger.LogWarning("Cannot open folder {FolderPath}: directory does not exist", path);
                return false;
            }

            var launcher = GetTopLevel()?.Launcher;
            if (launcher is null)
            {
                _logger.LogWarning("Cannot open folder {FolderPath}: TopLevel launcher is unavailable", path);
                return false;
            }

            bool launched = await launcher.LaunchDirectoryInfoAsync(dirInfo);
            if (!launched)
            {
                _logger.LogWarning("System launcher returned false when opening folder {FolderPath}", path);
            }

            return launched;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open folder {FolderPath}", path);
            return false;
        }
    }

    public async Task<bool> OpenUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        try
        {
            var launcher = GetTopLevel()?.Launcher;
            if (launcher is null)
            {
                _logger.LogWarning("Cannot open URL {Url}: TopLevel launcher is unavailable", url);
                return false;
            }

            bool launched = await launcher.LaunchUriAsync(uri);
            if (!launched)
            {
                _logger.LogWarning("System launcher returned false when opening URL {Url}", url);
            }

            return launched;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open URL {Url}", url);
            return false;
        }
    }

    public void ApplyTheme(ThemePreference theme)
    {
        try
        {
            if (Application.Current is not { } app)
            {
                return;
            }

            app.RequestedThemeVariant = theme switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply theme {Theme}", theme);
        }
    }

    public void SetAlwaysOnTop(bool alwaysOnTop)
    {
        try
        {
            if (GetMainWindow() is { } window)
            {
                window.Topmost = alwaysOnTop;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set AlwaysOnTop={AlwaysOnTop}", alwaysOnTop);
        }
    }

    private static Window? GetMainWindow()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }

    private static TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return TopLevel.GetTopLevel(window);
        }

        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime { MainView: { } view })
        {
            return TopLevel.GetTopLevel(view);
        }

        return null;
    }
}
