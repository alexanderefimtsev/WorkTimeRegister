using Microsoft.Data.Sqlite;
using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class WorkDayServiceTests
{
    [Fact]
    public async Task StartEndAndReopen_PreserveTheSameDayAndExcludeClosedTime()
    {
        await using var context = await Context.CreateAsync();
        var initial = await context.Run(WorkDayOperation.Initialize);
        Assert.Null(initial.Day);
        Assert.Equal(new WorkDaySettings(), initial.Settings);
        var started = await context.Run(WorkDayOperation.StartDay);
        Assert.True(started.Day!.IsOpen);
        Assert.Equal(TimeEntryKind.Work, started.Summary.ActiveKind);
        await context.Advance(TimeSpan.FromHours(1));
        await context.Run(WorkDayOperation.StartBreak);
        await context.Advance(TimeSpan.FromMinutes(15));
        var ended = await context.Run(WorkDayOperation.EndDay);
        Assert.False(ended.Day!.IsOpen);
        Assert.Null(ended.Summary.ActiveKind);
        Assert.Equal(TimeSpan.FromHours(1), ended.Summary.TotalWork);
        Assert.Equal(TimeSpan.FromMinutes(15), ended.Summary.TotalBreak);
        await context.Advance(TimeSpan.FromHours(2));
        var restarted = await context.Run(WorkDayOperation.StartDay);
        Assert.Equal(started.Day.Id, restarted.Day!.Id);
        Assert.Equal(TimeEntryKind.Work, restarted.Summary.ActiveKind);
        Assert.Equal(TimeSpan.Zero, restarted.Summary.CurrentPeriod);
        await context.Advance(TimeSpan.FromMinutes(10));
        var current = await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(TimeSpan.FromMinutes(70), current.Summary.TotalWork);
        Assert.Equal(TimeSpan.FromMinutes(15), current.Summary.TotalBreak);
        Assert.Equal(1, await context.CountAsync("WorkDays"));
    }

    [Fact]
    public async Task AutoStart_OnlyStartsAnUnstartedDateAndDoesNotBypassRestoreOrManualEnd()
    {
        await using var context = await Context.CreateAsync();
        await context.Settings(new WorkDaySettings(AutoStartOnLaunch: true));
        var started = await context.Run(WorkDayOperation.Initialize);
        Assert.True(started.Day!.IsOpen);
        await context.Run(WorkDayOperation.Exit);
        Assert.False((await context.Run(WorkDayOperation.Initialize)).Day!.IsOpen);
        await context.Run(WorkDayOperation.StartDay);
        await context.Settings(new WorkDaySettings(true, true, true));
        await context.Run(WorkDayOperation.EndDay);
        await context.Run(WorkDayOperation.Exit);
        Assert.False((await context.Run(WorkDayOperation.Initialize)).Day!.IsOpen);
        await context.Advance(TimeSpan.FromDays(1));
        var tomorrow = await context.Run(WorkDayOperation.Initialize);
        Assert.True(tomorrow.Day!.IsOpen);
        Assert.NotEqual(started.Day.Id, tomorrow.Day.Id);
        Assert.Equal(TimeSpan.Zero, tomorrow.Summary.TotalWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitAndRestore_RestoreLastModeAndPauseButNeverCountTheExitGap(bool paused)
    {
        await using var context = await Context.CreateAsync();
        await context.Settings(new WorkDaySettings(RestoreOnLaunch: true));
        var day = await context.Run(WorkDayOperation.StartDay);
        await context.Advance(TimeSpan.FromMinutes(20));
        await context.Run(WorkDayOperation.StartBreak);
        await context.Advance(TimeSpan.FromMinutes(5));
        if (paused)
            await context.Run(WorkDayOperation.TogglePause);
        var exited = await context.Run(WorkDayOperation.Exit);
        Assert.Equal(WorkDayCloseReason.ApplicationExit, exited.Day!.CloseReason);
        Assert.Equal(TimeEntryKind.Break, exited.Day.LastMode);
        Assert.Equal(paused, exited.Day.WasPaused);
        await context.Advance(TimeSpan.FromHours(2));
        var restored = await context.Run(WorkDayOperation.Initialize);
        Assert.Equal(day.Day!.Id, restored.Day!.Id);
        Assert.Equal(TimeEntryKind.Break, restored.Summary.ActiveKind);
        Assert.Equal(paused, restored.Summary.IsPaused);
        Assert.Equal(TimeSpan.Zero, restored.Summary.CurrentPeriod);
        Assert.Equal(TimeSpan.FromMinutes(5), restored.Summary.TotalBreak);
        await context.Advance(TimeSpan.FromMinutes(10));
        restored = await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(TimeSpan.FromMinutes(paused ? 5 : 15), restored.Summary.TotalBreak);
        Assert.Equal(TimeSpan.FromMinutes(20), restored.Summary.TotalWork);
    }

    [Fact]
    public async Task RestoreDisabledAndDifferentDate_DoNotRestartAClosedDay()
    {
        await using var context = await Context.CreateAsync();
        await context.Run(WorkDayOperation.StartDay);
        await context.Run(WorkDayOperation.Exit);
        Assert.False((await context.Run(WorkDayOperation.Initialize)).Day!.IsOpen);
        await context.Settings(new WorkDaySettings(RestoreOnLaunch: true, AutoContinueAtMidnight: true));
        await context.Advance(TimeSpan.FromDays(2));
        Assert.Null((await context.Run(WorkDayOperation.Initialize)).Day);
        Assert.Equal(1, await context.CountAsync("WorkDays"));
    }

    [Theory]
    [InlineData(TimeEntryKind.Work, false, false)]
    [InlineData(TimeEntryKind.Work, false, true)]
    [InlineData(TimeEntryKind.Break, false, false)]
    [InlineData(TimeEntryKind.Break, false, true)]
    [InlineData(TimeEntryKind.Work, true, false)]
    [InlineData(TimeEntryKind.Work, true, true)]
    [InlineData(TimeEntryKind.Break, true, false)]
    [InlineData(TimeEntryKind.Break, true, true)]
    public async Task Midnight_SplitsAtExactBoundaryAndRespectsContinuation(TimeEntryKind mode, bool paused, bool autoContinue)
    {
        var start = new DateTimeOffset(2026, 10, 28, 23, 59, 50, TimeSpan.FromHours(3));
        await using var context = await Context.CreateAsync(start);
        await context.Settings(new WorkDaySettings(AutoContinueAtMidnight: autoContinue));
        var yesterday = await context.Run(WorkDayOperation.StartDay);
        if (mode == TimeEntryKind.Break)
            await context.Run(WorkDayOperation.StartBreak);
        if (paused)
        {
            await context.Advance(TimeSpan.FromSeconds(5));
            await context.Run(WorkDayOperation.TogglePause);
        }
        context.At(start.AddSeconds(20));
        var today = await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(new DateOnly(2026, 10, 29), today.Date);
        if (autoContinue)
        {
            Assert.True(today.Day!.IsOpen);
            Assert.NotEqual(yesterday.Day!.Id, today.Day.Id);
            Assert.Equal(mode, today.Summary.ActiveKind);
            Assert.Equal(paused, today.Summary.IsPaused);
            Assert.Equal(TimeSpan.FromSeconds(paused ? 0 : 10), today.Summary.CurrentPeriod);
        }
        else
        {
            Assert.Null(today.Day);
            Assert.Null(today.Summary.ActiveKind);
        }
        var previousStart = WorkDayService.Boundary(new DateOnly(2026, 10, 28), context.Clock.LocalTimeZone);
        var previousEnd = WorkDayService.Boundary(new DateOnly(2026, 10, 29), context.Clock.LocalTimeZone);
        var entries = await context.Repository.GetEntriesAsync(previousStart, previousEnd);
        Assert.Equal(yesterday.Day!.DayEnd, entries.Last().EndedAt);
        Assert.All(entries, entry => Assert.True(entry.EndedAt <= yesterday.Day.DayEnd));
        var pauses = await context.Repository.GetPausesAsync(previousStart, previousEnd);
        var previousSummary = new TimeSummaryCalculator().Calculate(entries, yesterday.Day!.DayStart,
            yesterday.Day.DayEnd, context.Clock.GetUtcNow(), pauses: pauses);
        Assert.Equal(TimeSpan.FromSeconds(paused ? 5 : 10), mode == TimeEntryKind.Work ? previousSummary.TotalWork : previousSummary.TotalBreak);
        var logs = await context.Repository.GetActionLogAsync(start.AddDays(-1), start.AddDays(2));
        var close = Assert.Single(logs, log => log.Action == "Автоматическое завершение рабочего дня");
        Assert.Equal(new DateOnly(2026, 10, 28), close.WorkDayDate);
        Assert.False(close.IsWorkDayOpen);
        Assert.Null(close.CurrentMode);
        var logCount = logs.Count;
        await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(logCount, (await context.Repository.GetActionLogAsync(start.AddDays(-1), start.AddDays(2))).Count);
    }

    [Fact]
    public async Task WakeAfterSeveralDays_ReconcilesEveryBoundaryOnce()
    {
        var start = new DateTimeOffset(2026, 10, 28, 23, 0, 0, TimeSpan.FromHours(3));
        await using var context = await Context.CreateAsync(start);
        await context.Settings(new WorkDaySettings(AutoContinueAtMidnight: true));
        await context.Run(WorkDayOperation.StartDay);
        await context.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(2));
        var today = await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(TimeSpan.FromHours(1), today.Summary.TotalWork);
        Assert.Equal(4, await context.CountAsync("WorkDays"));
        await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(4, await context.CountAsync("WorkDays"));
        await context.Run(WorkDayOperation.EndDay);
        await context.Advance(TimeSpan.FromDays(2));
        await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(4, await context.CountAsync("WorkDays"));
    }

    [Fact]
    public async Task Midnight_DoesNotCarryManualCorrectionsIntoTheNewDay()
    {
        var start = new DateTimeOffset(2026, 10, 28, 23, 59, 50, TimeSpan.FromHours(3));
        await using var context = await Context.CreateAsync(start);
        await context.Settings(new WorkDaySettings(AutoContinueAtMidnight: true));
        await context.Run(WorkDayOperation.StartDay);
        await context.Service.ExecuteAsync(WorkDayOperation.AdjustTime, adjustment: TimeAdjustmentOperation.Add,
            category: TimeEntryKind.Work, amount: TimeSpan.FromHours(2));
        await context.Advance(TimeSpan.FromSeconds(20));
        var today = await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(TimeSpan.FromSeconds(10), today.Summary.TotalWork);
        Assert.Equal(TimeSpan.FromSeconds(10), today.Summary.CurrentPeriod);
    }

    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public async Task CalendarBoundaries_RespectDaylightSavingTime(int year, int month, int day, int hours)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var date = new DateOnly(year, month, day);
        Assert.Equal(TimeSpan.FromHours(hours), WorkDayService.Boundary(date.AddDays(1), zone) - WorkDayService.Boundary(date, zone));
        await using var context = await Context.CreateAsync(WorkDayService.Boundary(date, zone), zone);
        await context.Settings(new WorkDaySettings(AutoContinueAtMidnight: true));
        await context.Run(WorkDayOperation.StartDay);
        await context.Advance(WorkDayService.Boundary(date.AddDays(1), zone).AddHours(1) - context.Clock.Current);
        var today = await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(date.AddDays(1), today.Date);
        Assert.Equal(TimeSpan.FromHours(1), today.Summary.TotalWork);
        var entries = await context.Repository.GetEntriesAsync(WorkDayService.Boundary(date, zone), WorkDayService.Boundary(date.AddDays(1), zone));
        Assert.Equal(TimeSpan.FromHours(hours), Assert.Single(entries).EndedAt - entries[0].StartedAt);
    }

    [Fact]
    public async Task FailedLogWrite_RollsBackDayPeriodAndPauseChanges()
    {
        await using var context = await Context.CreateAsync();
        await context.SqlAsync("CREATE TRIGGER RejectDayLog BEFORE INSERT ON ActionLog BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => context.Run(WorkDayOperation.StartDay));
        Assert.Equal(0, await context.CountAsync("WorkDays"));
        Assert.Equal(0, await context.CountAsync("TimeEntries"));
        await context.SqlAsync("DROP TRIGGER RejectDayLog;");
        await context.Run(WorkDayOperation.StartDay);
        await context.Run(WorkDayOperation.TogglePause);
        await context.SqlAsync("CREATE TRIGGER RejectDayLog BEFORE INSERT ON ActionLog BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => context.Run(WorkDayOperation.Exit));
        var stillOpen = await context.Run(WorkDayOperation.Refresh);
        Assert.True(stillOpen.Day!.IsOpen);
        Assert.True(stillOpen.Summary.IsPaused);
    }

    [Fact]
    public async Task ClosedDay_RejectsModeAndPauseCommands()
    {
        await using var context = await Context.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Run(WorkDayOperation.StartWork));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Run(WorkDayOperation.StartBreak));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Run(WorkDayOperation.TogglePause));
        Assert.Equal(0, await context.CountAsync("TimeEntries"));
    }

    private sealed class Context(string directory, SqliteTimeEntryRepository repository, Clock clock) : IAsyncDisposable
    {
        public string DatabasePath => Path.Combine(directory, "test.db");
        public SqliteTimeEntryRepository Repository { get; } = repository;
        public Clock Clock { get; } = clock;
        public WorkDayService Service { get; } = new(repository, clock);

        public static async Task<Context> CreateAsync(DateTimeOffset? at = null, TimeZoneInfo? zone = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"WorkDayTests-{Guid.NewGuid():N}");
            var clock = new Clock(at ?? new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(3)), zone);
            var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
            await repository.InitializeAsync(clock);
            return new Context(directory, repository, clock);
        }

        public Task<WorkDaySnapshot> Run(WorkDayOperation operation) => Service.ExecuteAsync(operation);
        public Task<WorkDaySnapshot> Settings(WorkDaySettings value) => Service.ExecuteAsync(WorkDayOperation.SaveSettings, value);
        public async Task Advance(TimeSpan amount)
        {
            var end = Clock.Current + amount;
            while (Clock.Current < end)
            {
                Clock.Current = Clock.Current.AddSeconds(30) < end ? Clock.Current.AddSeconds(30) : end;
                await Service.ExecuteAsync(WorkDayOperation.Refresh);
            }
        }
        public void At(DateTimeOffset at) => Clock.Current = at;

        public async Task<long> CountAsync(string table)
        {
            await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task SqlAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(directory, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Clock(DateTimeOffset current, TimeZoneInfo? zone = null) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = current;
        public override TimeZoneInfo LocalTimeZone { get; } = zone ?? TimeZoneInfo.CreateCustomTimeZone(
            "WorkDayTests", current.Offset, "WorkDayTests", "WorkDayTests");
        public override DateTimeOffset GetUtcNow() => Current.ToUniversalTime();
    }
}
