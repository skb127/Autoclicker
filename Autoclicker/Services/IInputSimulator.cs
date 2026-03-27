using System.Threading.Tasks;

namespace Autoclicker.Services;

public interface IInputSimulator
{
    // Ask for user permission and open the session
    Task InitializeAsync();
        
    // Execute the real physical click
    void SimulateClick(int x, int y);
}