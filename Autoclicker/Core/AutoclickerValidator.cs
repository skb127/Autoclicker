using System;
using System.Collections.Generic;
using System.Linq;
using Autoclicker.Services;
using Autoclicker.ViewModels;

namespace Autoclicker.Core;

/// <summary>
/// Categorizes validation failures across autoclicker actions so callers can handle
/// each case uniformly (for example, opening Wayland shortcut setup vs. showing a warning dialog).
/// </summary>
public enum ValidationErrorCode
{
    None = 0,
    WaylandHotkeyNotVerified,
    EmptyPattern,
    DragRequiresAtLeastTwoCoordinates,
    TargetOutsideSharedMonitor,
    CoordinateOutsideAllMonitors,
    NegativeCoordinateWithoutMonitors
}

/// <summary>
/// Immutable result returned by <see cref="AutoclickerValidator"/> containing both
/// user-facing dialog strings and a structured diagnostic message for logging.
/// </summary>
public sealed record ValidationResult(
    bool IsValid,
    ValidationErrorCode Code,
    string Title,
    string UserMessage,
    string LogMessage)
{
    public static ValidationResult Success { get; } =
        new(true, ValidationErrorCode.None, string.Empty, string.Empty, string.Empty);

    public static ValidationResult Failure(
        ValidationErrorCode code,
        string title,
        string userMessage,
        string logMessage) =>
        new(false, code, title, userMessage, logMessage);
}

/// <summary>
/// Centralized numeric limits and clamping helpers for autoclicker and general preferences.
/// </summary>
public static class SettingLimits
{
    public const int MinIntervalMs = 1;
    public const int MaxIntervalMs = 60000;
    public const int DefaultRandomMaxStepMs = 50;
    public const int ShortIntervalWarningThresholdMs = 300;

    public const int MinRepetitions = 0;
    public const int MaxRepetitions = 9999;

    public const int MinHoldMs = 1;
    public const int MaxHoldMs = 60000;

    public const int MinJitterPx = 0;
    public const int MaxJitterPx = 50;

    public const int MinStartDelaySeconds = 0;
    public const int MaxStartDelaySeconds = 60;

    public static int ClampIntervalMs(int value) =>
        Math.Clamp(value, MinIntervalMs, MaxIntervalMs);

    public static int ClampMaxIntervalMs(int maxIntervalMs, int minIntervalMs) =>
        Math.Clamp(maxIntervalMs, ClampIntervalMs(minIntervalMs), MaxIntervalMs);

    public static int ClampRepetitions(int value) =>
        Math.Clamp(value, MinRepetitions, MaxRepetitions);

    public static int ClampHoldMs(int value) =>
        Math.Clamp(value, MinHoldMs, MaxHoldMs);

    public static int ClampJitterPx(int value) =>
        Math.Clamp(value, MinJitterPx, MaxJitterPx);

    public static int ClampStartDelaySeconds(int value) =>
        Math.Clamp(value, MinStartDelaySeconds, MaxStartDelaySeconds);
}

/// <summary>
/// Centralized pure domain validator for autoclicker pre-start preconditions,
/// shared-monitor target checks, and manual coordinate entry.
/// </summary>
public static class AutoclickerValidator
{
    /// <summary>
    /// Evaluates preconditions required before initializing input simulation or starting a click loop:
    /// 1) Wayland emergency shortcut verification, 2) non-empty coordinate pattern,
    /// 3) minimum coordinate count for Drag actions.
    /// </summary>
    public static ValidationResult ValidatePreStart(
        IReadOnlyList<Coordinate> pattern,
        ClickActionKind action,
        bool requiresManualHotkeySetup,
        bool isHotkeyConfigured)
    {
        if (requiresManualHotkeySetup && !isHotkeyConfigured)
        {
            return ValidationResult.Failure(
                ValidationErrorCode.WaylandHotkeyNotVerified,
                "Emergency Key Required",
                "Press your emergency stop shortcut once to verify it for this session before starting.",
                "Cannot start autoclicker: Wayland emergency hotkey is not configured");
        }

        if (pattern.Count == 0)
        {
            return ValidationResult.Failure(
                ValidationErrorCode.EmptyPattern,
                "No Coordinates in Pattern",
                "Add at least one coordinate to the click sequence before starting (use 'Add Coordinate to Pattern' or enter X and Y manually).",
                "Cannot start autoclicker: click pattern is empty (Count=0)");
        }

        if (action == ClickActionKind.Drag && pattern.Count < 2)
        {
            return ValidationResult.Failure(
                ValidationErrorCode.DragRequiresAtLeastTwoCoordinates,
                "Insufficient Coordinates",
                "Drag action requires at least 2 coordinates in the pattern (start and end points).",
                $"Cannot start autoclicker: Drag action requires at least 2 coordinates (CurrentCount={pattern.Count})");
        }

        return ValidationResult.Success;
    }

    /// <summary>
    /// Validates target-bounds issues returned by <see cref="IInputSimulator.ValidateTargets"/>.
    /// </summary>
    public static ValidationResult ValidateSharedMonitorTargets(IReadOnlyList<string> validationIssues)
    {
        if (validationIssues.Count == 0)
        {
            return ValidationResult.Success;
        }

        string details = validationIssues.Count == 1
            ? validationIssues[0]
            : $"{validationIssues[0]}\n\n({validationIssues.Count - 1} more coordinate(s) outside the shared monitor.)";

        return ValidationResult.Failure(
            ValidationErrorCode.TargetOutsideSharedMonitor,
            "Coordinate Outside Shared Monitor",
            details,
            $"Cannot start autoclicker: {validationIssues.Count} coordinate(s) outside shared monitor ({validationIssues[0]})");
    }

    /// <summary>
    /// Validates manually entered screen coordinates against detected monitor rectangles,
    /// or rejects negative coordinates when monitor bounds are unavailable.
    /// </summary>
    public static ValidationResult ValidateManualCoordinate(
        int x,
        int y,
        IReadOnlyList<ScreenArea> screens)
    {
        if (screens.Count > 0)
        {
            bool isInsideAnyScreen = screens.Any(s => s.Contains(x, y));
            if (!isInsideAnyScreen)
            {
                string monitorList = string.Join(
                    "\n",
                    screens.Select(s =>
                        $"• {s.Name}: X [{s.X}..{s.Right - 1}], Y [{s.Y}..{s.Bottom - 1}] ({s.Width}×{s.Height})"));

                return ValidationResult.Failure(
                    ValidationErrorCode.CoordinateOutsideAllMonitors,
                    "Coordinate Out of Bounds",
                    $"The coordinate (X: {x}, Y: {y}) is outside all connected monitors.\n\nAvailable monitors:\n{monitorList}",
                    $"Rejected manual coordinate ({x}, {y}): outside all {screens.Count} connected monitor(s)");
            }
        }
        else if (x < 0 || y < 0)
        {
            return ValidationResult.Failure(
                ValidationErrorCode.NegativeCoordinateWithoutMonitors,
                "Invalid Coordinate",
                $"Monitor bounds could not be detected, and negative coordinates (X: {x}, Y: {y}) are not allowed.",
                $"Rejected negative manual coordinate ({x}, {y}) with no detected monitors");
        }

        return ValidationResult.Success;
    }
}
