using System;
using System.IO;
using System.Text.Json;
using Autoclicker.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger<SettingsStore> _logger;

    public SettingsStore(string? customFilePath = null, ILogger<SettingsStore>? logger = null)
    {
        _logger = logger ?? NullLogger<SettingsStore>.Instance;

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
                _logger.LogInformation("Settings file not found at {SettingsFilePath}; using default settings", _settingsFilePath);
                return new AppSettings();
            }

            string json = File.ReadAllText(_settingsFilePath);
            var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            if (loaded is null)
            {
                _logger.LogWarning("Settings file at {SettingsFilePath} deserialized to null; using default settings", _settingsFilePath);
                return new AppSettings();
            }

            _logger.LogInformation(
                "Loaded settings from {SettingsFilePath} (PatternCount={PatternCount}, Action={Action}, Button={Button}, MinIntervalMs={MinIntervalMs}, MaxIntervalMs={MaxIntervalMs}, Randomize={RandomizeInterval}, Repetitions={Repetitions}, HumanMovement={HumanMovement})",
                _settingsFilePath,
                loaded.Pattern?.Count ?? 0,
                loaded.Action,
                loaded.Button,
                loaded.MinIntervalMs,
                loaded.MaxIntervalMs,
                loaded.RandomizeInterval,
                loaded.Repetitions,
                loaded.HumanMovement);

            return loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load settings from {SettingsFilePath}. Using defaults.", _settingsFilePath);
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

            _logger.LogInformation(
                "Saved settings to {SettingsFilePath} (PatternCount={PatternCount})",
                _settingsFilePath,
                settings.Pattern?.Count ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save settings to {SettingsFilePath}.", _settingsFilePath);
        }
    }
}

