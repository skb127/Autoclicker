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

    // DI container will automatically inject native implementations here
    public MainViewModel(
        IInputSimulator inputSimulator, 
        ICoordinatePicker coordinatePicker, 
        IHotkeyListener hotkeyListener)
    {
        _inputSimulator = inputSimulator;
        _coordinatePicker = coordinatePicker;
        _hotkeyListener = hotkeyListener;
    }

    private async Task CaptureCoordinateAsync()
    {
        // Call our interface. In Windows it will open the transparent window.
        var coordinate = await _coordinatePicker.PickCoordinateAsync();
    
        // Update the UI text
        CoordinatesText = $"Selected coordinate: X: {coordinate.X}, Y: {coordinate.Y}";
    }
    
    // Example using CommunityToolkit.Mvvm to generate the UI command
    [RelayCommand]
    private async Task StartAutoclick()
    {
        // 1. Ask the system for permission (it will only do this the first time)
        await _inputSimulator.InitializeAsync();
    
        // 2. Trigger the click
        _inputSimulator.SimulateClick(100, 200);
    }
    
    [RelayCommand]
    private void CaptureCoordinate()
    {
        // Here we will add logic to capture coordinates later
    }
}