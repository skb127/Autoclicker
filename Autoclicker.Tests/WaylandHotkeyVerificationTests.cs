using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autoclicker.Core;
using Autoclicker.Platforms.Linux;
using Autoclicker.Services;
using Autoclicker.ViewModels;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Autoclicker.Tests;

public sealed class WaylandHotkeyVerificationTests
{
    [Fact]
    public async Task WaylandHotkeyListener_StartsUnconfigured_AndVerifiesOnFirstDbusStopSignal()
    {
        // Arrange
        var stopService = new WaylandDbusStopService();
        try
        {
            var listener = new WaylandHotkeyListener(stopService);

            int stopRaisedCount = 0;
            listener.OnStopRequested += () => stopRaisedCount++;

            bool requiresManualSetupBeforeSignal = listener.RequiresManualSetup;
            bool isConfiguredBeforeSignal = listener.IsHotkeyConfigured;

            // Act
            await stopService.StopAsync();

            // Assert
            Assert.True(requiresManualSetupBeforeSignal);
            Assert.False(isConfiguredBeforeSignal);
            Assert.True(listener.IsHotkeyConfigured);
            Assert.Equal(1, stopRaisedCount);
        }
        finally
        {
            stopService.Dispose();
        }
    }

    [Fact]
    public void MainViewModel_WhenIdleAndStopSignalArrives_UnlocksStartWithoutStoppingClickLoop()
    {
        // Arrange
        var hotkeyListener = new FakeWaylandHotkeyListener();
        var settingsStore = new InMemorySettingsStore(new AppSettings(
            Pattern: [new Coordinate(100, 200)]));
        var logger = new ListLogger<MainViewModel>();

        var vm = new MainViewModel(
            new FakeInputSimulator(),
            new FakeCoordinatePicker(),
            hotkeyListener,
            settingsStore,
            new FakeScreenBoundsProvider(),
            logger);

        // Act - Open settings while shortcut is unverified
        vm.OpenSettingsCommand.Execute(null);

        // Assert - Start is locked and verification banner is shown
        Assert.True(vm.RequiresManualSetup);
        Assert.False(vm.IsHotkeyReady);
        Assert.True(vm.IsEmergencyKeyPending);
        Assert.False(vm.IsWaylandEmergencyKeyReady);
        Assert.NotNull(vm.StartDisabledReason);
        Assert.False(vm.CanStartAutoclick);

        Assert.True(vm.Settings.IsOpen);
        Assert.Equal((int)SettingsTab.EmergencyKey, vm.Settings.SelectedTabIndex);
        Assert.True(vm.Settings.IsVerificationBannerVisible);

        // Act - Simulate D-Bus emergency shortcut signal
        hotkeyListener.SimulateDbusStopSignal();

        // Assert - Start unlocks, Settings stays open, and click loop was not stopped
        Assert.True(vm.IsHotkeyReady);
        Assert.False(vm.IsEmergencyKeyPending);
        Assert.True(vm.IsWaylandEmergencyKeyReady);
        Assert.Null(vm.StartDisabledReason);
        Assert.True(vm.CanStartAutoclick);

        Assert.True(vm.Settings.IsOpen);
        Assert.True(vm.Settings.IsHotkeyReady);
        Assert.False(vm.Settings.IsVerificationBannerVisible);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Stopping autoclicker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartAutoclick_WhenWaylandShortcutUnverified_OpensEmergencyKeySettingsInsteadOfStarting()
    {
        // Arrange
        var hotkeyListener = new FakeWaylandHotkeyListener();
        var settingsStore = new InMemorySettingsStore(new AppSettings(
            Pattern: [new Coordinate(100, 200)]));

        var vm = new MainViewModel(
            new FakeInputSimulator(),
            new FakeCoordinatePicker(),
            hotkeyListener,
            settingsStore,
            new FakeScreenBoundsProvider());

        // Act
        await vm.StartAutoclickCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsClicking);
        Assert.Null(vm.ActiveDialog);
        Assert.True(vm.Settings.IsOpen);
        Assert.Equal((int)SettingsTab.EmergencyKey, vm.Settings.SelectedTabIndex);
        Assert.True(vm.Settings.IsVerificationBannerVisible);
    }

    [Fact]
    public async Task StartAutoclick_WhenHotkeyVerifiedAndPatternEmpty_ShowsNoCoordinatesWarningDialogAndDoesNotStart()
    {
        // Arrange
        var hotkeyListener = new FakeWaylandHotkeyListener();
        hotkeyListener.SimulateDbusStopSignal();

        var settingsStore = new InMemorySettingsStore(new AppSettings(Pattern: []));
        var logger = new ListLogger<MainViewModel>();

        var vm = new MainViewModel(
            new FakeInputSimulator(),
            new FakeCoordinatePicker(),
            hotkeyListener,
            settingsStore,
            new FakeScreenBoundsProvider(),
            logger);

        // Act
        await vm.StartAutoclickCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsClicking);
        Assert.NotNull(vm.ActiveDialog);
        Assert.Equal(DialogSeverity.Warning, vm.ActiveDialog!.Severity);
        Assert.Equal("No Coordinates in Pattern", vm.ActiveDialog.Title);
        Assert.Contains("at least one coordinate", vm.ActiveDialog.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logger.Messages, m => m.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RestoreDefaultSettings_ResetsClickAndMovementFields_AndPreservesPatternAndGeneralPreferences()
    {
        // Arrange
        var hotkeyListener = new FakeWaylandHotkeyListener();
        var initial = new AppSettings(
            MinIntervalMs: 350,
            MaxIntervalMs: 900,
            RandomizeInterval: true,
            Repetitions: 12,
            Button: MouseButtonKind.Right,
            Action: ClickActionKind.Hold,
            HoldMs: 500,
            JitterPx: 8,
            HumanMovement: true,
            MovementSpeed: MovementSpeedKind.Fast,
            Pattern: [new Coordinate(10, 20), new Coordinate(30, 40)],
            Theme: ThemePreference.Dark,
            StartDelaySeconds: 5,
            AlwaysOnTop: true);
        var settingsStore = new InMemorySettingsStore(initial);

        var vm = new MainViewModel(
            new FakeInputSimulator(),
            new FakeCoordinatePicker(),
            hotkeyListener,
            settingsStore,
            new FakeScreenBoundsProvider());

        var defaults = new AppSettings();

        // Act
        vm.Settings.RequestRestoreDefaultsCommand.Execute(null);
        vm.Settings.ConfirmRestoreDefaultsCommand.Execute(null);

        // Assert
        Assert.Equal(defaults.MinIntervalMs, vm.MinIntervalMs);
        Assert.Equal(defaults.MaxIntervalMs, vm.MaxIntervalMs);
        Assert.Equal(defaults.RandomizeInterval, vm.RandomizeInterval);
        Assert.Equal(defaults.Repetitions, vm.Repetitions);
        Assert.Equal(defaults.Button, vm.SelectedButton);
        Assert.Equal(defaults.Action, vm.SelectedAction);
        Assert.Equal(defaults.HoldMs, vm.HoldMs);
        Assert.Equal(defaults.JitterPx, vm.JitterPx);
        Assert.Equal(defaults.HumanMovement, vm.HumanMovement);
        Assert.Equal(defaults.MovementSpeed, vm.MovementSpeed);

        // Pattern and General tab preferences are preserved.
        Assert.Equal(2, vm.ClickPatterns.Count);
        Assert.Equal(new Coordinate(10, 20), vm.ClickPatterns[0]);
        Assert.Equal(new Coordinate(30, 40), vm.ClickPatterns[1]);
        Assert.Equal(ThemePreference.Dark, vm.Settings.Theme);
        Assert.Equal(5, vm.Settings.StartDelaySeconds);
        Assert.True(vm.Settings.AlwaysOnTop);
    }

    [Fact]
    public async Task RunCountdownAsync_EmitsDescendingSecondsAndResetsOnCompletionOrCancellation()
    {
        // Arrange
        var vm = new MainViewModel(
            new FakeInputSimulator(),
            new FakeCoordinatePicker(),
            new FakeWaylandHotkeyListener(),
            new InMemorySettingsStore(new AppSettings()),
            new FakeScreenBoundsProvider());

        var completedObservedSeconds = new List<int>();
        var completedActiveStates = new List<bool>();
        using var cts = new CancellationTokenSource();

        // Act - Complete full countdown
        await vm.RunCountdownAsync(
            seconds: 3,
            token: cts.Token,
            delayAsync: (_, _) =>
            {
                completedActiveStates.Add(vm.IsCountingDown);
                completedObservedSeconds.Add(vm.CountdownSeconds);
                return Task.CompletedTask;
            });

        // Assert - Emits 3..1 while counting down, then resets
        Assert.All(completedActiveStates, Assert.True);
        Assert.Equal([3, 2, 1], completedObservedSeconds);
        Assert.False(vm.IsCountingDown);
        Assert.Equal(0, vm.CountdownSeconds);

        // Arrange - Mid-countdown cancellation
        var canceledObservedSeconds = new List<int>();
        using var cancelCts = new CancellationTokenSource();

        // Act - Cancel countdown after observing 4
        Exception? cancelException = await Record.ExceptionAsync(async () =>
        {
            await vm.RunCountdownAsync(
                seconds: 5,
                token: cancelCts.Token,
                delayAsync: (_, token) =>
                {
                    canceledObservedSeconds.Add(vm.CountdownSeconds);
                    if (vm.CountdownSeconds == 4)
                    {
                        cancelCts.Cancel();
                    }

                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                });
        });

        // Assert - Throws OperationCanceledException and resets countdown state
        Assert.IsAssignableFrom<OperationCanceledException>(cancelException);
        Assert.Equal([5, 4], canceledObservedSeconds);
        Assert.False(vm.IsCountingDown);
        Assert.Equal(0, vm.CountdownSeconds);
    }

    [Fact]
    public async Task StartAutoclick_WhenCanceledDuringCountdown_ExecutesFinishClickingAndDoesNotRunClickLoop()
    {
        // Arrange
        var hotkeyListener = new FakeWaylandHotkeyListener();
        hotkeyListener.SimulateDbusStopSignal();

        var inputSimulator = new FakeInputSimulator();
        var settingsStore = new InMemorySettingsStore(new AppSettings(
            Pattern: [new Coordinate(100, 200)],
            StartDelaySeconds: 3));

        var vm = new MainViewModel(
            inputSimulator,
            new FakeCoordinatePicker(),
            hotkeyListener,
            settingsStore,
            new FakeScreenBoundsProvider());

        vm.CountdownDelayOverride = (_, token) =>
        {
            // Simulate user clicking Cancel or pressing emergency stop hotkey during the countdown
            vm.StopAutoclickCommand.Execute(null);
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        // Act
        await vm.StartAutoclickCommand.ExecuteAsync(null);

        // Assert
        Assert.False(vm.IsClicking);
        Assert.False(vm.IsCountingDown);
        Assert.Equal(0, vm.CountdownSeconds);
        Assert.Equal(1, vm.FinishClickingCallCount);
        Assert.Equal(0, inputSimulator.ButtonPressCount);
    }

    private sealed class FakeWaylandHotkeyListener : IHotkeyListener
    {
        private bool _isConfigured;

        public event Action? OnStopRequested;

        public bool RequiresManualSetup => true;
        public bool IsHotkeyConfigured => _isConfigured;

        public Task StartListening() => Task.CompletedTask;

        public void StopListening()
        {
        }

        public void SimulateDbusStopSignal()
        {
            _isConfigured = true;
            OnStopRequested?.Invoke();
        }
    }

    private sealed class InMemorySettingsStore(AppSettings initial) : ISettingsStore
    {
        private AppSettings _current = initial;

        public AppSettings Load() => _current;
        public void Save(AppSettings settings) => _current = settings;
    }

    private sealed class FakeInputSimulator : IInputSimulator
    {
        public int ButtonPressCount { get; private set; }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task MoveToAsync(int x, int y) => Task.CompletedTask;

        public Task ButtonAsync(MouseButtonKind button, bool isDown)
        {
            if (isDown)
            {
                ButtonPressCount++;
            }

            return Task.CompletedTask;
        }

        public bool TryGetCursorPosition(out int x, out int y)
        {
            x = 0;
            y = 0;
            return false;
        }
    }

    private sealed class FakeCoordinatePicker : ICoordinatePicker
    {
        public Task<(int X, int Y)?> PickCoordinateAsync() => Task.FromResult<(int X, int Y)?>(null);
    }

    private sealed class FakeScreenBoundsProvider : IScreenBoundsProvider
    {
        public IReadOnlyList<ScreenArea> GetScreens() => [];
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
