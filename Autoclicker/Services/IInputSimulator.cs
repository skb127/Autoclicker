namespace Autoclicker.Services;

public interface IInputSimulator
{
    // Method to simulate a mouse click at a specific position
    void SimulateClick(int x, int y);
}