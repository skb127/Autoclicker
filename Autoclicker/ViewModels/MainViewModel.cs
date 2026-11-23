using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Core;
using Autoclicker.Services;
using Autoclicker.Views.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Autoclicker.ViewModels;

/// <summary>
/// Represents a 2D screen coordinate in the click sequence pattern.
/// </summary>
public record Coordinate(int X, int Y);

public partial class MainViewModel : ViewModelBase
{
    private const int WindowHideSettleDelayMs = 120;

    private readonly IInputSimulator _inputSimulator;
    private readonly ICoordinatePicker _coordinatePicker;
    private readonly IHotkeyListener _hotkeyListener;
    private readonly ISettingsStore _settingsStore;
    private readonly IScreenBoundsProvider _screenBoundsProvider;
    private readonly ILogger<MainViewModel> _logger;
    private readonly ClickEngine _clickEngine;

    private CancellationTokenSource? _clickCts;
    private bool _isLoadingSettings;

    public event EventHandler<int>? CoordinateAdded;

    internal Func<TimeSpan, CancellationToken, Task>? CountdownDelayOverride { get; set; }

    internal int FinishClickingCallCount { get; private set; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty] private bool _isClicking;

    [ObservableProperty] private bool _isCountingDown;

    [ObservableProperty] private int _countdownSeconds;

    // ─── Interval & Randomization ──────────────────────────────────────

    [ObservableProperty] private int _minIntervalMs = 1000;

    [ObservableProperty] private int _maxIntervalMs = 1000;

    [ObservableProperty] private bool _randomizeInterval;

    [ObservableProperty] private int _repetitions;

    // ─── Action & Button Types ─────────────────────────────────────────

    [ObservableProperty] private MouseButtonKind _selectedButton = MouseButtonKind.Left;

    [ObservableProperty] private ClickActionKind _selectedAction = ClickActionKind.Click;

    [ObservableProperty] private int _holdMs = 200;

    // ─── Movement & Jitter ─────────────────────────────────────────────

    [ObservableProperty] private int _jitterPx;

    [ObservableProperty] private bool _humanMovement;

    [ObservableProperty] private MovementSpeedKind _movementSpeed = MovementSpeedKind.Normal;

    // ─── Pattern Selection & Manual Entry ──────────────────────────────

    [ObservableProperty] private int _selectedPatternIndex = -1;

    [ObservableProperty] private int _manualX;

    [ObservableProperty] private int _manualY;

    // ─── Status & Modals ───────────────────────────────────────────────

    [ObservableProperty] private DialogMessage? _activeDialog;

    [ObservableProperty] private bool _canStartAutoclick;

    public ObservableCollection<Coordinate> ClickPatterns { get; } = [];

    // ─── ComboBox Option Lists ─────────────────────────────────────────

    public IReadOnlyList<MouseButtonKind> AvailableButtons { get; } =
    [
        MouseButtonKind.Left,
        MouseButtonKind.Right,
        MouseButtonKind.Middle
    ];

    public IReadOnlyList<ClickActionKind> AvailableActions { get; } =
    [
        ClickActionKind.Click,
        ClickActionKind.DoubleClick,
        ClickActionKind.Hold,
        ClickActionKind.Drag
    ];

    public IReadOnlyList<MovementSpeedKind> AvailableSpeeds { get; } =
    [
        MovementSpeedKind.Slow,
        MovementSpeedKind.Normal,
        MovementSpeedKind.Fast
    ];

    // ─── Computed UI Visibility / Enablement Properties ────────────────

    /// <summary>True when the user selects Hold as the click action.</summary>
    public bool IsHoldSelected => SelectedAction == ClickActionKind.Hold;

    /// <summary>True when the user selects Drag as the click action.</summary>
    public bool IsDragSelected => SelectedAction == ClickActionKind.Drag;

    /// <summary>
    /// True when the Mouse Button selector should be enabled.
    /// Drag action is restricted to the Left mouse button.
    /// </summary>
    public bool CanSelectMouseButton => !IsClicking && SelectedAction != ClickActionKind.Drag;

    /// <summary>
    /// True when human-like movement is active and the configured minimum interval is below the warning threshold,
    /// warning the user that the trajectory travel time may exceed the nominal interval.
    /// </summary>
    public bool IsShortIntervalWarningVisible =>
        HumanMovement && MinIntervalMs < SettingLimits.ShortIntervalWarningThresholdMs;

    /// <summary>True when an item is selected and can be moved up in the pattern list.</summary>
    public bool CanMovePatternUp =>
        !IsClicking && SelectedPatternIndex > 0 && SelectedPatternIndex < ClickPatterns.Count;

    /// <summary>True when an item is selected and can be moved down in the pattern list.</summary>
    public bool CanMovePatternDown =>
        !IsClicking && SelectedPatternIndex >= 0 && SelectedPatternIndex < ClickPatterns.Count - 1;

    /// <summary>True when an item is selected and can be removed from the pattern list.</summary>
    public bool CanRemoveSelectedPattern =>
        !IsClicking && SelectedPatternIndex >= 0 && SelectedPatternIndex < ClickPatterns.Count;

    // ─── Wayland-specific properties (read from the hotkey listener) ───

    /// <summary>True if the platform requires explicit hotkey setup (Wayland only).</summary>
    public bool RequiresManualSetup => _hotkeyListener.RequiresManualSetup;

    /// <summary>True if the emergency hotkey is configured and ready.</summary>
    public bool IsHotkeyReady => _hotkeyListener.IsHotkeyConfigured;

    /// <summary>
    /// True on Wayland when the emergency stop shortcut has not yet been verified in the current session.
    /// Drives the prominent orange main-screen banner and the gear button badge.
    /// </summary>
    public bool IsEmergencyKeyPending => RequiresManualSetup && !IsHotkeyReady;

    /// <summary>
    /// True on Wayland once the emergency stop shortcut has been verified for the current session.
    /// Drives the compact green status bar above the Start button.
    /// </summary>
    public bool IsWaylandEmergencyKeyReady => RequiresManualSetup && IsHotkeyReady;

    /// <summary>
    /// Tooltip shown on the disabled Start button when the Wayland emergency shortcut is still unverified.
    /// </summary>
    public string? StartDisabledReason =>
        IsEmergencyKeyPending
            ? "Verify your emergency stop shortcut first (Wayland): press it once"
            : null;

    public MainViewModel(
        IInputSimulator inputSimulator,
        ICoordinatePicker coordinatePicker,
        IHotkeyListener hotkeyListener,
        ISettingsStore settingsStore,
        IScreenBoundsProvider screenBoundsProvider,
        ILogger<MainViewModel>? logger = null,
        ILogger<ClickEngine>? clickEngineLogger = null,
        SettingsViewModel? settings = null)
    {
        _inputSimulator = inputSimulator;
        _coordinatePicker = coordinatePicker;
        _hotkeyListener = hotkeyListener;
        _settingsStore = settingsStore;
        _screenBoundsProvider = screenBoundsProvider;
        _logger = logger ?? NullLogger<MainViewModel>.Instance;
        _clickEngine = new ClickEngine(_inputSimulator, logger: clickEngineLogger);

        Settings = settings ?? new SettingsViewModel(
            hotkeyListener,
            NullDesktopIntegration.Instance,
            logDirectory: null);

        Settings.DialogRequested += (_, dialog) => ActiveDialog = dialog;
        Settings.PreferencesChanged += (_, _) => SaveCurrentSettings();
        Settings.RestoreDefaultsRequested += (_, _) => RestoreDefaultSettings();

        ClickPatterns.CollectionChanged += (_, _) => RefreshPatternButtonsState();

        // Load persisted user settings and pattern from disk
        LoadSavedSettings();

        // Connect our native emergency key to handle both startup verification and stopping the loop
        _hotkeyListener.OnStopRequested += OnHotkeyStopRequested;

        // Initialize Wayland services asynchronously (fire-and-forget on startup)
        _ = InitializeHotkeyAsync();
    }

    /// <summary>
    /// Initializes the hotkey listener (starts D-Bus service, probes portal availability).
    /// Called once on app startup. Updates UI state when complete.
    /// </summary>
    private async Task InitializeHotkeyAsync()
    {
        try
        {
            await _hotkeyListener.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hotkey initialization warning");
        }

        // RefreshWaylandState calls OnPropertyChanged, which MUST run on the UI thread.
        // InitializeAsync runs on a background thread (fire-and-forget from constructor),
        // so we marshal the notification back to the UI thread here.
        await Dispatcher.UIThread.InvokeAsync(RefreshWaylandState);
    }

    // ─── Persistence ───────────────────────────────────────────────────

    private void LoadSavedSettings()
    {
        _isLoadingSettings = true;
        try
        {
            var saved = _settingsStore.Load();

            ApplyClickAndMovementValues(saved);

            ClickPatterns.Clear();
            if (saved.Pattern is { Count: > 0 })
            {
                foreach (var coord in saved.Pattern)
                {
                    ClickPatterns.Add(coord);
                }
            }

            Settings.LoadPreferences(saved);
        }
        finally
        {
            _isLoadingSettings = false;
            RefreshPatternButtonsState();
            RefreshWaylandState();
            NotifyComputedActionProperties();
        }
    }

    /// <summary>
    /// Resets click interval, repetitions, button/action, hold duration, jitter, and human movement
    /// back to factory defaults while preserving the user's coordinate pattern and General preferences.
    /// </summary>
    public void RestoreDefaultSettings()
    {
        if (IsClicking) return;

        var defaults = new AppSettings();
        _isLoadingSettings = true;
        try
        {
            ApplyClickAndMovementValues(defaults);
        }
        finally
        {
            _isLoadingSettings = false;
            NotifyComputedActionProperties();
        }

        SaveCurrentSettings();
        _logger.LogInformation(
            "Restored default click and movement settings (preserved {PatternCount} coordinate(s))",
            ClickPatterns.Count);
    }

    private void ApplyClickAndMovementValues(AppSettings source)
    {
        MinIntervalMs = SettingLimits.ClampIntervalMs(source.MinIntervalMs);
        MaxIntervalMs = SettingLimits.ClampMaxIntervalMs(source.MaxIntervalMs, MinIntervalMs);
        RandomizeInterval = source.RandomizeInterval;
        if (!RandomizeInterval)
        {
            MaxIntervalMs = MinIntervalMs;
        }

        Repetitions = SettingLimits.ClampRepetitions(source.Repetitions);
        SelectedAction = source.Action;
        SelectedButton = source.Action == ClickActionKind.Drag ? MouseButtonKind.Left : source.Button;
        HoldMs = SettingLimits.ClampHoldMs(source.HoldMs);
        JitterPx = SettingLimits.ClampJitterPx(source.JitterPx);
        HumanMovement = source.HumanMovement;
        MovementSpeed = source.MovementSpeed;
    }

    private void NotifyComputedActionProperties()
    {
        OnPropertyChanged(nameof(IsHoldSelected));
        OnPropertyChanged(nameof(IsDragSelected));
        OnPropertyChanged(nameof(CanSelectMouseButton));
        OnPropertyChanged(nameof(IsShortIntervalWarningVisible));
    }

    /// <summary>
    /// Saves the current UI settings and coordinate pattern to disk.
    /// Called automatically when starting the autoclicker, changing preferences, and closing the main window.
    /// </summary>
    public void SaveCurrentSettings()
    {
        if (_isLoadingSettings) return;

        var settings = new AppSettings(
            Version: 1,
            MinIntervalMs: MinIntervalMs,
            MaxIntervalMs: MaxIntervalMs,
            RandomizeInterval: RandomizeInterval,
            Repetitions: Repetitions,
            Button: SelectedButton,
            Action: SelectedAction,
            HoldMs: HoldMs,
            JitterPx: JitterPx,
            HumanMovement: HumanMovement,
            MovementSpeed: MovementSpeed,
            Pattern: [.. ClickPatterns],
            Theme: Settings.Theme,
            StartDelaySeconds: Settings.StartDelaySeconds,
            AlwaysOnTop: Settings.AlwaysOnTop);

        _settingsStore.Save(settings);
    }

    // ─── Pattern Management Commands ───────────────────────────────────

    [RelayCommand]
    private async Task CaptureCoordinateAsync()
    {
        ActiveDialog = null;
        try
        {
            var coordinate = await _coordinatePicker.PickCoordinateAsync();
            if (coordinate is { } picked)
            {
                AddCoordinateToPattern(picked.X, picked.Y);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Coordinate capture failed");
            ShowWarning("Coordinate Capture Failed", ex.Message);
        }
    }

    [RelayCommand]
    private void AddManualCoordinate()
    {
        if (IsClicking) return;

        var validation = AutoclickerValidator.ValidateManualCoordinate(
            ManualX,
            ManualY,
            _screenBoundsProvider.GetScreens());
        if (!validation.IsValid)
        {
            HandleValidationFailure(validation);
            return;
        }

        AddCoordinateToPattern(ManualX, ManualY);
    }

    private void AddCoordinateToPattern(int x, int y)
    {
        ClickPatterns.Add(new Coordinate(x, y));
        int addedIndex = ClickPatterns.Count - 1;
        SelectedPatternIndex = addedIndex;
        CoordinateAdded?.Invoke(this, addedIndex);
        _logger.LogInformation(
            "Added coordinate ({X}, {Y}) at index {Index} (Total={Count})",
            x,
            y,
            addedIndex,
            ClickPatterns.Count);
    }

    [RelayCommand]
    private void ClearPattern()
    {
        int removedCount = ClickPatterns.Count;
        ClickPatterns.Clear();
        SelectedPatternIndex = -1;
        ActiveDialog = null;

        if (removedCount > 0)
        {
            _logger.LogInformation("Cleared coordinate pattern ({RemovedCount} coordinate(s) removed)", removedCount);
        }
    }

    [RelayCommand]
    private void MovePatternUp()
    {
        MovePattern(SelectedPatternIndex, SelectedPatternIndex - 1);
    }

    [RelayCommand]
    private void MovePatternDown()
    {
        MovePattern(SelectedPatternIndex, SelectedPatternIndex + 1);
    }

    [RelayCommand]
    private void RemoveSelectedPattern()
    {
        if (IsClicking || SelectedPatternIndex < 0 || SelectedPatternIndex >= ClickPatterns.Count)
        {
            return;
        }

        int removedIndex = SelectedPatternIndex;
        var removed = ClickPatterns[removedIndex];
        ClickPatterns.RemoveAt(removedIndex);

        _logger.LogInformation(
            "Removed coordinate ({X}, {Y}) at index {Index} (Remaining={Count})",
            removed.X,
            removed.Y,
            removedIndex,
            ClickPatterns.Count);

        if (ClickPatterns.Count == 0)
        {
            SelectedPatternIndex = -1;
        }
        else
        {
            SelectedPatternIndex = Math.Min(removedIndex, ClickPatterns.Count - 1);
        }
    }

    /// <summary>
    /// Moves a coordinate inside <see cref="ClickPatterns"/> from <paramref name="fromIndex"/>
    /// to <paramref name="toIndex"/>. Used by both the Up/Down buttons and ListBox Drag &amp; Drop.
    /// </summary>
    public void MovePattern(int fromIndex, int toIndex)
    {
        if (IsClicking) return;
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= ClickPatterns.Count) return;
        if (toIndex < 0 || toIndex >= ClickPatterns.Count) return;

        ClickPatterns.Move(fromIndex, toIndex);
        SelectedPatternIndex = toIndex;
        RefreshPatternButtonsState();
    }

    // ─── Start / Stop Commands ─────────────────────────────────────────

    [RelayCommand]
    private async Task StartAutoclick()
    {
        if (IsClicking) return;

        var preStartValidation = AutoclickerValidator.ValidatePreStart(
            ClickPatterns,
            SelectedAction,
            _hotkeyListener.RequiresManualSetup,
            _hotkeyListener.IsHotkeyConfigured);
        if (!preStartValidation.IsValid)
        {
            HandleValidationFailure(preStartValidation);
            return;
        }

        ActiveDialog = null;
        Settings.CloseCommand.Execute(null);
        SaveCurrentSettings();

        Window? mainWindow = null;
        FullScreenCountdownOverlay? countdownOverlay = null;
        bool wasWindowVisible = false;
        WindowState savedWindowState = WindowState.Normal;
        PixelPoint? savedPosition = null;
        bool enteredClickingPhase = false;
        bool handedOffToBackgroundLoop = false;

        try
        {
            await _inputSimulator.InitializeAsync();

            // Verify that all pattern points fall inside the shared monitor (relevant on multi-monitor Wayland)
            var targetValidation = AutoclickerValidator.ValidateSharedMonitorTargets(
                _inputSimulator.ValidateTargets(ClickPatterns.Select(p => (p.X, p.Y))));
            if (!targetValidation.IsValid)
            {
                HandleValidationFailure(targetValidation);
                return;
            }

            await _hotkeyListener.StartListening();

            var settingsSnapshot = new ClickSettings(
                MinIntervalMs: MinIntervalMs,
                MaxIntervalMs: MaxIntervalMs,
                RandomizeInterval: RandomizeInterval,
                Repetitions: Repetitions,
                Button: SelectedButton,
                Action: SelectedAction,
                HoldMs: HoldMs,
                JitterPx: JitterPx,
                HumanMovement: HumanMovement,
                MovementSpeed: MovementSpeed);

            var patternSnapshot = ClickPatterns.ToList();

            _clickCts?.Cancel();
            _clickCts?.Dispose();
            var cts = new CancellationTokenSource();
            _clickCts = cts;

            IsClicking = true;
            enteredClickingPhase = true;

            mainWindow = GetMainWindow();
            countdownOverlay = Settings.StartDelaySeconds > 0 && mainWindow is not null
                ? FullScreenCountdownOverlay.Create(
                    mainWindow,
                    patternSnapshot,
                    singleMonitorTargetOnly: _hotkeyListener.RequiresManualSetup,
                    onCancelRequested: StopAutoclick,
                    logger: _logger)
                : null;

            if (mainWindow is { IsVisible: true })
            {
                wasWindowVisible = true;
                savedWindowState = mainWindow.WindowState;
                if (savedWindowState == WindowState.Normal)
                {
                    savedPosition = mainWindow.Position;
                }

                // Hide the app window so the target desktop is visible underneath the full-screen countdown
                // and targets underneath can be clicked reliably.
                mainWindow.Hide();
                await Task.Delay(WindowHideSettleDelayMs, cts.Token);
            }

            if (Settings.StartDelaySeconds > 0)
            {
                _logger.LogInformation("Countdown of {Delay}s before starting", Settings.StartDelaySeconds);
                await RunCountdownAsync(
                    Settings.StartDelaySeconds,
                    cts.Token,
                    delayAsync: CountdownDelayOverride,
                    overlay: countdownOverlay);
                await Task.Delay(WindowHideSettleDelayMs, cts.Token);
            }

            handedOffToBackgroundLoop = true;

            _logger.LogInformation(
                "Starting autoclicker: Button={Button}, Action={Action}, Count={Count}, MinIntervalMs={MinIntervalMs}, MaxIntervalMs={MaxIntervalMs}, Randomize={RandomizeInterval}, Repetitions={Repetitions}, HoldMs={HoldMs}, JitterPx={JitterPx}, HumanMovement={HumanMovement}, Speed={MovementSpeed}",
                settingsSnapshot.Button,
                settingsSnapshot.Action,
                patternSnapshot.Count,
                settingsSnapshot.MinIntervalMs,
                settingsSnapshot.MaxIntervalMs,
                settingsSnapshot.RandomizeInterval,
                settingsSnapshot.Repetitions,
                settingsSnapshot.HoldMs,
                settingsSnapshot.JitterPx,
                settingsSnapshot.HumanMovement,
                settingsSnapshot.MovementSpeed);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _clickEngine.RunAsync(patternSnapshot, settingsSnapshot, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the user stops the loop via button or emergency hotkey
                    _logger.LogInformation("Autoclick loop canceled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Autoclick loop error");
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        ShowError("Autoclick Loop Error", ex.Message));
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        FinishClicking(mainWindow, wasWindowVisible, savedWindowState, savedPosition));
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected if the user cancels during the countdown or the brief hide-settle delay.
            _logger.LogInformation("Autoclick start canceled before background loop began");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Input initialization failed while starting autoclicker");
            ShowError("Input Initialization Failed", $"Could not start event injection. {ex.Message}");
        }
        finally
        {
            if (!handedOffToBackgroundLoop)
            {
                if (enteredClickingPhase || wasWindowVisible)
                {
                    // Restore and activate MainWindow before closing the topmost countdown overlay
                    // so the OS/compositor preserves foreground activation and taskbar visibility.
                    FinishClicking(mainWindow, wasWindowVisible, savedWindowState, savedPosition);
                }

                countdownOverlay?.Close();

                if (mainWindow is not null && wasWindowVisible)
                {
                    mainWindow.Activate();
                }
            }
            else
            {
                countdownOverlay?.Close();
            }
        }
    }

    /// <summary>
    /// Runs the full-screen countdown sequence (<c>N .. 1</c>) after the main window hides.
    /// </summary>
    internal async Task RunCountdownAsync(
        int seconds,
        CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        FullScreenCountdownOverlay? overlay = null)
    {
        if (seconds <= 0)
        {
            return;
        }

        var waitAsync = delayAsync ?? Task.Delay;
        IsCountingDown = true;
        try
        {
            overlay?.Show(seconds);

            for (int remaining = seconds; remaining > 0; remaining--)
            {
                token.ThrowIfCancellationRequested();
                CountdownSeconds = remaining;
                overlay?.UpdateSeconds(remaining);
                await waitAsync(TimeSpan.FromSeconds(1), token);
            }

            overlay?.Close();
        }
        finally
        {
            IsCountingDown = false;
            CountdownSeconds = 0;
        }
    }

    [RelayCommand]
    private void StopAutoclick()
    {
        _logger.LogInformation("Stopping autoclicker");

        try
        {
            _clickCts?.Cancel();
        }
        catch (Exception ex)
        {
            // Ignore if already disposed
            _logger.LogWarning(ex, "CancellationTokenSource already disposed when stopping autoclicker");
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            IsCountingDown = false;
            CountdownSeconds = 0;
            IsClicking = false;
            _hotkeyListener.StopListening();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsCountingDown = false;
                CountdownSeconds = 0;
                IsClicking = false;
                _hotkeyListener.StopListening();
            });
        }
    }

    private void FinishClicking(
        Window? mainWindow,
        bool wasWindowVisible,
        WindowState savedWindowState,
        PixelPoint? savedPosition)
    {
        FinishClickingCallCount++;
        IsCountingDown = false;
        CountdownSeconds = 0;
        IsClicking = false;
        _hotkeyListener.StopListening();

        if (mainWindow is not null && wasWindowVisible)
        {
            mainWindow.Show();

            if (savedWindowState == WindowState.Maximized &&
                mainWindow.WindowState != WindowState.Maximized)
            {
                mainWindow.WindowState = WindowState.Maximized;
            }
            else if (savedWindowState == WindowState.Normal &&
                     savedPosition is { } pos &&
                     mainWindow.Position != pos)
            {
                mainWindow.Position = pos;
            }

            mainWindow.Activate();
        }
    }

    private static Window? GetMainWindow()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }

    // ─── Settings & Modal Dialog Commands ──────────────────────────────

    [RelayCommand]
    private void OpenSettings()
    {
        if (IsClicking) return;

        ActiveDialog = null;
        if (IsEmergencyKeyPending)
        {
            Settings.Open(SettingsTab.EmergencyKey, showVerificationRequired: true);
        }
        else
        {
            Settings.Open();
        }
    }

    [RelayCommand]
    private void OpenEmergencyKeySettings()
    {
        if (IsClicking) return;

        ActiveDialog = null;
        Settings.Open(SettingsTab.EmergencyKey, showVerificationRequired: IsEmergencyKeyPending);
    }

    private void OnHotkeyStopRequested()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            HandleHotkeyStopOnUiThread();
        }
        else
        {
            Dispatcher.UIThread.Post(HandleHotkeyStopOnUiThread);
        }
    }

    private void HandleHotkeyStopOnUiThread()
    {
        if (IsClicking)
        {
            StopAutoclick();
        }

        RefreshWaylandState();
    }

    [RelayCommand]
    private void CloseDialog()
    {
        ActiveDialog = null;
    }

    private void HandleValidationFailure(ValidationResult result)
    {
        if (result.IsValid) return;

        _logger.LogWarning("{ValidationMessage}", result.LogMessage);

        if (result.Code == ValidationErrorCode.WaylandHotkeyNotVerified)
        {
            Settings.Open(SettingsTab.EmergencyKey, showVerificationRequired: true);
            return;
        }

        ShowWarning(result.Title, result.UserMessage);
    }

    private void ShowInfo(string title, string message, string confirmText = "OK") =>
        ActiveDialog = new DialogMessage(DialogSeverity.Info, title, message, confirmText);

    private void ShowWarning(string title, string message, string confirmText = "OK") =>
        ActiveDialog = new DialogMessage(DialogSeverity.Warning, title, message, confirmText);

    private void ShowError(string title, string message, string confirmText = "OK") =>
        ActiveDialog = new DialogMessage(DialogSeverity.Error, title, message, confirmText);

    // ─── Property Change Callbacks & State Synchronization ─────────────

    partial void OnMinIntervalMsChanged(int value)
    {
        if (_isLoadingSettings) return;

        int clamped = SettingLimits.ClampIntervalMs(value);
        if (clamped != value)
        {
            OnPropertyChanged(nameof(MinIntervalMs));
            MinIntervalMs = clamped;
            return;
        }

        // Notify MinIntervalMs listeners before updating dependent MaxIntervalMs.
        OnPropertyChanged(nameof(MinIntervalMs));

        if (!RandomizeInterval || MaxIntervalMs < value)
        {
            MaxIntervalMs = value;
        }

        OnPropertyChanged(nameof(IsShortIntervalWarningVisible));
    }

    partial void OnMaxIntervalMsChanged(int value)
    {
        if (_isLoadingSettings) return;

        int clamped = SettingLimits.ClampMaxIntervalMs(value, MinIntervalMs);
        if (clamped != value)
        {
            OnPropertyChanged(nameof(MaxIntervalMs));
            MaxIntervalMs = clamped;
        }
    }

    partial void OnRandomizeIntervalChanged(bool value)
    {
        if (_isLoadingSettings) return;

        if (!value)
        {
            MaxIntervalMs = MinIntervalMs;
        }
        else if (MaxIntervalMs <= MinIntervalMs)
        {
            MaxIntervalMs = Math.Min(
                SettingLimits.MaxIntervalMs,
                MinIntervalMs + SettingLimits.DefaultRandomMaxStepMs);
        }
    }

    partial void OnSelectedActionChanged(ClickActionKind value)
    {
        if (value == ClickActionKind.Drag && SelectedButton != MouseButtonKind.Left)
        {
            SelectedButton = MouseButtonKind.Left;
        }

        OnPropertyChanged(nameof(IsHoldSelected));
        OnPropertyChanged(nameof(IsDragSelected));
        OnPropertyChanged(nameof(CanSelectMouseButton));
    }

    partial void OnHumanMovementChanged(bool value)
    {
        OnPropertyChanged(nameof(IsShortIntervalWarningVisible));
    }

    partial void OnSelectedPatternIndexChanged(int value)
    {
        RefreshPatternButtonsState();
    }

    partial void OnIsClickingChanged(bool value)
    {
        UpdateCanStart();
        RefreshPatternButtonsState();
        OnPropertyChanged(nameof(CanSelectMouseButton));
    }

    private void RefreshPatternButtonsState()
    {
        OnPropertyChanged(nameof(CanMovePatternUp));
        OnPropertyChanged(nameof(CanMovePatternDown));
        OnPropertyChanged(nameof(CanRemoveSelectedPattern));
    }

    /// <summary>Updates CanStartAutoclick based on current state.</summary>
    private void UpdateCanStart()
    {
        // On Win32/X11: RequiresManualSetup=false -> always allowed (just !IsClicking)
        // On Wayland: also requires IsHotkeyConfigured
        CanStartAutoclick = !IsClicking && (!_hotkeyListener.RequiresManualSetup || _hotkeyListener.IsHotkeyConfigured);
    }

    /// <summary>Refreshes all Wayland-related UI bindings after state changes.</summary>
    private void RefreshWaylandState()
    {
        OnPropertyChanged(nameof(RequiresManualSetup));
        OnPropertyChanged(nameof(IsHotkeyReady));
        OnPropertyChanged(nameof(IsEmergencyKeyPending));
        OnPropertyChanged(nameof(IsWaylandEmergencyKeyReady));
        OnPropertyChanged(nameof(StartDisabledReason));
        Settings.RefreshHotkeyState();
        UpdateCanStart();
    }
}
