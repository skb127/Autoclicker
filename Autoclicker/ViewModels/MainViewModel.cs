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

    [ObservableProperty] 
    private string _coordinatesText = "Selected coordinate: X: 0, Y: 0";

    // This property will control if the loop is on or off
    [ObservableProperty]
    private bool _isClicking;
    
    // User-configurable time (in milliseconds)
    [ObservableProperty]
    private int _intervalMs = 1000; 

    // Configurable repetitions (0 = Infinite)
    [ObservableProperty]
    private int _repetitions = 0; 

    // WARNING SYSTEM: Red message in case of error on Linux
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // REQUIREMENT FULFILLED: Click pattern (Multiple coordinates)
    public ObservableCollection<Coordinate> ClickPatterns { get; } = [];

    // DI container will automatically inject native implementations here
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
    }

    [RelayCommand]
    private async Task CaptureCoordinateAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            var coordinate = await _coordinatePicker.PickCoordinateAsync();
            // We avoid adding 0,0 if it was a canceled error on Linux
            if (coordinate.X!= 0 || coordinate.Y!= 0) 
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
        // Do not start if already clicking or if the pattern is empty
        if (IsClicking || ClickPatterns.Count == 0) return;
        ErrorMessage = string.Empty;
        
        try 
        {
            await _inputSimulator.InitializeAsync();
            _hotkeyListener.StartListening();

            IsClicking = true;

            Thread clickThread = new Thread(ClickLoop)
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
    
    private void ClickLoop()
    {
        // High precision timer integrated in .NET
        var watch = Stopwatch.StartNew();
        int currentRepeats = 0;
            
        try 
        {
            while (IsClicking)
            {
                // We iterate over the click pattern defined by the user
                foreach (var point in ClickPatterns)
                {
                    if (!IsClicking) break;

                    _inputSimulator.SimulateClick(point.X, point.Y);

                    // We calculate the wait using our high-precision timer
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
            // We send the error to the GUI safely
            Dispatcher.UIThread.InvokeAsync(() => 
                ErrorMessage = "Error in loop: " + ex.Message);
        }
        finally 
        {
            watch.Stop();
            // We return the state to the interface safely on the UI thread
            Dispatcher.UIThread.InvokeAsync(() => {
                IsClicking = false;
                _hotkeyListener.StopListening();
            });
        }
    }
}