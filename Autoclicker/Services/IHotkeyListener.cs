using System;

namespace Autoclicker.Services;

public interface IHotkeyListener
{
    // This event is raised when the user presses the emergency stop key
    event Action OnStopRequested;
    void StartListening();
    void StopListening();
}