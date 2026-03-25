using System.Threading.Tasks;

namespace Autoclicker.Services;

public interface ICoordinatePicker
{
    // Async task that returns the coordinates which the user chose
    Task<(int X, int Y)> PickCoordinateAsync();
}