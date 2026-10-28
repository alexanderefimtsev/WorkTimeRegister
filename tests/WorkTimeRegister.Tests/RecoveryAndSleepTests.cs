using Microsoft.Data.Sqlite;
using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class RecoveryAndSleepTests
{
    [Theory]
    [InlineData(TimeEntryKind.Work, RecoveryChoice.Exclude, 5)]
    [InlineData(TimeEntryKind.Work, RecoveryChoice.Include, 6)]
    [InlineData(TimeEntryKind.Work, RecoveryChoice.Custom, 5.25)]
    [InlineData(TimeEntryKind.Break, RecoveryChoice.Exclude, 5)]
    [InlineData(TimeEntryKind.Break, RecoveryChoice.Include, 6)]
    [InlineData(TimeEntryKind.Break, RecoveryChoice.Custom, 5.25)]
    public async Task Crash_RequiresDecisionAndNeverCountsDialogWaitingTime(TimeEntryKind kind, RecoveryChoice choice, double hours)
    {
        await using var context = await Context.Create();
        await context.Run(WorkDayOperation.StartDay);
        if (kind == TimeEntryKind.Break) await context.Run(WorkDayOperation.StartBreak);
        context.Clock.Current = context.Clock.Current.AddHours(5);
        await context.Run(WorkDayOperation.Refresh);
        context.Clock.Current = context.Clock.Current.AddHours(1);
        context.Restart();
        var interrupted = await context.Run(WorkDayOperation.Initialize);
        Assert.NotNull(interrupted.Recovery);
        Assert.Equal(TimeSpan.FromHours(5), Total(interrupted, kind));
        Assert.Equal(TimeSpan.FromHours(1), interrupted.Recovery.Duration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Run(WorkDayOperation.StartWork));
        context.Clock.Current = context.Clock.Current.AddMinutes(30);
        var recovered = await context.Service.ExecuteAsync(WorkDayOperation.ResolveRecovery,
            amount: TimeSpan.FromMinutes(15), recovery: choice);
        Assert.Null(recovered.Recovery);
        Assert.True(recovered.Summary.IsPaused);
        Assert.Equal(TimeSpan.FromHours(hours), Total(recovered, kind));
        context.Clock.Current = context.Clock.Current.AddMinutes(10);
        var paused = await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(TimeSpan.FromHours(hours), Total(paused, kind));
        await context.Run(WorkDayOperation.TogglePause);
        context.Clock.Current = context.Clock.Current.AddMinutes(20);
        var active = await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(20), Total(active, kind));
        var ended = await context.Run(WorkDayOperation.EndDay);
        Assert.Null(ended.Summary.ActiveKind);
        Assert.Equal(active.Summary.TotalWork, ended.Summary.TotalWork);
        Assert.Equal(active.Summary.TotalBreak, ended.Summary.TotalBreak);
    }

    [Theory]
    [InlineData(RecoveryChoice.Exclude, 9, 0, 0, 0)]
    [InlineData(RecoveryChoice.Include, 15, 24, 24, 9)]
    [InlineData(RecoveryChoice.Custom, 15, 3, 0, 0)]
    public async Task RecoveryAcrossWeekend_DistributesChosenTimeAcrossCalendarDays(RecoveryChoice choice,
        int friday, int saturday, int sunday, int monday)
    {
        await using var context = await Context.Create(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.FromHours(3)));
        await context.Service.ExecuteAsync(WorkDayOperation.SaveSettings, new WorkDaySettings(AutoContinueAtMidnight: true));
        await context.Run(WorkDayOperation.StartDay);
        context.Clock.Current = context.Clock.Current.AddHours(9);
        await context.Run(WorkDayOperation.Refresh);
        context.Clock.Current = context.Clock.Current.AddDays(2).AddHours(15);
        context.Restart();
        var pending = await context.Run(WorkDayOperation.Initialize);
        Assert.NotNull(pending.Recovery);
        Assert.Equal(1, await context.Scalar("SELECT COUNT(*) FROM WorkDays;"));
        await context.Service.ExecuteAsync(WorkDayOperation.ResolveRecovery, amount: TimeSpan.FromHours(9), recovery: choice);
        var expected = new[] { friday, saturday, sunday, monday };
        for (var index = 0; index < expected.Length; index++)
            Assert.Equal(TimeSpan.FromHours(expected[index]), await context.WorkOn(new DateOnly(2026, 10, 23).AddDays(index)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SleepAcrossWeekend_ExcludesSleepAndHonorsResumeSetting(bool autoResume)
    {
        await using var context = await Context.Create(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.FromHours(3)));
        await context.Service.ExecuteAsync(WorkDayOperation.SaveSettings,
            new WorkDaySettings(AutoContinueAtMidnight: true, ResumeAfterSleep: autoResume));
        await context.Run(WorkDayOperation.StartDay);
        context.Clock.Current = context.Clock.Current.AddHours(9);
        await context.Run(WorkDayOperation.Suspend);
        context.Clock.Current = context.Clock.Current.AddDays(2).AddHours(15);
        var resumed = await context.Run(WorkDayOperation.Resume);
        Assert.Equal(!autoResume, resumed.Summary.IsPaused);
        Assert.Equal(TimeSpan.FromHours(9), await context.WorkOn(new DateOnly(2026, 10, 23)));
        Assert.Equal(TimeSpan.Zero, await context.WorkOn(new DateOnly(2026, 10, 24)));
        Assert.Equal(TimeSpan.Zero, await context.WorkOn(new DateOnly(2026, 10, 25)));
        Assert.Equal(TimeSpan.Zero, resumed.Summary.TotalWork);
        context.Clock.Current = context.Clock.Current.AddMinutes(30);
        var later = await context.Run(WorkDayOperation.Refresh);
        Assert.Equal(autoResume ? TimeSpan.FromMinutes(30) : TimeSpan.Zero, later.Summary.TotalWork);
    }

    [Theory]
    [InlineData(TimeEntryKind.Work, false)]
    [InlineData(TimeEntryKind.Break, false)]
    [InlineData(TimeEntryKind.Work, true)]
    [InlineData(TimeEntryKind.Break, true)]
    public async Task Sleep_DoesNotResumeManualPauseOrLoseExistingTotals(TimeEntryKind kind, bool initiallyPaused)
    {
        await using var context = await Context.Create();
        await context.Service.ExecuteAsync(WorkDayOperation.SaveSettings, new WorkDaySettings(ResumeAfterSleep: true));
        await context.Run(WorkDayOperation.StartDay);
        if (kind == TimeEntryKind.Break) await context.Run(WorkDayOperation.StartBreak);
        context.Clock.Current = context.Clock.Current.AddHours(1);
        if (initiallyPaused) await context.Run(WorkDayOperation.TogglePause);
        await context.Run(WorkDayOperation.Suspend);
        context.Clock.Current = context.Clock.Current.AddHours(2);
        var resumed = await context.Run(WorkDayOperation.Resume);
        Assert.Equal(initiallyPaused, resumed.Summary.IsPaused);
        Assert.Equal(TimeSpan.FromHours(1), Total(resumed, kind));
        Assert.Equal(TimeSpan.FromHours(1), resumed.Summary.CurrentPeriod);
    }

    [Fact]
    public async Task SleepOptionDisabled_PreservesContinuousTimeAccounting()
    {
        await using var context = await Context.Create();
        await context.Service.ExecuteAsync(WorkDayOperation.SaveSettings, new WorkDaySettings(PauseDuringSleep: false));
        await context.Run(WorkDayOperation.StartDay);
        context.Clock.Current = context.Clock.Current.AddHours(1);
        await context.Run(WorkDayOperation.Suspend);
        context.Clock.Current = context.Clock.Current.AddHours(2);
        var result = await context.Run(WorkDayOperation.Resume);
        Assert.False(result.Summary.IsPaused);
        Assert.Equal(TimeSpan.FromHours(3), result.Summary.TotalWork);
    }

    [Fact]
    public async Task CrashWhileAlreadyPaused_PreservesPauseAndNeverAddsUnknownTime()
    {
        await using var context = await Context.Create();
        await context.Run(WorkDayOperation.StartDay);
        context.Clock.Current = context.Clock.Current.AddHours(1);
        await context.Run(WorkDayOperation.TogglePause);
        context.Clock.Current = context.Clock.Current.AddHours(2);
        context.Restart();
        var pending = await context.Run(WorkDayOperation.Initialize);
        Assert.True(pending.Recovery!.WasPaused);
        var resolved = await context.Service.ExecuteAsync(WorkDayOperation.ResolveRecovery, recovery: RecoveryChoice.Include);
        Assert.Equal(TimeSpan.FromHours(1), resolved.Summary.TotalWork);
        Assert.True(resolved.Summary.IsPaused);
    }

    [Fact]
    public async Task InvalidRecoveryAmount_RollsBackAndKeepsPendingDecision()
    {
        await using var context = await Context.Create();
        await context.Run(WorkDayOperation.StartDay);
        context.Clock.Current = context.Clock.Current.AddHours(1);
        context.Restart();
        await context.Run(WorkDayOperation.Initialize);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.ExecuteAsync(
            WorkDayOperation.ResolveRecovery, amount: TimeSpan.FromHours(2), recovery: RecoveryChoice.Custom));
        Assert.NotNull((await context.Run(WorkDayOperation.Refresh)).Recovery);
    }

    private static TimeSpan Total(WorkDaySnapshot snapshot, TimeEntryKind kind) =>
        kind == TimeEntryKind.Work ? snapshot.Summary.TotalWork : snapshot.Summary.TotalBreak;

    [Fact]
    public async Task RecoveryWithoutMidnightContinuation_ExposesAndEnforcesEndOfDayLimit()
    {
        await using var context = await Context.Create(new DateTimeOffset(2026, 10, 28, 23, 0, 0, TimeSpan.FromHours(3)));
        await context.Run(WorkDayOperation.StartDay);
        context.Clock.Current = context.Clock.Current.AddMinutes(30);
        await context.Run(WorkDayOperation.Refresh);
        context.Clock.Current = context.Clock.Current.AddMinutes(90);
        context.Restart();
        var pending = await context.Run(WorkDayOperation.Initialize);
        Assert.Equal(TimeSpan.FromMinutes(90), pending.Recovery!.Duration);
        Assert.Equal(TimeSpan.FromMinutes(30), pending.Recovery.MaximumCountableDuration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.ExecuteAsync(
            WorkDayOperation.ResolveRecovery, recovery: RecoveryChoice.Custom, amount: TimeSpan.FromHours(1)));
        var result = await context.Service.ExecuteAsync(WorkDayOperation.ResolveRecovery, recovery: RecoveryChoice.Include);
        Assert.Null(result.Day);
        Assert.Equal(TimeSpan.FromHours(1), await context.WorkOn(new DateOnly(2026, 10, 28)));
    }

    [Fact]
    public async Task FailedRecoveryLog_RollsBackPausesAndPreservesPendingDecision()
    {
        await using var context = await Context.Create();
        await context.Run(WorkDayOperation.StartDay);
        context.Clock.Current = context.Clock.Current.AddHours(1);
        context.Restart();
        await context.Run(WorkDayOperation.Initialize);
        await context.Scalar("CREATE TRIGGER RejectRecovery BEFORE INSERT ON ActionLog BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => context.Run(WorkDayOperation.ResolveRecovery));
        Assert.Equal(0, await context.Scalar("SELECT COUNT(*) FROM TimePauses;"));
        Assert.NotNull((await context.Run(WorkDayOperation.Refresh)).Recovery);
    }

    [Fact]
    public async Task SleepWithMidnightContinuationDisabled_ClosesDayAndDoesNotReopenIt()
    {
        await using var context = await Context.Create();
        await context.Run(WorkDayOperation.StartDay);
        context.Clock.Current = context.Clock.Current.AddHours(9);
        await context.Run(WorkDayOperation.Suspend);
        context.Clock.Current = context.Clock.Current.AddDays(3);
        var resumed = await context.Run(WorkDayOperation.Resume);
        Assert.Null(resumed.Day);
        Assert.Null(resumed.Summary.ActiveKind);
        Assert.Equal(TimeSpan.FromHours(9), await context.WorkOn(new DateOnly(2026, 10, 28)));
    }

    [Fact]
    public async Task WakeWithoutSuspendNotification_UsesLastCheckpointAndPauses()
    {
        await using var context = await Context.Create();
        await context.Run(WorkDayOperation.StartDay);
        context.Clock.Current = context.Clock.Current.AddHours(1);
        await context.Run(WorkDayOperation.Refresh);
        context.Clock.Current = context.Clock.Current.AddHours(2);
        var result = await context.Run(WorkDayOperation.Resume);
        Assert.True(result.Summary.IsPaused);
        Assert.Equal(TimeSpan.FromHours(1), result.Summary.TotalWork);
    }

    [Fact]
    public async Task RepeatedLaunchBeforeRecovery_PreservesOriginalCheckpoint()
    {
        await using var context = await Context.Create();
        await context.Run(WorkDayOperation.StartDay);
        var confirmed = context.Clock.Current;
        context.Clock.Current = context.Clock.Current.AddHours(1);
        context.Restart();
        await context.Run(WorkDayOperation.Initialize);
        context.Clock.Current = context.Clock.Current.AddHours(1);
        context.Restart();
        var again = await context.Run(WorkDayOperation.Initialize);
        Assert.Equal(confirmed.ToUniversalTime(), again.Recovery!.ConfirmedAt);
        Assert.Equal(TimeSpan.FromHours(2), again.Recovery.Duration);
    }

    private sealed class Context(string path, Clock clock, SqliteTimeEntryRepository repository) : IAsyncDisposable
    {
        public Clock Clock { get; } = clock;
        public SqliteTimeEntryRepository Repository { get; } = repository;
        public WorkDayService Service { get; private set; } = new(repository, clock);
        public static async Task<Context> Create(DateTimeOffset? initial = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"RecoveryTests-{Guid.NewGuid():N}");
            var clock = new Clock(initial ?? new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(3)));
            var path = Path.Combine(directory, "test.db");
            var repository = new SqliteTimeEntryRepository(path);
            await repository.InitializeAsync(clock);
            return new Context(path, clock, repository);
        }
        public void Restart() => Service = new WorkDayService(Repository, Clock);
        public Task<WorkDaySnapshot> Run(WorkDayOperation operation) => Service.ExecuteAsync(operation);
        public async Task<long> Scalar(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        public async Task<TimeSpan> WorkOn(DateOnly date)
        {
            var from = WorkDayService.Boundary(date, Clock.LocalTimeZone);
            var to = WorkDayService.Boundary(date.AddDays(1), Clock.LocalTimeZone);
            return new TimeSummaryCalculator().Calculate(await Repository.GetEntriesAsync(from, to), from, to,
                Clock.GetUtcNow(), await Repository.GetTransfersAsync(from, to), await Repository.GetPausesAsync(from, to)).TotalWork;
        }
        public ValueTask DisposeAsync()
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Clock(DateTimeOffset initial) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = initial;
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("RecoveryTests", initial.Offset, "RecoveryTests", "RecoveryTests");
        public override DateTimeOffset GetUtcNow() => Current.ToUniversalTime();
    }
}
