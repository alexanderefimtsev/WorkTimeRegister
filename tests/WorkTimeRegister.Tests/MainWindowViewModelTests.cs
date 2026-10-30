using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using WorkTimeRegister.ViewModels;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task ModeCommands_FollowActiveModeAndPause()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.FromHours(3));
        var timeProvider = new MutableTimeProvider(now);

        try
        {
            await repository.InitializeAsync();

            using var viewModel = CreateViewModel(repository, timeProvider);
            await viewModel.Initialization;
            Assert.False(viewModel.StartWorkCommand.CanExecute(null));
            Assert.False(viewModel.StartBreakCommand.CanExecute(null));
            Assert.False(viewModel.TogglePauseCommand.CanExecute(null));
            Assert.Equal("Начать рабочий день", viewModel.WorkDayActionText);
            Assert.True(viewModel.ToggleWorkDayCommand.CanExecute(null));
            Assert.False(viewModel.ModeSwitchCommand.CanExecute(null));
            await viewModel.ToggleWorkDayAsync();
            await WaitForAsync(() => viewModel.ActiveKind == TimeEntryKind.Work);
            Assert.False(viewModel.StartWorkCommand.CanExecute(null));
            Assert.True(viewModel.StartBreakCommand.CanExecute(null));
            Assert.True(viewModel.TogglePauseCommand.CanExecute(null));
            Assert.Equal("Начать перерыв", viewModel.ModeSwitchActionText);
            Assert.True(viewModel.ModeSwitchCommand.CanExecute(null));
            viewModel.StartBreakCommand.Execute(null);
            await WaitForAsync(() => viewModel.ActiveKind == TimeEntryKind.Break);
            Assert.True(viewModel.StartWorkCommand.CanExecute(null));
            Assert.False(viewModel.StartBreakCommand.CanExecute(null));
            Assert.True(viewModel.TogglePauseCommand.CanExecute(null));
            Assert.Equal("Продолжить работу", viewModel.ModeSwitchActionText);
            Assert.True(viewModel.ModeSwitchCommand.CanExecute(null));
            await viewModel.TogglePauseAsync();
            await WaitForAsync(() => viewModel.IsPaused);
            Assert.False(viewModel.StartWorkCommand.CanExecute(null));
            Assert.False(viewModel.StartBreakCommand.CanExecute(null));
            Assert.True(viewModel.TogglePauseCommand.CanExecute(null));
            Assert.True(viewModel.EndWorkCommand.CanExecute(null));
            Assert.Equal("Продолжить работу", viewModel.ModeSwitchActionText);
            Assert.False(viewModel.ModeSwitchCommand.CanExecute(null));
            await viewModel.ToggleWorkDayAsync();
            Assert.Equal("Рабочий день завершён", viewModel.CurrentPeriodTitle);
            Assert.False(viewModel.StartWorkCommand.CanExecute(null));
            Assert.False(viewModel.StartBreakCommand.CanExecute(null));
            Assert.False(viewModel.TogglePauseCommand.CanExecute(null));
            Assert.Equal("Начать рабочий день", viewModel.WorkDayActionText);
            Assert.False(viewModel.ModeSwitchCommand.CanExecute(null));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ModeSwitchCommand_ChangesModeAndWritesExistingLogActions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
        var now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.FromHours(3));
        var timeProvider = new MutableTimeProvider(now);

        try
        {
            await repository.InitializeAsync();
            using var viewModel = CreateViewModel(repository, timeProvider);
            await viewModel.Initialization;
            Assert.False(viewModel.ModeSwitchCommand.CanExecute(null));
            await viewModel.ToggleWorkDayAsync();
            Assert.Equal(TimeEntryKind.Work, viewModel.ActiveKind);

            viewModel.ModeSwitchCommand.Execute(null);
            await WaitForAsync(() => viewModel.ActiveKind == TimeEntryKind.Break);
            await WaitForAsync(() => viewModel.ModeSwitchCommand.CanExecute(null));

            viewModel.ModeSwitchCommand.Execute(null);
            await WaitForAsync(() => viewModel.ActiveKind == TimeEntryKind.Work);

            var dayStart = new DateTimeOffset(now.Date, now.Offset);
            var log = await repository.GetActionLogAsync(dayStart, dayStart.AddDays(1));
            Assert.Equal(
                ["Окончание перерыва", "Начало перерыва", "Начало рабочего дня"],
                log.Select(record => record.Action));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task TogglePauseAsync_FreezesAndResumesActiveMode()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
        var timeProvider = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(3)));

        try
        {
            await repository.InitializeAsync();
            await repository.StartAsync(TimeEntryKind.Work, timeProvider.GetLocalNow().AddHours(-1));
            using var viewModel = new MainWindowViewModel(
                repository,
                new TimeSummaryCalculator(),
                timeProvider);

            await viewModel.TogglePauseAsync();

            Assert.True(viewModel.IsPaused);
            Assert.Equal("На паузе…", viewModel.CurrentPeriodTitle);
            Assert.Equal(TimeSpan.FromHours(1), viewModel.CurrentPeriodValue);
            Assert.Equal("Продолжить", viewModel.PauseActionText);

            timeProvider.Advance(TimeSpan.FromMinutes(30));
            await viewModel.TogglePauseAsync();

            Assert.False(viewModel.IsPaused);
            Assert.Equal("Работаю…", viewModel.CurrentPeriodTitle);
            Assert.Equal(TimeSpan.FromHours(1), viewModel.CurrentPeriodValue);

            await AdvanceAlive(viewModel, timeProvider, TimeSpan.FromMinutes(15));
            await viewModel.TogglePauseAsync();

            Assert.True(viewModel.IsPaused);
            Assert.Equal(TimeSpan.FromMinutes(75), viewModel.CurrentPeriodValue);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExitAndRelaunch_PersistSettingsAndRestorePausedBreakWithoutCountingTheGap()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(3)));
        try
        {
            await repository.InitializeAsync(timeProvider);
            using (var viewModel = CreateViewModel(repository, timeProvider))
            {
                await viewModel.Initialization;
                Assert.False(viewModel.IsWorkDayOpen);
                Assert.False(viewModel.StartWorkCommand.CanExecute(null));
                Assert.True(await viewModel.SaveSettingsAsync(new WorkDaySettings(RestoreOnLaunch: true)));
                Assert.False(viewModel.IsWorkDayOpen);
                await viewModel.ToggleWorkDayAsync();
                await AdvanceAlive(viewModel, timeProvider, TimeSpan.FromMinutes(30));
                viewModel.StartBreakCommand.Execute(null);
                await WaitForAsync(() => viewModel.ActiveKind == TimeEntryKind.Break);
                await AdvanceAlive(viewModel, timeProvider, TimeSpan.FromMinutes(5));
                await viewModel.TogglePauseAsync();
                Assert.True(await viewModel.PrepareForExitAsync());
                Assert.False(viewModel.IsWorkDayOpen);
                Assert.False(viewModel.ToggleWorkDayCommand.CanExecute(null));
                Assert.Null(await repository.GetActiveEntryAsync());
            }
            timeProvider.Advance(TimeSpan.FromHours(2));
            using (var restored = CreateViewModel(repository, timeProvider))
            {
                await restored.Initialization;
                Assert.True(restored.IsWorkDayOpen);
                Assert.True(restored.Settings.RestoreOnLaunch);
                Assert.True(restored.IsPaused);
                Assert.Equal(TimeEntryKind.Break, restored.ActiveKind);
                Assert.Equal(TimeSpan.FromMinutes(30), restored.TotalWork);
                Assert.Equal(TimeSpan.FromMinutes(5), restored.TotalBreak);
                Assert.Equal(TimeSpan.Zero, restored.CurrentPeriodValue);
                Assert.False(restored.ModeSwitchCommand.CanExecute(null));
                Assert.True(restored.TogglePauseCommand.CanExecute(null));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static async Task AdvanceAlive(MainWindowViewModel viewModel, MutableTimeProvider clock, TimeSpan amount)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < amount; elapsed += TimeSpan.FromSeconds(30))
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await viewModel.RefreshAsync();
        }
    }

    private static MainWindowViewModel CreateViewModel(
        ITimeEntryRepository repository,
        TimeProvider timeProvider) =>
        new(repository, new TimeSummaryCalculator(), timeProvider);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private readonly TimeZoneInfo timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "WorkTimeRegisterTests",
            current.Offset,
            "WorkTimeRegisterTests",
            "WorkTimeRegisterTests");

        public override TimeZoneInfo LocalTimeZone => timeZone;
        public override DateTimeOffset GetUtcNow() => current.ToUniversalTime();

        public void Advance(TimeSpan value) => current = current.Add(value);
    }
}
