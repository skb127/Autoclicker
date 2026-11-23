using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Core;
using Autoclicker.Logging;
using Autoclicker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Autoclicker.ViewModels;

/// <summary>
/// Identifies the tabs inside the Settings modal overlay.
/// </summary>
public enum SettingsTab
{
    General = 0,
    EmergencyKey = 1,
    Logs = 2,
    About = 3
}

/// <summary>
/// ViewModel for the in-window Settings modal panel (General, Emergency Key, Logs, About).
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private static readonly TimeSpan DefaultStatusDuration = TimeSpan.FromSeconds(2.5);

    private readonly IHotkeyListener _hotkeyListener;
    private readonly IDesktopIntegration _desktopIntegration;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly TimeSpan _statusMessageDuration;

    private bool _isLoadingPreferences;
    private int _statusMessageVersion;

    public event EventHandler<DialogMessage>? DialogRequested;
    public event EventHandler? PreferencesChanged;
    public event EventHandler? RestoreDefaultsRequested;

    // ─── Modal Overlay State ───────────────────────────────────────────

    [ObservableProperty] private bool _isOpen;

    [ObservableProperty] private int _selectedTabIndex;

    [ObservableProperty] private bool _showVerificationRequired;

    [ObservableProperty] private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    // ─── General Tab ───────────────────────────────────────────────────

    [ObservableProperty] private ThemePreference _theme = ThemePreference.System;

    [ObservableProperty] private int _startDelaySeconds;

    [ObservableProperty] private bool _alwaysOnTop;

    [ObservableProperty] private bool _isRestoreConfirmationVisible;

    public IReadOnlyList<ThemePreference> AvailableThemes { get; } =
    [
        ThemePreference.System,
        ThemePreference.Light,
        ThemePreference.Dark
    ];

    public IReadOnlyList<int> AvailableStartDelays { get; } = [0, 3, 5];

    /// <summary>
    /// True when running on Wayland, used to display the compositor note under Always on Top.
    /// </summary>
    public bool IsWayland =>
        _hotkeyListener.RequiresManualSetup ||
        AppInfo.SessionDescription.Contains("Wayland", StringComparison.OrdinalIgnoreCase);

    // ─── Emergency Key Tab ─────────────────────────────────────────────

    public bool RequiresManualSetup => _hotkeyListener.RequiresManualSetup;

    public bool IsHotkeyReady => _hotkeyListener.IsHotkeyConfigured;

    public bool IsPortalAvailable => _hotkeyListener.IsPortalAvailable;

    public string StopCommand => _hotkeyListener.SetupInstructions;

    public string FixedStopKeyName => "ESC";

    /// <summary>
    /// True when the panel was opened because the user needs to verify their Wayland shortcut
    /// and the shortcut has not yet been verified this session.
    /// </summary>
    public bool IsVerificationBannerVisible => ShowVerificationRequired && !IsHotkeyReady;

    // ─── Logs Tab ──────────────────────────────────────────────────────

    public string? LogDirectory { get; }

    public bool HasLogDirectory => !string.IsNullOrWhiteSpace(LogDirectory);

    public string LogDirectoryDisplay =>
        HasLogDirectory ? LogDirectory! : "File logging is disabled (console only).";

    [ObservableProperty] private string _logStatsText = "0 files · 0 B";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestDeleteOldLogsCommand))]
    private bool _canDeleteOldLogs;

    [ObservableProperty] private bool _isDeleteLogsConfirmationVisible;

    // ─── About Tab ─────────────────────────────────────────────────────

    public string ProductName => AppInfo.ProductName;

    public string AppVersion => AppInfo.DisplayVersion;

    public bool IsDevelopmentBuild => AppInfo.IsDevelopmentBuild;

    public string OsDescription => AppInfo.OsDescription;

    public string Session => AppInfo.SessionDescription;

    public string DotNetRuntime => AppInfo.DotNetRuntime;

    public string AvaloniaVersion => AppInfo.AvaloniaVersion;

    public string RepositoryUrl => AppInfo.RepositoryUrl;

    public string Diagnostics { get; }

    public SettingsViewModel(
        IHotkeyListener hotkeyListener,
        IDesktopIntegration desktopIntegration,
        string? logDirectory,
        ILogger<SettingsViewModel>? logger = null,
        TimeSpan? statusMessageDuration = null)
    {
        _hotkeyListener = hotkeyListener;
        _desktopIntegration = desktopIntegration;
        _logger = logger ?? NullLogger<SettingsViewModel>.Instance;
        _statusMessageDuration = statusMessageDuration ?? DefaultStatusDuration;

        LogDirectory = string.IsNullOrWhiteSpace(logDirectory) ? null : logDirectory;
        Diagnostics = AppInfo.BuildDiagnostics(LogDirectory);

        RefreshLogStats();
    }

    // ─── Panel Open / Close & State Refresh ────────────────────────────

    public void Open(SettingsTab tab = SettingsTab.General, bool showVerificationRequired = false)
    {
        SelectedTabIndex = (int)tab;
        ShowVerificationRequired = showVerificationRequired;
        IsRestoreConfirmationVisible = false;
        IsDeleteLogsConfirmationVisible = false;
        ClearStatusMessage();
        RefreshHotkeyState();
        RefreshLogStats();
        IsOpen = true;
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        ShowVerificationRequired = false;
        IsRestoreConfirmationVisible = false;
        IsDeleteLogsConfirmationVisible = false;
        ClearStatusMessage();
    }

    /// <summary>
    /// Synchronizes preferences loaded from <see cref="AppSettings"/> without firing
    /// <see cref="PreferencesChanged"/>, and applies theme and always-on-top to the desktop.
    /// </summary>
    public void LoadPreferences(AppSettings saved)
    {
        _isLoadingPreferences = true;
        try
        {
            Theme = Enum.IsDefined(saved.Theme) ? saved.Theme : ThemePreference.System;
            StartDelaySeconds = NormalizeStartDelay(saved.StartDelaySeconds);
            AlwaysOnTop = saved.AlwaysOnTop;
            ReapplyWindowPreferences();
        }
        finally
        {
            _isLoadingPreferences = false;
        }
    }

    /// <summary>
    /// Re-applies the active theme and always-on-top state to the desktop window
    /// (called after <c>MainWindow</c> is created on startup).
    /// </summary>
    public void ReapplyWindowPreferences()
    {
        _desktopIntegration.ApplyTheme(Theme);
        _desktopIntegration.SetAlwaysOnTop(AlwaysOnTop);
    }

    /// <summary>
    /// Refreshes Wayland hotkey bindings while keeping the Settings panel open.
    /// </summary>
    public void RefreshHotkeyState()
    {
        if (IsHotkeyReady && ShowVerificationRequired)
        {
            ShowVerificationRequired = false;
        }

        OnPropertyChanged(nameof(RequiresManualSetup));
        OnPropertyChanged(nameof(IsHotkeyReady));
        OnPropertyChanged(nameof(IsPortalAvailable));
        OnPropertyChanged(nameof(StopCommand));
        OnPropertyChanged(nameof(IsWayland));
        OnPropertyChanged(nameof(IsVerificationBannerVisible));
    }

    /// <summary>
    /// Re-scans the log directory to update file count, total byte size, and delete button availability.
    /// </summary>
    public void RefreshLogStats()
    {
        if (!HasLogDirectory)
        {
            LogStatsText = "File logging is disabled (console only).";
            CanDeleteOldLogs = false;
            return;
        }

        var stats = LogMaintenance.GetStats(LogDirectory);
        LogStatsText = LogMaintenance.FormatStats(stats);
        CanDeleteOldLogs = stats.FileCount > 1;
    }

    // ─── General Tab Commands ──────────────────────────────────────────

    [RelayCommand]
    private void RequestRestoreDefaults()
    {
        IsRestoreConfirmationVisible = true;
    }

    [RelayCommand]
    private void CancelRestoreDefaults()
    {
        IsRestoreConfirmationVisible = false;
    }

    [RelayCommand]
    private void ConfirmRestoreDefaults()
    {
        IsRestoreConfirmationVisible = false;
        RestoreDefaultsRequested?.Invoke(this, EventArgs.Empty);
        SetTransientStatus("Default settings restored ✔");
    }

    // ─── Emergency Key Tab Commands ────────────────────────────────────

    [RelayCommand]
    private async Task ConfigureViaPortalAsync()
    {
        var success = await _hotkeyListener.ConfigureHotkeyAsync();

        if (success)
        {
            _logger.LogInformation("Portal hotkey configured successfully");
            ShowVerificationRequired = false;
            SetTransientStatus("Emergency shortcut configured via portal ✔");
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            RaiseDialog(
                DialogSeverity.Warning,
                "Portal Configuration Failed",
                "Automatic portal configuration failed. Please use the manual setup below.");
        }

        RefreshHotkeyState();
    }

    [RelayCommand]
    private void ShowPortalInfo()
    {
        RaiseDialog(
            DialogSeverity.Info,
            "About Automatic Configuration",
            "When you click 'Configure via Portal', Linux will ask you to set an emergency shortcut.\n\n" +
            "However, on some systems, Linux may silently reject your key combination without warning.\n\n" +
            "When this happens, the automatic method will no longer work for safety reasons. To fix this, simply use the 100% reliable Manual Setup described below.",
            confirmText: "Got it!");
    }

    [RelayCommand]
    private async Task CopyStopCommandAsync()
    {
        await CopyToClipboardWithStatusAsync(StopCommand, "Stop command copied to clipboard ✔");
    }

    // ─── Logs Tab Commands ─────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasLogDirectory))]
    private async Task CopyLogPathAsync()
    {
        if (!HasLogDirectory) return;
        await CopyToClipboardWithStatusAsync(LogDirectory!, "Log path copied to clipboard ✔");
    }

    [RelayCommand(CanExecute = nameof(HasLogDirectory))]
    private async Task OpenLogFolderAsync()
    {
        if (!HasLogDirectory) return;

        bool opened = await _desktopIntegration.OpenFolderAsync(LogDirectory!);
        if (!opened)
        {
            RaiseDialog(
                DialogSeverity.Warning,
                "Could Not Open Log Folder",
                $"The system file manager could not be opened automatically.\n\nLog folder path:\n{LogDirectory}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteOldLogs))]
    private void RequestDeleteOldLogs()
    {
        if (!CanDeleteOldLogs) return;
        IsDeleteLogsConfirmationVisible = true;
    }

    [RelayCommand]
    private void CancelDeleteOldLogs()
    {
        IsDeleteLogsConfirmationVisible = false;
    }

    [RelayCommand]
    private void ConfirmDeleteOldLogs()
    {
        IsDeleteLogsConfirmationVisible = false;
        if (!HasLogDirectory) return;

        var result = LogMaintenance.DeleteOldLogs(LogDirectory);
        _logger.LogInformation(
            "Deleted old logs in {LogDirectory}: Deleted={DeletedFiles}, FreedBytes={FreedBytes}, Failed={Failed}",
            LogDirectory,
            result.DeletedFiles,
            result.FreedBytes,
            result.Failed);

        RefreshLogStats();

        if (result.DeletedFiles > 0)
        {
            string freed = LogMaintenance.FormatBytes(result.FreedBytes);
            SetTransientStatus($"Deleted {result.DeletedFiles} old log file(s), freed {freed} ✔");
        }
        else if (result.Failed > 0)
        {
            SetTransientStatus($"Could not delete {result.Failed} log file(s)");
        }
        else
        {
            SetTransientStatus("No old log files to delete");
        }
    }

    // ─── About Tab Commands ────────────────────────────────────────────

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        await CopyToClipboardWithStatusAsync(Diagnostics, "Diagnostic info copied to clipboard ✔");
    }

    [RelayCommand]
    private async Task OpenRepositoryAsync()
    {
        bool opened = await _desktopIntegration.OpenUrlAsync(RepositoryUrl);
        if (!opened)
        {
            SetTransientStatus($"Repository: {RepositoryUrl}");
        }
    }

    // ─── Property Change Callbacks ─────────────────────────────────────

    partial void OnThemeChanged(ThemePreference value)
    {
        if (_isLoadingPreferences) return;

        _desktopIntegration.ApplyTheme(value);
        _logger.LogInformation("Theme preference changed to {Theme}", value);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnStartDelaySecondsChanged(int value)
    {
        if (_isLoadingPreferences) return;

        int clamped = SettingLimits.ClampStartDelaySeconds(value);
        if (clamped != value)
        {
            StartDelaySeconds = clamped;
            return;
        }

        _logger.LogInformation("Start countdown delay changed to {StartDelaySeconds}s", value);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnAlwaysOnTopChanged(bool value)
    {
        if (_isLoadingPreferences) return;

        _desktopIntegration.SetAlwaysOnTop(value);
        _logger.LogInformation("AlwaysOnTop preference changed to {AlwaysOnTop}", value);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShowVerificationRequiredChanged(bool value)
    {
        OnPropertyChanged(nameof(IsVerificationBannerVisible));
    }

    partial void OnStatusMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasStatusMessage));
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private async Task CopyToClipboardWithStatusAsync(string text, string successMessage)
    {
        bool copied = await _desktopIntegration.CopyTextAsync(text);
        SetTransientStatus(copied ? successMessage : "Clipboard unavailable");
    }

    private void SetTransientStatus(string message)
    {
        int version = Interlocked.Increment(ref _statusMessageVersion);
        StatusMessage = message;

        _ = ClearStatusAfterDelayAsync(version);
    }

    private async Task ClearStatusAfterDelayAsync(int version)
    {
        try
        {
            await Task.Delay(_statusMessageDuration);
        }
        catch
        {
            return;
        }

        if (Volatile.Read(ref _statusMessageVersion) == version)
        {
            StatusMessage = null;
        }
    }

    private void ClearStatusMessage()
    {
        Interlocked.Increment(ref _statusMessageVersion);
        StatusMessage = null;
    }

    private void RaiseDialog(DialogSeverity severity, string title, string message, string confirmText = "OK")
    {
        DialogRequested?.Invoke(this, new DialogMessage(severity, title, message, confirmText));
    }

    private int NormalizeStartDelay(int seconds)
    {
        if (AvailableStartDelays.Contains(seconds))
        {
            return seconds;
        }

        return SettingLimits.ClampStartDelaySeconds(seconds);
    }
}
