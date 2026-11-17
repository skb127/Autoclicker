namespace Autoclicker.ViewModels;

/// <summary>
/// Severity level determining the icon and accent border of <see cref="DialogMessage"/>.
/// </summary>
public enum DialogSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Immutable payload displayed inside the in-window modal dialog.
/// </summary>
public sealed record DialogMessage(
    DialogSeverity Severity,
    string Title,
    string Message,
    string ConfirmText = "OK");
