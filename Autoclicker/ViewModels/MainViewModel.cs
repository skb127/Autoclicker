using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Autoclicker.ViewModels;

// Simple structure to store the pattern points
public record Coordinate(int X, int Y);

public partial class MainViewModel : ViewModelBase
{
    private readonly IInputSimulator _inputSimulator;
    private readonly ICoordinatePicker _coordinatePicker;
    private readonly IHotkeyListener _hotkeyListener;

    [ObservableProperty] private string _coordinatesText = "Selected coordinate: X: 0, Y: 0";

    [ObservableProperty] private bool _isClicking;

    [ObservableProperty] private int _intervalMs = 1000;

    [ObservableProperty] private int _repetitions;

    [ObservableProperty] private string _errorMessage = string.Empty;

    [ObservableProperty] private bool _isPortalInfoModalOpen;

    [ObservableProperty] private bool _canStartAutoclick;

    public ObservableCollection<Coordinate> ClickPatterns { get; } = [];

    // ─── Wayland-specific properties (read from the hotkey listener) ───

    /// <summary>True if the platform requires explicit hotkey setup (Wayland only).</summary>
    public bool RequiresManualSetup => _hotkeyListener.RequiresManualSetup;

    /// <summary>True if the emergency hotkey is configured and ready.</summary>
    public bool IsHotkeyReady => _hotkeyListener.IsHotkeyConfigured;

    /// <summary>True if the GlobalShortcuts portal is available for automatic configuration.</summary>
    public bool IsPortalAvailable => _hotkeyListener.IsPortalAvailable;

    /// <summary>The busctl command the user needs for manual GNOME shortcut setup.</summary>
    public string SetupInstructions => _hotkeyListener.SetupInstructions;

    public MainViewModel(
        IInputSimulator inputSimulator,
        ICoordinatePicker coordinatePicker,
        IHotkeyListener hotkeyListener)
    {
        _inputSimulator = inputSimulator;
        _coordinatePicker = coordinatePicker;
        _hotkeyListener = hotkeyListener;

        // We connect our native emergency key to the method that stops the loop
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

    [RelayCommand]
    private async Task CaptureCoordinateAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            var coordinate = await _coordinatePicker.PickCoordinateAsync();
            // We avoid adding 0,0 if it was a canceled error on Linux
            if (coordinate.X != 0 || coordinate.Y != 0)
            {
                ClickPatterns.Add(new Coordinate(coordinate.X, coordinate.Y));
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
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private async Task StartAutoclick()
    {
        if (IsClicking || ClickPatterns.Count == 0) return;

        if (_hotkeyListener is { RequiresManualSetup: true, IsHotkeyConfigured: false })
        {
            ErrorMessage = "⚠️ Configure the emergency key before starting (see section below).";
            return;
        }

        ErrorMessage = string.Empty;

        try
        {
            await _inputSimulator.InitializeAsync();
            await _hotkeyListener.StartListening();

            IsClicking = true;

            Thread clickThread = new Thread(async void () =>
            {
                try
                {
                    await ClickLoop();
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        ErrorMessage = "Error in loop: " + ex.Message);
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            clickThread.Start();
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
        }
        else
        {
            ErrorMessage = "Portal configuration failed. Please use the manual setup below.";
        }

        RefreshWaylandState();
    }

    /// <summary>
    /// Called when the user confirms they have manually configured a GNOME custom shortcut.
    /// Unlocks the Start button.
    /// </summary>
    [RelayCommand]
    private void ConfirmManualSetup()
    {
        _hotkeyListener.ConfirmManualSetup();
        ErrorMessage = string.Empty;
        RefreshWaylandState();
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

    // ─── State Management ──────────────────────────────────────────────

    /// <summary>Updates CanStartAutoclick based on current state.</summary>
    private void UpdateCanStart()
    {
        // On Win32/X11: RequiresManualSetup=false → always allowed (just !IsClicking)
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
        UpdateCanStart();
    }

    /// <summary>Called by CommunityToolkit.Mvvm when IsClicking changes.</summary>
    partial void OnIsClickingChanged(bool value)
    {
        UpdateCanStart();
    }

    private async Task ClickLoop()
    {
        var watch = Stopwatch.StartNew();
        int currentRepeats = 0;

        try
        {
            while (IsClicking)
            {
                foreach (var point in ClickPatterns)
                {
                    if (!IsClicking) break;

                    await _inputSimulator.SimulateClick(point.X, point.Y);

                    long expectedTicks = watch.ElapsedTicks + (IntervalMs * Stopwatch.Frequency / 1000);
                    while (watch.ElapsedTicks < expectedTicks)
                    {
                        Thread.SpinWait(10);
                    }
                }

                currentRepeats++;

                // If the user configured a limit greater than 0 and it has been reached, we stop
                if (Repetitions > 0 && currentRepeats >= Repetitions)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                ErrorMessage = "Error in loop: " + ex.Message);
        }
        finally
        {
            watch.Stop();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsClicking = false;
                _hotkeyListener.StopListening();
            });
        }
    }
}