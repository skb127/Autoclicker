using System;
using System.Collections.Generic;
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
        var stopService = new WaylandDbusStopService();
        try
        {
            var listener = new WaylandHotkeyListener(stopService);

            int stopRaisedCount = 0;
            listener.OnStopRequested += () => stopRaisedCount++;

            Assert.True(listener.RequiresManualSetup);
            Assert.False(listener.IsHotkeyConfigured);

            await stopService.StopAsync();

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

        vm.ShowWaylandSetupDetails = true;

        Assert.True(vm.RequiresManualSetup);
        Assert.False(vm.IsHotkeyReady);
        Assert.True(vm.IsWaylandSetupSectionVisible);
        Assert.False(vm.CanStartAutoclick);

        hotkeyListener.SimulateDbusStopSignal();

        Assert.True(vm.IsHotkeyReady);
        Assert.False(vm.ShowWaylandSetupDetails);
        Assert.False(vm.IsWaylandSetupSectionVisible);
        Assert.True(vm.CanStartAutoclick);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("Stopping autoclicker", StringComparison.Ordinal));
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
        public Task InitializeAsync() => Task.CompletedTask;
        public Task MoveToAsync(int x, int y) => Task.CompletedTask;
        public Task ButtonAsync(MouseButtonKind button, bool isDown) => Task.CompletedTask;

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
