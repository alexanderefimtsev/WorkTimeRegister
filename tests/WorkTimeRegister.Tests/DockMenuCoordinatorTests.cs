using Avalonia.Controls;
using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using WorkTimeRegister.ViewModels;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class DockMenuCoordinatorTests
{
    [Fact]
    public async Task MenuActions_UpdateImmediatelyAndNeverAssignCommandsToSeparators()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DockMenuTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
        var clock = new MutableClock();
        try
        {
            await repository.InitializeAsync(clock);
            using var viewModel = new MainWindowViewModel(repository, new TimeSummaryCalculator(), clock);
            using var coordinator = new DockMenuCoordinator(viewModel);
            await viewModel.Initialization;
            var mode = Assert.IsType<NativeMenuItem>(coordinator.Menu.Items[0]);
            var pause = Assert.IsType<NativeMenuItem>(coordinator.Menu.Items[1]);
            var separator = Assert.IsType<NativeMenuItemSeparator>(coordinator.Menu.Items[2]);
            var day = Assert.IsType<NativeMenuItem>(coordinator.Menu.Items[3]);
            Assert.Null(separator.Command);
            Assert.Same(viewModel.ToggleWorkDayCommand, day.Command);
            Assert.Same(viewModel.TogglePauseCommand, pause.Command);
            Assert.Equal("Начать рабочий день", day.Header);
            Assert.True(day.IsEnabled);
            Assert.False(mode.IsEnabled);
            Assert.False(pause.IsEnabled);

            day.Command!.Execute(null);
            await WaitForAsync(() => viewModel.IsWorkDayOpen && day.IsEnabled);
            Assert.Equal("Закончить рабочий день", day.Header);
            Assert.Equal("Начать перерыв", mode.Header);
            Assert.True(mode.IsEnabled);
            Assert.True(pause.IsEnabled);

            pause.Command!.Execute(null);
            await WaitForAsync(() => viewModel.IsPaused && pause.IsEnabled);
            Assert.Equal("Продолжить", pause.Header);
            Assert.False(mode.IsEnabled);
            Assert.True(day.IsEnabled);
            pause.Command.Execute(null);
            await WaitForAsync(() => !viewModel.IsPaused && pause.IsEnabled);
            Assert.Equal("Пауза", pause.Header);

            mode.Command!.Execute(null);
            await WaitForAsync(() => viewModel.ActiveKind == TimeEntryKind.Break && mode.IsEnabled);
            Assert.Equal("Продолжить работу", mode.Header);
            pause.Command.Execute(null);
            await WaitForAsync(() => viewModel.IsPaused && pause.IsEnabled);
            day.Command.Execute(null);
            await WaitForAsync(() => !viewModel.IsWorkDayOpen && day.IsEnabled);
            Assert.Equal("Начать рабочий день", day.Header);
            Assert.False(mode.IsEnabled);
            Assert.False(pause.IsEnabled);
            Assert.Null(await repository.GetActiveEntryAsync());

            day.Command.Execute(null);
            await WaitForAsync(() => viewModel.IsWorkDayOpen && day.IsEnabled);
            Assert.Equal("Закончить рабочий день", day.Header);
            coordinator.Dispose();
            await viewModel.ToggleWorkDayAsync();
            Assert.Null(day.Command);
            Assert.False(day.IsEnabled);
            Assert.Equal("Закончить рабочий день", day.Header);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Midnight_UpdatesWorkDayAndPauseAvailabilityWithoutOpeningTheMenu(bool autoContinue)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"DockMenuTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
        var clock = new MutableClock { Now = new DateTimeOffset(2026, 10, 28, 23, 59, 50, TimeSpan.FromHours(3)) };
        try
        {
            await repository.InitializeAsync(clock);
            using var viewModel = new MainWindowViewModel(repository, new TimeSummaryCalculator(), clock);
            using var coordinator = new DockMenuCoordinator(viewModel);
            await viewModel.Initialization;
            await viewModel.SaveSettingsAsync(new WorkDaySettings(AutoContinueAtMidnight: autoContinue));
            await viewModel.ToggleWorkDayAsync();
            clock.Now = clock.Now.AddSeconds(20);
            await viewModel.RefreshAsync();
            var pause = Assert.IsType<NativeMenuItem>(coordinator.Menu.Items[1]);
            var day = Assert.IsType<NativeMenuItem>(coordinator.Menu.Items[3]);
            Assert.Equal(autoContinue ? "Закончить рабочий день" : "Начать рабочий день", day.Header);
            Assert.Equal(autoContinue, pause.IsEnabled);
            Assert.True(day.IsEnabled);
            Assert.True(await viewModel.PrepareForExitAsync());
            Assert.Equal("Начать рабочий день", day.Header);
            Assert.False(day.IsEnabled);
            Assert.False(pause.IsEnabled);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(3));
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("DockMenuTests", TimeSpan.FromHours(3), "DockMenuTests", "DockMenuTests");
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
