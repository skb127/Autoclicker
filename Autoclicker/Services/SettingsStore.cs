using System;
using System.IO;
using System.Text.Json;
using Autoclicker.Core;

namespace Autoclicker.Services;

/// <summary>
/// Abstraction for loading and saving application settings and the coordinate pattern.
/// </summary>
public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

/// <summary>
/// Persists <see cref="AppSettings"/> as JSON under the user's application configuration directory:
/// <c>~/.config/Autoclicker/settings.json</c> on Linux or <c>%APPDATA%\Autoclicker\settings.json</c> on Windows.
/// Uses atomic writes (temporary file + move) and gracefully falls back to defaults if the file is missing or corrupt.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private readonly string _settingsFilePath;

    public SettingsStore(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            _settingsFilePath = customFilePath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string directory = Path.Combine(appData, "Autoclicker");
            _settingsFilePath = Path.Combine(directory, "settings.json");
        }
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(_settingsFilePath);
            var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            return loaded ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not load settings ({ex.Message}). Using defaults.");
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = _settingsFilePath + ".tmp";
            string json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not save settings: {ex.Message}");
        }
    }
}

