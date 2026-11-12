using System.Threading.Tasks;

namespace Autoclicker.Services;

public interface ICoordinatePicker
{
    /// <summary>
    /// Opens an interactive overlay across all monitors and returns the global coordinate
    /// clicked by the user, or <c>null</c> if the user cancels (e.g., by pressing Escape).
    /// </summary>
    Task<(int X, int Y)?> PickCoordinateAsync();
}
