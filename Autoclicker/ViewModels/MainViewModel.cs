using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Autoclicker.ViewModels;

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
    
    private int _targetX;
    private int _targetY;

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
        var coordinate = await _coordinatePicker.PickCoordinateAsync();
        _targetX = coordinate.X;
        _targetY = coordinate.Y;
        CoordinatesText = $"Selected coordinate: X: {_targetX}, Y: {_targetY}";
    }
    
    [RelayCommand]
    private async Task StartAutoclick()
    {
        if (IsClicking) return;

        // 1. Request OS permissions if necessary (Linux Wayland)
        await _inputSimulator.InitializeAsync();
            
        // 2. Start listening for the emergency key (Escape on Windows)
        _hotkeyListener.StartListening();

        IsClicking = true;

        // 3. We start the loop in a dedicated thread with MAXIMUM priority
        Thread clickThread = new Thread(ClickLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        clickThread.Start();
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
            
        // Speed: 1 click every 50 milliseconds (20 clicks per second)
        const int intervaloMs = 50; 

        while (IsClicking)
        {
            _inputSimulator.SimulateClick(_targetX, _targetY);

            // Active wait (SpinWait) to avoid operating system bottlenecks
            long ticksEsperados = watch.ElapsedTicks + (intervaloMs * Stopwatch.Frequency / 1000);
            while (watch.ElapsedTicks < ticksEsperados)
            {
                Thread.SpinWait(10);
            }
        }
        watch.Stop();
    }
}