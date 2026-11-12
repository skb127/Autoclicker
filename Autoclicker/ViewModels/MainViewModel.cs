using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Core;
using Autoclicker.Services;
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
    private readonly IInputSimulator _inputSimulator;
    private readonly ICoordinatePicker _coordinatePicker;
    private readonly IHotkeyListener _hotkeyListener;
    private readonly ISettingsStore _settingsStore;
    private readonly ClickEngine _clickEngine;

    private CancellationTokenSource? _clickCts;
    private bool _isLoadingSettings;

    [ObservableProperty] private string _coordinatesText = "Selected coordinate: X: 0, Y: 0";

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

    // ─── Pattern Selection ─────────────────────────────────────────────

    [ObservableProperty] private int _selectedPatternIndex = -1;

    // ─── Status & Modals ───────────────────────────────────────────────

    [ObservableProperty] private string _errorMessage = string.Empty;

    [ObservableProperty] private bool _isPortalInfoModalOpen;

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
        ISettingsStore settingsStore)
    {
        _inputSimulator = inputSimulator;
        _coordinatePicker = coordinatePicker;
        _hotkeyListener = hotkeyListener;
        _settingsStore = settingsStore;
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
            Pattern: ClickPatterns.ToList(),
            WaylandHotkeyConfirmed: _hotkeyListener.RequiresManualSetup && _hotkeyListener.IsHotkeyConfigured);

        _settingsStore.Save(settings);
    }

    // ─── Pattern Management Commands ───────────────────────────────────

    [RelayCommand]
    private async Task CaptureCoordinateAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            var coordinate = await _coordinatePicker.PickCoordinateAsync();
            if (coordinate is { } picked)
            {
                ClickPatterns.Add(new Coordinate(picked.X, picked.Y));
                SelectedPatternIndex = ClickPatterns.Count - 1;
                CoordinatesText = $"Selected coordinate: X: {picked.X}, Y: {picked.Y}";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "Warning: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ClearPattern()
    {
        ClickPatterns.Clear();
        SelectedPatternIndex = -1;
        ErrorMessage = string.Empty;
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
            ErrorMessage = "⚠️ Drag action requires at least 2 coordinates in the pattern (start and end points).";
            return;
        }

        if (_hotkeyListener is { RequiresManualSetup: true, IsHotkeyConfigured: false })
        {
            ErrorMessage = "⚠️ Configure the emergency key before starting (see section below).";
            return;
        }

        ErrorMessage = string.Empty;
        SaveCurrentSettings();

        try
        {
            await _inputSimulator.InitializeAsync();

            // Verify that all pattern points fall inside the shared monitor (relevant on multi-monitor Wayland)
            var validationIssues = _inputSimulator.ValidateTargets(ClickPatterns.Select(p => (p.X, p.Y)));
            if (validationIssues.Count > 0)
            {
                ErrorMessage = "⚠️ " + validationIssues[0];
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
                        ErrorMessage = "Error in loop: " + ex.Message);
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsClicking = false;
                        _hotkeyListener.StopListening();
                    });
                }
            });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Linux compatibility failure: Could not start event injection. {ex.Message}";
            IsClicking = false;
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

        IsClicking = false;
        _hotkeyListener.StopListening();
    }

    // ─── Wayland-specific commands ─────────────────────────────────────

    /// <summary>
    /// Attempts to configure the emergency hotkey via the GNOME GlobalShortcuts portal.
    /// Opens the system dialog where the user can assign a key binding.
    /// </summary>
    [RelayCommand]
    private async Task ConfigureWaylandHotkey()
    {
        ErrorMessage = string.Empty;

        var success = await _hotkeyListener.ConfigureHotkeyAsync();

        if (success)
        {
            Console.WriteLine("Portal hotkey configured successfully.");
            ShowWaylandSetupDetails = false;
            SaveCurrentSettings();
        }
        else
        {
            ErrorMessage = "Portal configuration failed. Please use the manual setup below.";
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
        ErrorMessage = string.Empty;
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
        IsPortalInfoModalOpen = true;
    }

    [RelayCommand]
    private void ClosePortalInfoModal()
    {
        IsPortalInfoModalOpen = false;
    }

    // ─── Property Change Callbacks & State Synchronization ─────────────

    partial void OnMinIntervalMsChanged(int value)
    {
        if (_isLoadingSettings) return;

        if (value < 1)
        {
            MinIntervalMs = 1;
            return;
        }

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
            MaxIntervalMs = 1;
            return;
        }

        if (value < MinIntervalMs)
        {
            MinIntervalMs = value;
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
