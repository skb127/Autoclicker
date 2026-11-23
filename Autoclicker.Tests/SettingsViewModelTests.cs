using System;
using System.IO;
using System.Threading.Tasks;
using Autoclicker.Core;
using Autoclicker.Services;
using Autoclicker.ViewModels;
using Xunit;

namespace Autoclicker.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _tempLogDir;

    public SettingsViewModelTests()
    {
        _tempLogDir = Path.Combine(Path.GetTempPath(), "AutoclickerSettingsVmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempLogDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempLogDir))
            {
                Directory.Delete(_tempLogDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public void OpenAndClose_SwitchesTabAndResetsConfirmationsAndBanner()
    {
        // Arrange
        var hotkey = new FakeHotkeyListener { RequiresManualSetup = true, IsHotkeyConfigured = false };
        var desktop = new FakeDesktopIntegration();
        var vm = new SettingsViewModel(hotkey, desktop, _tempLogDir);

        // Act - Open panel
        vm.Open(SettingsTab.Logs, showVerificationRequired: true);

        // Assert - Opened on Logs tab with verification banner active
        Assert.True(vm.IsOpen);
        Assert.Equal((int)SettingsTab.Logs, vm.SelectedTabIndex);
        Assert.True(vm.IsVerificationBannerVisible);

        // Act - Request restore defaults and close panel
        vm.RequestRestoreDefaultsCommand.Execute(null);
        bool confirmationShownBeforeClose = vm.IsRestoreConfirmationVisible;
        vm.CloseCommand.Execute(null);

        // Assert - Confirmation was shown and closing resets state
        Assert.True(confirmationShownBeforeClose);
        Assert.False(vm.IsOpen);
        Assert.False(vm.IsVerificationBannerVisible);
        Assert.False(vm.IsRestoreConfirmationVisible);
    }

    [Fact]
    public async Task CopyCommands_CopyExpectedPayloadsToClipboardAndSetStatusMessage()
    {
        // Arrange
        var hotkey = new FakeHotkeyListener
        {
            RequiresManualSetup = true,
            SetupInstructions = "busctl --user call com.autoclicker.app /com/autoclicker/app com.autoclicker.app Stop"
        };
        var desktop = new FakeDesktopIntegration();
        var vm = new SettingsViewModel(
            hotkey,
            desktop,
            _tempLogDir,
            statusMessageDuration: TimeSpan.FromMinutes(5));

        // Act - Copy stop command
        await vm.CopyStopCommandCommand.ExecuteAsync(null);

        // Assert - Stop command copied and status shown
        Assert.Equal(hotkey.SetupInstructions, desktop.LastCopiedText);
        Assert.Contains("copied", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // Act - Copy log path
        await vm.CopyLogPathCommand.ExecuteAsync(null);

        // Assert - Log path copied
        Assert.Equal(_tempLogDir, desktop.LastCopiedText);

        // Act - Copy diagnostics
        await vm.CopyDiagnosticsCommand.ExecuteAsync(null);

        // Assert - Diagnostics copied
        Assert.Equal(vm.Diagnostics, desktop.LastCopiedText);
    }

    [Fact]
    public void LogCommands_WhenLogDirectoryIsNull_AreDisabled()
    {
        // Arrange
        var vm = new SettingsViewModel(new FakeHotkeyListener(), new FakeDesktopIntegration(), logDirectory: null);

        // Act
        bool hasLogDirectory = vm.HasLogDirectory;
        bool canCopyLogPath = vm.CopyLogPathCommand.CanExecute(null);
        bool canOpenLogFolder = vm.OpenLogFolderCommand.CanExecute(null);
        bool canDeleteOldLogs = vm.RequestDeleteOldLogsCommand.CanExecute(null);

        // Assert
        Assert.False(hasLogDirectory);
        Assert.False(canCopyLogPath);
        Assert.False(canOpenLogFolder);
        Assert.False(canDeleteOldLogs);
    }

    [Fact]
    public async Task OpenLogFolder_WhenLauncherFails_RaisesWarningDialogWithPath()
    {
        // Arrange
        var desktop = new FakeDesktopIntegration { OpenFolderResult = false };
        var vm = new SettingsViewModel(new FakeHotkeyListener(), desktop, _tempLogDir);

        DialogMessage? receivedDialog = null;
        vm.DialogRequested += (_, d) => receivedDialog = d;

        // Act
        await vm.OpenLogFolderCommand.ExecuteAsync(null);

        // Assert
        Assert.NotNull(receivedDialog);
        Assert.Equal(DialogSeverity.Warning, receivedDialog!.Severity);
        Assert.Contains(_tempLogDir, receivedDialog.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigureViaPortal_OnFailureRaisesDialog_OnSuccessUpdatesHotkeyReady()
    {
        // Arrange
        var hotkey = new FakeHotkeyListener
        {
            RequiresManualSetup = true,
            IsHotkeyConfigured = false,
            IsPortalAvailable = true,
            ConfigureHotkeyResult = false
        };
        var desktop = new FakeDesktopIntegration();
        var vm = new SettingsViewModel(hotkey, desktop, _tempLogDir, statusMessageDuration: TimeSpan.FromMinutes(5));
        vm.Open(SettingsTab.EmergencyKey, showVerificationRequired: true);

        DialogMessage? dialog = null;
        vm.DialogRequested += (_, d) => dialog = d;

        // Act - Portal configuration fails
        await vm.ConfigureViaPortalCommand.ExecuteAsync(null);

        // Assert - Warning dialog shown and hotkey remains unverified
        Assert.NotNull(dialog);
        Assert.Equal(DialogSeverity.Warning, dialog!.Severity);
        Assert.False(vm.IsHotkeyReady);
        Assert.True(vm.IsVerificationBannerVisible);

        // Act - Portal configuration succeeds
        dialog = null;
        hotkey.ConfigureHotkeyResult = true;
        await vm.ConfigureViaPortalCommand.ExecuteAsync(null);

        // Assert - Hotkey verified and panel stays open
        Assert.Null(dialog);
        Assert.True(vm.IsHotkeyReady);
        Assert.False(vm.IsVerificationBannerVisible);
        Assert.True(vm.IsOpen);
    }

    [Fact]
    public void RefreshHotkeyState_WhenShortcutVerified_HidesBannerAndKeepsPanelOpen()
    {
        // Arrange
        var hotkey = new FakeHotkeyListener { RequiresManualSetup = true, IsHotkeyConfigured = false };
        var vm = new SettingsViewModel(hotkey, new FakeDesktopIntegration(), _tempLogDir);
        vm.Open(SettingsTab.EmergencyKey, showVerificationRequired: true);
        bool wasOpenBeforeRefresh = vm.IsOpen;
        bool wasBannerVisibleBeforeRefresh = vm.IsVerificationBannerVisible;

        // Act
        hotkey.IsHotkeyConfigured = true;
        vm.RefreshHotkeyState();

        // Assert
        Assert.True(wasOpenBeforeRefresh);
        Assert.True(wasBannerVisibleBeforeRefresh);
        Assert.True(vm.IsOpen);
        Assert.True(vm.IsHotkeyReady);
        Assert.False(vm.IsVerificationBannerVisible);
    }

    [Fact]
    public void Preferences_DoNotRaisePreferencesChangedDuringLoad_AndRaiseOnUserEdit()
    {
        // Arrange
        var desktop = new FakeDesktopIntegration();
        var vm = new SettingsViewModel(new FakeHotkeyListener(), desktop, _tempLogDir);

        int changedCount = 0;
        vm.PreferencesChanged += (_, _) => changedCount++;

        // Act - Load saved preferences
        vm.LoadPreferences(new AppSettings(
            Theme: ThemePreference.Dark,
            StartDelaySeconds: 3,
            AlwaysOnTop: true));

        // Assert - Loading does not raise PreferencesChanged and applies initial values
        Assert.Equal(0, changedCount);
        Assert.Equal(ThemePreference.Dark, desktop.LastAppliedTheme);
        Assert.True(desktop.LastAlwaysOnTop);

        // Act - Edit theme
        vm.Theme = ThemePreference.Light;

        // Assert - Theme edit raises PreferencesChanged and updates desktop
        Assert.Equal(1, changedCount);
        Assert.Equal(ThemePreference.Light, desktop.LastAppliedTheme);

        // Act - Edit start delay
        vm.StartDelaySeconds = 5;

        // Assert - Delay edit raises PreferencesChanged
        Assert.Equal(2, changedCount);

        // Act - Edit always on top
        vm.AlwaysOnTop = false;

        // Assert - AlwaysOnTop edit raises PreferencesChanged and updates desktop
        Assert.Equal(3, changedCount);
        Assert.False(desktop.LastAlwaysOnTop);
    }

    [Fact]
    public void DeleteOldLogsAndRestoreDefaults_RequireConfirmationAndCanBeCanceled()
    {
        // Arrange
        string oldLog = Path.Combine(_tempLogDir, "autoclicker-20261118.log");
        string currentLog = Path.Combine(_tempLogDir, "autoclicker-20261121.log");
        File.WriteAllBytes(oldLog, new byte[128]);
        File.WriteAllBytes(currentLog, new byte[256]);
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(currentLog, DateTime.UtcNow);

        var vm = new SettingsViewModel(
            new FakeHotkeyListener(),
            new FakeDesktopIntegration(),
            _tempLogDir,
            statusMessageDuration: TimeSpan.FromMinutes(5));
        vm.RefreshLogStats();
        bool canDeleteInitially = vm.CanDeleteOldLogs;

        int restoreCount = 0;
        vm.RestoreDefaultsRequested += (_, _) => restoreCount++;

        // Act - Request and cancel log deletion
        vm.RequestDeleteOldLogsCommand.Execute(null);
        bool deletePromptVisible = vm.IsDeleteLogsConfirmationVisible;
        vm.CancelDeleteOldLogsCommand.Execute(null);

        // Assert - Log deletion canceled and old log kept
        Assert.True(canDeleteInitially);
        Assert.True(deletePromptVisible);
        Assert.False(vm.IsDeleteLogsConfirmationVisible);
        Assert.True(File.Exists(oldLog));

        // Act - Request and confirm log deletion
        vm.RequestDeleteOldLogsCommand.Execute(null);
        vm.ConfirmDeleteOldLogsCommand.Execute(null);

        // Assert - Old log deleted and current log preserved
        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(currentLog));
        Assert.False(vm.CanDeleteOldLogs);

        // Act - Request and cancel restore defaults
        vm.RequestRestoreDefaultsCommand.Execute(null);
        vm.CancelRestoreDefaultsCommand.Execute(null);

        // Assert - Restore canceled
        Assert.Equal(0, restoreCount);

        // Act - Request and confirm restore defaults
        vm.RequestRestoreDefaultsCommand.Execute(null);
        vm.ConfirmRestoreDefaultsCommand.Execute(null);

        // Assert - Restore executed
        Assert.Equal(1, restoreCount);
    }

    private sealed class FakeHotkeyListener : IHotkeyListener
    {
        public event Action? OnStopRequested
        {
            add { }
            remove { }
        }

        public bool RequiresManualSetup { get; set; }
        public bool IsHotkeyConfigured { get; set; } = true;
        public bool IsPortalAvailable { get; set; }
        public string SetupInstructions { get; set; } = string.Empty;
        public bool ConfigureHotkeyResult { get; set; } = true;

        public Task StartListening() => Task.CompletedTask;
        public void StopListening() { }

        public Task<bool> ConfigureHotkeyAsync()
        {
            if (ConfigureHotkeyResult)
            {
                IsHotkeyConfigured = true;
            }

            return Task.FromResult(ConfigureHotkeyResult);
        }
    }

    private sealed class FakeDesktopIntegration : IDesktopIntegration
    {
        public string? LastCopiedText { get; private set; }
        public string? LastOpenedFolder { get; private set; }
        public string? LastOpenedUrl { get; private set; }
        public ThemePreference? LastAppliedTheme { get; private set; }
        public bool? LastAlwaysOnTop { get; private set; }

        public bool CopyResult { get; set; } = true;
        public bool OpenFolderResult { get; set; } = true;
        public bool OpenUrlResult { get; set; } = true;

        public Task<bool> CopyTextAsync(string text)
        {
            LastCopiedText = text;
            return Task.FromResult(CopyResult);
        }

        public Task<bool> OpenFolderAsync(string path)
        {
            LastOpenedFolder = path;
            return Task.FromResult(OpenFolderResult);
        }

        public Task<bool> OpenUrlAsync(string url)
        {
            LastOpenedUrl = url;
            return Task.FromResult(OpenUrlResult);
        }

        public void ApplyTheme(ThemePreference theme) => LastAppliedTheme = theme;
        public void SetAlwaysOnTop(bool alwaysOnTop) => LastAlwaysOnTop = alwaysOnTop;
    }
}
