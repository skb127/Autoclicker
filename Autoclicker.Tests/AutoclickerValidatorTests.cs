using System;
using System.Collections.Generic;
using Autoclicker.Core;
using Autoclicker.Services;
using Autoclicker.ViewModels;
using Autoclicker.Views.Controls;
using Xunit;

namespace Autoclicker.Tests;

public sealed class AutoclickerValidatorTests
{
    [Fact]
    public void ValidatePreStart_WhenWaylandHotkeyUnverified_ReturnsWaylandHotkeyNotVerifiedFirst()
    {
        // Arrange
        IReadOnlyList<Coordinate> emptyPattern = [];

        // Act
        var result = AutoclickerValidator.ValidatePreStart(
            emptyPattern,
            ClickActionKind.Drag,
            requiresManualHotkeySetup: true,
            isHotkeyConfigured: false);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(ValidationErrorCode.WaylandHotkeyNotVerified, result.Code);
        Assert.NotEmpty(result.Title);
        Assert.NotEmpty(result.UserMessage);
        Assert.NotEmpty(result.LogMessage);
    }

    [Fact]
    public void ValidatePreStart_WhenPatternEmpty_ReturnsEmptyPatternFailure()
    {
        // Arrange
        IReadOnlyList<Coordinate> emptyPattern = [];

        // Act
        var result = AutoclickerValidator.ValidatePreStart(
            emptyPattern,
            ClickActionKind.Click,
            requiresManualHotkeySetup: true,
            isHotkeyConfigured: true);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(ValidationErrorCode.EmptyPattern, result.Code);
        Assert.Equal("No Coordinates in Pattern", result.Title);
        Assert.Contains("at least one coordinate", result.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("empty", result.LogMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatePreStart_WhenDragHasSingleCoordinate_ReturnsDragRequiresAtLeastTwoCoordinates()
    {
        // Arrange
        IReadOnlyList<Coordinate> singlePoint = [new Coordinate(100, 200)];

        // Act
        var result = AutoclickerValidator.ValidatePreStart(
            singlePoint,
            ClickActionKind.Drag,
            requiresManualHotkeySetup: false,
            isHotkeyConfigured: true);

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(ValidationErrorCode.DragRequiresAtLeastTwoCoordinates, result.Code);
        Assert.Equal("Insufficient Coordinates", result.Title);
        Assert.Contains("at least 2 coordinates", result.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CurrentCount=1", result.LogMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ClickActionKind.Click, 1)]
    [InlineData(ClickActionKind.DoubleClick, 1)]
    [InlineData(ClickActionKind.Hold, 1)]
    [InlineData(ClickActionKind.Drag, 2)]
    public void ValidatePreStart_WhenValidClickOrDrag_ReturnsSuccess(ClickActionKind action, int pointCount)
    {
        // Arrange
        var pattern = new List<Coordinate>();
        for (int i = 0; i < pointCount; i++)
        {
            pattern.Add(new Coordinate(100 * (i + 1), 200));
        }

        // Act
        var result = AutoclickerValidator.ValidatePreStart(
            pattern,
            action,
            requiresManualHotkeySetup: true,
            isHotkeyConfigured: true);

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal(ValidationErrorCode.None, result.Code);
    }

    [Fact]
    public void ValidateSharedMonitorTargets_WhenIssuesPresent_FormatsSingleAndMultipleIssues()
    {
        // Arrange
        IReadOnlyList<string> noIssues = [];
        IReadOnlyList<string> singleIssue = ["(2000, 100) is outside monitor DP-1"];
        IReadOnlyList<string> multipleIssues =
        [
            "(2000, 100) is outside monitor DP-1",
            "(2500, 300) is outside monitor DP-1",
            "(3000, 400) is outside monitor DP-1"
        ];

        // Act
        var validResult = AutoclickerValidator.ValidateSharedMonitorTargets(noIssues);
        var singleResult = AutoclickerValidator.ValidateSharedMonitorTargets(singleIssue);
        var multiResult = AutoclickerValidator.ValidateSharedMonitorTargets(multipleIssues);

        // Assert
        Assert.True(validResult.IsValid);

        Assert.False(singleResult.IsValid);
        Assert.Equal(ValidationErrorCode.TargetOutsideSharedMonitor, singleResult.Code);
        Assert.Equal(singleIssue[0], singleResult.UserMessage);

        Assert.False(multiResult.IsValid);
        Assert.Equal(ValidationErrorCode.TargetOutsideSharedMonitor, multiResult.Code);
        Assert.Contains(multipleIssues[0], multiResult.UserMessage, StringComparison.Ordinal);
        Assert.Contains("2 more coordinate(s)", multiResult.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateManualCoordinate_WhenInsideOrOutsideMonitorsOrNegativeWithoutMonitors_ReturnsExpectedResult()
    {
        // Arrange
        IReadOnlyList<ScreenArea> monitors = [new ScreenArea("DP-1", 0, 0, 1920, 1080)];
        IReadOnlyList<ScreenArea> noMonitors = [];

        // Act
        var insideResult = AutoclickerValidator.ValidateManualCoordinate(500, 400, monitors);
        var outsideResult = AutoclickerValidator.ValidateManualCoordinate(2500, 400, monitors);
        var negativeNoMonitorsResult = AutoclickerValidator.ValidateManualCoordinate(-10, 50, noMonitors);
        var positiveNoMonitorsResult = AutoclickerValidator.ValidateManualCoordinate(10, 50, noMonitors);

        // Assert
        Assert.True(insideResult.IsValid);

        Assert.False(outsideResult.IsValid);
        Assert.Equal(ValidationErrorCode.CoordinateOutsideAllMonitors, outsideResult.Code);
        Assert.Contains("DP-1", outsideResult.UserMessage, StringComparison.Ordinal);

        Assert.False(negativeNoMonitorsResult.IsValid);
        Assert.Equal(ValidationErrorCode.NegativeCoordinateWithoutMonitors, negativeNoMonitorsResult.Code);

        Assert.True(positiveNoMonitorsResult.IsValid);
    }

    [Fact]
    public void SettingLimits_ClampsAllNumericRangesWithinBounds()
    {
        // Arrange
        const int belowZero = -10;
        const int aboveAll = 100_000;

        // Act
        int minIntervalLow = SettingLimits.ClampIntervalMs(belowZero);
        int minIntervalHigh = SettingLimits.ClampIntervalMs(aboveAll);
        int maxIntervalBelowMin = SettingLimits.ClampMaxIntervalMs(150, minIntervalMs: 500);
        int maxIntervalHigh = SettingLimits.ClampMaxIntervalMs(aboveAll, minIntervalMs: 500);
        int repsLow = SettingLimits.ClampRepetitions(belowZero);
        int repsHigh = SettingLimits.ClampRepetitions(aboveAll);
        int holdLow = SettingLimits.ClampHoldMs(belowZero);
        int holdHigh = SettingLimits.ClampHoldMs(aboveAll);
        int jitterLow = SettingLimits.ClampJitterPx(belowZero);
        int jitterHigh = SettingLimits.ClampJitterPx(aboveAll);
        int delayLow = SettingLimits.ClampStartDelaySeconds(belowZero);
        int delayHigh = SettingLimits.ClampStartDelaySeconds(aboveAll);

        // Assert
        Assert.Equal(SettingLimits.MinIntervalMs, minIntervalLow);
        Assert.Equal(SettingLimits.MaxIntervalMs, minIntervalHigh);
        Assert.Equal(500, maxIntervalBelowMin);
        Assert.Equal(SettingLimits.MaxIntervalMs, maxIntervalHigh);
        Assert.Equal(SettingLimits.MinRepetitions, repsLow);
        Assert.Equal(SettingLimits.MaxRepetitions, repsHigh);
        Assert.Equal(SettingLimits.MinHoldMs, holdLow);
        Assert.Equal(SettingLimits.MaxHoldMs, holdHigh);
        Assert.Equal(SettingLimits.MinJitterPx, jitterLow);
        Assert.Equal(SettingLimits.MaxJitterPx, jitterHigh);
        Assert.Equal(SettingLimits.MinStartDelaySeconds, delayLow);
        Assert.Equal(SettingLimits.MaxStartDelaySeconds, delayHigh);
    }

    [Fact]
    public void SelectTargetScreenAreas_WhenNotWayland_ReturnsAllMonitors()
    {
        // Arrange
        var screen1 = new ScreenArea("DP-1", 0, 0, 1920, 1080);
        var screen2 = new ScreenArea("HDMI-1", 1920, 0, 1920, 1080);
        IReadOnlyList<ScreenArea> allScreens = [screen1, screen2];
        IReadOnlyList<Coordinate> patternOnSecondMonitor = [new Coordinate(2200, 500)];

        // Act
        var selected = FullScreenCountdownOverlay.SelectTargetScreenAreas(
            allScreens,
            patternOnSecondMonitor,
            singleMonitorTargetOnly: false);

        // Assert
        Assert.Equal(2, selected.Count);
        Assert.Equal([screen1, screen2], selected);
    }

    [Fact]
    public void SelectTargetScreenAreas_WhenWayland_ReturnsOnlyMonitorContainingPattern_WithFallbackToAll()
    {
        // Arrange
        var screen1 = new ScreenArea("DP-1", 0, 0, 1920, 1080);
        var screen2 = new ScreenArea("HDMI-1", 1920, 0, 1920, 1080);
        IReadOnlyList<ScreenArea> allScreens = [screen1, screen2];
        IReadOnlyList<Coordinate> patternOnSecondMonitor = [new Coordinate(2200, 500), new Coordinate(2500, 700)];
        IReadOnlyList<Coordinate> outOfBoundsPattern = [new Coordinate(9000, 9000)];

        // Act
        var waylandMatched = FullScreenCountdownOverlay.SelectTargetScreenAreas(
            allScreens,
            patternOnSecondMonitor,
            singleMonitorTargetOnly: true);
        var waylandFallback = FullScreenCountdownOverlay.SelectTargetScreenAreas(
            allScreens,
            outOfBoundsPattern,
            singleMonitorTargetOnly: true);

        // Assert
        Assert.Single(waylandMatched);
        Assert.Equal(screen2, waylandMatched[0]);
        Assert.Equal(2, waylandFallback.Count);
        Assert.Equal([screen1, screen2], waylandFallback);
    }
}
