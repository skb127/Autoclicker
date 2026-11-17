using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Core;
using Autoclicker.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    private readonly ClickEngine _clickEngine;

    private CancellationTokenSource? _clickCts;
    private bool _isLoadingSettings;

    public event EventHandler<int>? CoordinateAdded;

    [ObservableProperty] private bool _isClicking;

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

    [ObservableProperty] private bool _showWaylandSetupDetails;

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
    /// True when human-like movement is active and the configured minimum interval is below 300 ms,
    /// warning the user that the trajectory travel time may exceed the nominal interval.
    /// </summary>
    public bool IsShortIntervalWarningVisible => HumanMovement && MinIntervalMs < 300;

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

    /// <summary>True if the GlobalShortcuts portal is available for automatic configuration.</summary>
    public bool IsPortalAvailable => _hotkeyListener.IsPortalAvailable;

    /// <summary>The busctl command the user needs for manual GNOME/COSMIC/KDE shortcut setup.</summary>
    public string SetupInstructions => _hotkeyListener.SetupInstructions;

    /// <summary>
    /// True when the Wayland setup instructions section should be expanded:
    /// either because the hotkey is not yet configured, or because the user clicked to view instructions.
    /// </summary>
    public bool IsWaylandSetupSectionVisible => !IsHotkeyReady || ShowWaylandSetupDetails;

    public MainViewModel(
        IInputSimulator inputSimulator,
        ICoordinatePicker coordinatePicker,
        IHotkeyListener hotkeyListener,
        ISettingsStore settingsStore,
        IScreenBoundsProvider screenBoundsProvider)
    {
        _inputSimulator = inputSimulator;
        _coordinatePicker = coordinatePicker;
        _hotkeyListener = hotkeyListener;
        _settingsStore = settingsStore;
        _screenBoundsProvider = screenBoundsProvider;
        _clickEngine = new ClickEngine(_inputSimulator);

        ClickPatterns.CollectionChanged += (_, _) => RefreshPatternButtonsState();

        // Load persisted user settings and pattern from disk
        LoadSavedSettings();

        // Connect our native emergency key to the method that stops the loop
        _hotkeyListener.OnStopRequested += StopAutoclick;

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
            Console.WriteLine($"Hotkey initialization warning: {ex.Message}");
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

            MinIntervalMs = Math.Clamp(saved.MinIntervalMs, 1, 60000);
            MaxIntervalMs = Math.Clamp(saved.MaxIntervalMs, MinIntervalMs, 60000);
            RandomizeInterval = saved.RandomizeInterval;
            if (!RandomizeInterval)
            {
                MaxIntervalMs = MinIntervalMs;
            }

            Repetitions = Math.Clamp(saved.Repetitions, 0, 9999);
            SelectedAction = saved.Action;
            SelectedButton = saved.Action == ClickActionKind.Drag ? MouseButtonKind.Left : saved.Button;
            HoldMs = Math.Clamp(saved.HoldMs, 1, 60000);
            JitterPx = Math.Clamp(saved.JitterPx, 0, 50);
            HumanMovement = saved.HumanMovement;
            MovementSpeed = saved.MovementSpeed;

            ClickPatterns.Clear();
            if (saved.Pattern is { Count: > 0 })
            {
                foreach (var coord in saved.Pattern)
                {
                    ClickPatterns.Add(coord);
                }
            }

            if (saved.WaylandHotkeyConfirmed && _hotkeyListener.RequiresManualSetup)
            {
                _hotkeyListener.ConfirmManualSetup();
            }
        }
        finally
        {
            _isLoadingSettings = false;
            RefreshPatternButtonsState();
            RefreshWaylandState();
            OnPropertyChanged(nameof(IsHoldSelected));
            OnPropertyChanged(nameof(IsDragSelected));
            OnPropertyChanged(nameof(CanSelectMouseButton));
            OnPropertyChanged(nameof(IsShortIntervalWarningVisible));
        }
    }

    /// <summary>
    /// Saves the current UI settings and coordinate pattern to disk.
    /// Called automatically when starting the autoclicker and when closing the main window.
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
            WaylandHotkeyConfirmed: _hotkeyListener is { RequiresManualSetup: true, IsHotkeyConfigured: true });

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
            ShowWarning("Coordinate Capture Failed", ex.Message);
        }
    }

    [RelayCommand]
    private void AddManualCoordinate()
    {
        if (IsClicking) return;

        var screens = _screenBoundsProvider.GetScreens();
        if (screens.Count > 0)
        {
            bool isInsideAnyScreen = screens.Any(s => s.Contains(ManualX, ManualY));
            if (!isInsideAnyScreen)
            {
                var monitorList = string.Join(
                    "\n",
                    screens.Select(s =>
                        $"• {s.Name}: X [{s.X}..{s.Right - 1}], Y [{s.Y}..{s.Bottom - 1}] ({s.Width}×{s.Height})"));

                ShowWarning(
                    "Coordinate Out of Bounds",
                    $"The coordinate (X: {ManualX}, Y: {ManualY}) is outside all connected monitors.\n\nAvailable monitors:\n{monitorList}");
                return;
            }
        }
        else if (ManualX < 0 || ManualY < 0)
        {
            ShowWarning(
                "Invalid Coordinate",
                $"Monitor bounds could not be detected, and negative coordinates (X: {ManualX}, Y: {ManualY}) are not allowed.");
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
    }

    [RelayCommand]
    private void ClearPattern()
    {
        ClickPatterns.Clear();
        SelectedPatternIndex = -1;
        ActiveDialog = null;
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
        ClickPatterns.RemoveAt(removedIndex);

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
        if (IsClicking || ClickPatterns.Count == 0) return;

        if (SelectedAction == ClickActionKind.Drag && ClickPatterns.Count < 2)
        {
            ShowWarning(
                "Insufficient Coordinates",
                "Drag action requires at least 2 coordinates in the pattern (start and end points).");
            return;
        }

        if (_hotkeyListener is { RequiresManualSetup: true, IsHotkeyConfigured: false })
        {
            ShowWarning(
                "Emergency Key Required",
                "Configure the emergency key before starting (see the Wayland Emergency Key section below).");
            return;
        }

        ActiveDialog = null;
        SaveCurrentSettings();

        Window? mainWindow = null;
        bool wasWindowVisible = false;
        WindowState savedWindowState = WindowState.Normal;
        PixelPoint? savedPosition = null;
        bool handedOffToBackgroundLoop = false;

        try
        {
            await _inputSimulator.InitializeAsync();

            // Verify that all pattern points fall inside the shared monitor (relevant on multi-monitor Wayland)
            var validationIssues = _inputSimulator.ValidateTargets(ClickPatterns.Select(p => (p.X, p.Y)));
            if (validationIssues.Count > 0)
            {
                string details = validationIssues.Count == 1
                    ? validationIssues[0]
                    : $"{validationIssues[0]}\n\n({validationIssues.Count - 1} more coordinate(s) outside the shared monitor.)";
                ShowWarning("Coordinate Outside Shared Monitor", details);
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

            mainWindow = GetMainWindow();
            if (mainWindow is { IsVisible: true })
            {
                wasWindowVisible = true;
                savedWindowState = mainWindow.WindowState;
                if (savedWindowState == WindowState.Normal)
                {
                    savedPosition = mainWindow.Position;
                }

                // Hide the app window so targets underneath can be clicked reliably.
                mainWindow.Hide();
                await Task.Delay(WindowHideSettleDelayMs, cts.Token);
            }

            handedOffToBackgroundLoop = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _clickEngine.RunAsync(patternSnapshot, settingsSnapshot, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the user stops the loop via button or emergency hotkey
                }
                catch (Exception ex)
                {
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
            // Expected if the user stops during the brief hide-settle delay before the loop starts.
        }
        catch (Exception ex)
        {
            ShowError("Input Initialization Failed", $"Could not start event injection. {ex.Message}");
        }
        finally
        {
            if (!handedOffToBackgroundLoop && IsClicking)
            {
                FinishClicking(mainWindow, wasWindowVisible, savedWindowState, savedPosition);
            }
        }
    }

    [RelayCommand]
    private void StopAutoclick()
    {
        try
        {
            _clickCts?.Cancel();
        }
        catch
        {
            // Ignore if already disposed
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            IsClicking = false;
            _hotkeyListener.StopListening();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
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

    // ─── Wayland-specific & Modal Dialog Commands ──────────────────────

    /// <summary>
    /// Attempts to configure the emergency hotkey via the GNOME GlobalShortcuts portal.
    /// Opens the system dialog where the user can assign a key binding.
    /// </summary>
    [RelayCommand]
    private async Task ConfigureWaylandHotkey()
    {
        ActiveDialog = null;

        var success = await _hotkeyListener.ConfigureHotkeyAsync();

        if (success)
        {
            Console.WriteLine("Portal hotkey configured successfully.");
            ShowWaylandSetupDetails = false;
            SaveCurrentSettings();
        }
        else
        {
            ShowWarning(
                "Portal Configuration Failed",
                "Automatic portal configuration failed. Please use the manual setup below.");
        }

        RefreshWaylandState();
    }

    /// <summary>
    /// Called when the user confirms they have manually configured a custom shortcut.
    /// Unlocks the Start button and persists confirmation.
    /// </summary>
    [RelayCommand]
    private void ConfirmManualSetup()
    {
        _hotkeyListener.ConfirmManualSetup();
        ShowWaylandSetupDetails = false;
        ActiveDialog = null;
        SaveCurrentSettings();
        RefreshWaylandState();
    }

    [RelayCommand]
    private void ToggleWaylandSetupDetails()
    {
        ShowWaylandSetupDetails = !ShowWaylandSetupDetails;
    }

    [RelayCommand]
    private void OpenPortalInfoModal()
    {
        ShowInfo(
            "About Automatic Configuration",
            "When you click 'Configure via Portal', Linux will ask you to set an emergency shortcut.\n\n" +
            "However, on some systems, Linux may silently reject your key combination without warning.\n\n" +
            "When this happens, the automatic method will no longer work for safety reasons. To fix this, simply use the 100% reliable Manual Setup described below.",
            confirmText: "Got it!");
    }

    [RelayCommand]
    private void CloseDialog()
    {
        ActiveDialog = null;
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

        if (value < 1)
        {
            OnPropertyChanged(nameof(MinIntervalMs));
            MinIntervalMs = 1;
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

        if (value < 1)
        {
            OnPropertyChanged(nameof(MaxIntervalMs));
            MaxIntervalMs = 1;
            return;
        }

        // Only adjust the edited field upward so editing Max never drags Min down.
        if (value < MinIntervalMs)
        {
            OnPropertyChanged(nameof(MaxIntervalMs));
            MaxIntervalMs = MinIntervalMs;
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
            MaxIntervalMs = Math.Min(60000, MinIntervalMs + 50);
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

    partial void OnShowWaylandSetupDetailsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWaylandSetupSectionVisible));
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
        OnPropertyChanged(nameof(IsPortalAvailable));
        OnPropertyChanged(nameof(SetupInstructions));
        OnPropertyChanged(nameof(IsWaylandSetupSectionVisible));
        UpdateCanStart();
    }
}
