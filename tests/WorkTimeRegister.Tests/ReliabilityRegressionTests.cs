using Microsoft.Data.Sqlite;
using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using WorkTimeRegister.ViewModels;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class ReliabilityRegressionTests
{
    [Theory]
    [InlineData(WorkDayOperation.Suspend)]
    [InlineData(WorkDayOperation.StartBreak)]
    [InlineData(WorkDayOperation.EndDay)]
    [InlineData(WorkDayOperation.AdjustTime)]
    public async Task OtherActions_DoNotOverwriteUnknownGap(WorkDayOperation operation)
    {
        await using var c = await Context.Create();
        await c.Start();
        c.Clock.Now += TimeSpan.FromHours(2);
        var gap = await c.Run(operation);
        Assert.Equal(RecoveryOrigin.MissedUpdates, gap.Recovery!.Origin);
        Assert.Equal(TimeSpan.Zero, gap.Summary.TotalWork);
        Assert.True(gap.Day!.IsOpen);
        Assert.Equal(TimeEntryKind.Work, gap.Summary.ActiveKind);
    }

    [Theory]
    [InlineData(TimeEntryKind.Work, false)]
    [InlineData(TimeEntryKind.Work, true)]
    [InlineData(TimeEntryKind.Break, false)]
    [InlineData(TimeEntryKind.Break, true)]
    public async Task MissingNotifications_FreezeBeforeCheckpointOverwrite(TimeEntryKind mode, bool wakeFirst)
    {
        await using var c = await Context.Create();
        await c.Start(mode);
        await c.Alive(TimeSpan.FromHours(1));
        var confirmed = c.Clock.Now;
        c.Clock.Now += TimeSpan.FromHours(2);
        var gap = await c.Run(wakeFirst ? WorkDayOperation.Resume : WorkDayOperation.Refresh);
        Assert.Equal(RecoveryOrigin.MissedUpdates, gap.Recovery!.Origin);
        Assert.Equal(confirmed, gap.Recovery.ConfirmedAt);
        Assert.Equal(TimeSpan.FromHours(2), gap.Recovery.Duration);
        Assert.Equal(TimeSpan.FromHours(1), gap.Summary.CurrentPeriod);
        var again = await c.Run(wakeFirst ? WorkDayOperation.Refresh : WorkDayOperation.Resume);
        Assert.Equal(gap.Recovery, again.Recovery);
        Assert.Equal(confirmed, DateTimeOffset.Parse((string)(await c.Scalar("SELECT LastSeenAt FROM ApplicationState;"))!));
        var resolved = await c.Run(WorkDayOperation.ResolveRecovery);
        Assert.Null(resolved.Recovery);
        Assert.True(resolved.Summary.IsPaused);
        Assert.Equal(TimeSpan.FromHours(1), resolved.Summary.CurrentPeriod);
    }

    [Fact]
    public async Task RetryWithinProcess_DoesNotReportCrash_ButNewProcessDoes()
    {
        await using var c = await Context.Create();
        await c.Start();
        c.Clock.Now += TimeSpan.FromSeconds(2);
        var retry = new WorkDayService(c.Repository, c.Clock);
        Assert.Null((await retry.ExecuteAsync(WorkDayOperation.Initialize)).Recovery);
        var newProcess = new WorkDayService(c.Repository, c.Clock, Guid.NewGuid());
        var crashed = await newProcess.ExecuteAsync(WorkDayOperation.Initialize);
        Assert.Equal(RecoveryOrigin.Crash, crashed.Recovery!.Origin);
    }

    [Fact]
    public async Task DelayedSuspend_ExplainsGap_WithoutLosingOriginalEventTime()
    {
        await using var c = await Context.Create();
        await c.Start();
        await c.Alive(TimeSpan.FromHours(1));
        var sleptAt = c.Clock.Now;
        c.Clock.Now += TimeSpan.FromHours(2);
        Assert.NotNull((await c.Run(WorkDayOperation.Refresh)).Recovery);
        var cycle = Guid.NewGuid();
        var sleep = await c.Service.ExecuteAsync(WorkDayOperation.Suspend, at: sleptAt, powerCycleId: cycle);
        Assert.Null(sleep.Recovery);
        var wake = await c.Service.ExecuteAsync(WorkDayOperation.Resume, powerCycleId: cycle);
        Assert.True(wake.Summary.IsPaused);
        Assert.Equal(TimeSpan.FromHours(1), wake.Summary.TotalWork);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWake_ManualContinueAndNextSleep_KeepNinetyMinutes(bool autoResume)
    {
        await using var c = await Context.Create();
        using var vm = new MainWindowViewModel(c.Repository, new TimeSummaryCalculator(), c.Clock);
        await vm.Initialization;
        await vm.SaveSettingsAsync(new WorkDaySettings(ResumeAfterSleep: autoResume));
        await vm.ToggleWorkDayAsync();
        await c.AliveViewModel(vm, TimeSpan.FromHours(1));
        await vm.HandlePowerEventAsync(true, c.Clock.Now);
        c.Clock.Now += TimeSpan.FromHours(1);
        await c.Sql("CREATE TRIGGER RejectLog BEFORE INSERT ON ActionLog BEGIN SELECT RAISE(ABORT,'write failure'); END;");
        await vm.HandlePowerEventAsync(false, c.Clock.Now);
        Assert.True(vm.HasPendingPowerEvents);
        Assert.True(vm.HasError);
        Assert.True(vm.IsPaused);
        await vm.RefreshAsync();
        Assert.Equal(TimeSpan.FromHours(1), vm.TotalWork);
        await c.Sql("DROP TRIGGER RejectLog;");
        await vm.TogglePauseAsync();
        Assert.False(vm.HasPendingPowerEvents);
        Assert.False(vm.IsPaused);
        await c.AliveViewModel(vm, TimeSpan.FromMinutes(30));
        await vm.HandlePowerEventAsync(true, c.Clock.Now);
        Assert.True(vm.IsPaused);
        c.Clock.Now += TimeSpan.FromHours(1);
        await vm.HandlePowerEventAsync(false, c.Clock.Now);
        Assert.Equal(TimeSpan.FromMinutes(90), vm.TotalWork);
        Assert.Equal(!autoResume, vm.IsPaused);
    }

    [Fact]
    public async Task DelayedSleepInsideGap_OnlyAsksAboutUncertainPart_EvenAfterRelaunch()
    {
        await using var c = await Context.Create();
        await c.Start();
        await c.Alive(TimeSpan.FromHours(1));
        var sleptAt = c.Clock.Now.AddMinutes(30);
        c.Clock.Now += TimeSpan.FromHours(2);
        Assert.Equal(TimeSpan.FromHours(2), (await c.Run(WorkDayOperation.Refresh)).Recovery!.Duration);
        var clarified = await c.Service.ExecuteAsync(WorkDayOperation.Suspend, at: sleptAt, powerCycleId: Guid.NewGuid());
        Assert.Equal(TimeSpan.FromMinutes(30), clarified.Recovery!.Duration);
        c.Clock.Now += TimeSpan.FromMinutes(10);
        var reopened = await new WorkDayService(c.Repository, c.Clock, Guid.NewGuid()).ExecuteAsync(WorkDayOperation.Initialize);
        Assert.Equal(clarified.Recovery, reopened.Recovery);
        var chosen = await c.Service.ExecuteAsync(WorkDayOperation.ResolveRecovery, recovery: RecoveryChoice.Include);
        Assert.Equal(TimeSpan.FromMinutes(90), chosen.Summary.TotalWork);
        Assert.True(chosen.Summary.IsPaused);
    }

    [Fact]
    public async Task FailedSuspend_RetryKeepsItsTimestamp_AndManualPauseIsNotAutoResumed()
    {
        await using var c = await Context.Create();
        using var vm = new MainWindowViewModel(c.Repository, new TimeSummaryCalculator(), c.Clock);
        await vm.Initialization;
        await vm.SaveSettingsAsync(new WorkDaySettings(ResumeAfterSleep: true));
        await vm.ToggleWorkDayAsync();
        await c.AliveViewModel(vm, TimeSpan.FromHours(1));
        var sleptAt = c.Clock.Now;
        await c.Sql("CREATE TRIGGER RejectLog BEFORE INSERT ON ActionLog BEGIN SELECT RAISE(ABORT,'write failure'); END;");
        await vm.HandlePowerEventAsync(true, sleptAt);
        Assert.True(vm.HasPendingPowerEvents);
        c.Clock.Now += TimeSpan.FromHours(2);
        await vm.RetryPowerEventsAsync();
        Assert.True(vm.HasPendingPowerEvents);
        await c.Sql("DROP TRIGGER RejectLog;");
        // The user's Pause must stay a manual pause even if retry already applied the sleep pause.
        await vm.TogglePauseAsync();
        Assert.True(vm.IsPaused);
        Assert.False(vm.HasPendingPowerEvents);
        Assert.Equal(TimeSpan.FromHours(1), vm.TotalWork);
        c.Clock.Now += TimeSpan.FromSeconds(1);
        await vm.HandlePowerEventAsync(false, c.Clock.Now);
        Assert.True(vm.IsPaused);
        Assert.Equal(sleptAt, DateTimeOffset.Parse((string)(await c.Scalar("SELECT StartedAt FROM TimePauses LIMIT 1;"))!));
    }

    [Theory]
    [InlineData(false, RecoveryChoice.Exclude, 30, 0)]
    [InlineData(false, RecoveryChoice.Include, 60, 0)]
    [InlineData(true, RecoveryChoice.Exclude, 30, 0)]
    [InlineData(true, RecoveryChoice.Include, 60, 60)]
    [InlineData(true, RecoveryChoice.Custom, 60, 15)]
    public async Task UnknownGapAcrossMidnight_PreservesCorrectionsAndChosenAmount(bool autoContinue,
        RecoveryChoice choice, int yesterday, int today)
    {
        await using var c = await Context.Create();
        c.Clock.Now = new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);
        await c.Service.ExecuteAsync(WorkDayOperation.SaveSettings, new WorkDaySettings(AutoContinueAtMidnight: autoContinue));
        await c.Start();
        await c.Service.ExecuteAsync(WorkDayOperation.AdjustTime, adjustment: TimeAdjustmentOperation.Add, amount: TimeSpan.FromMinutes(10));
        await c.Alive(TimeSpan.FromMinutes(30));
        c.Clock.Now += TimeSpan.FromMinutes(90);
        Assert.NotNull((await c.Run(WorkDayOperation.Refresh)).Recovery);
        var resolved = await c.Service.ExecuteAsync(WorkDayOperation.ResolveRecovery, recovery: choice, amount: TimeSpan.FromMinutes(45));
        Assert.Null(resolved.Recovery);
        Assert.Equal(TimeSpan.FromMinutes(today), resolved.Summary.TotalWork);
        var from = new DateTimeOffset(2026, 10, 27, 21, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(1);
        var previous = new TimeSummaryCalculator().Calculate(await c.Repository.GetEntriesAsync(from, to), from, to,
            c.Clock.Now, await c.Repository.GetTransfersAsync(from, to), await c.Repository.GetPausesAsync(from, to));
        Assert.Equal(TimeSpan.FromMinutes(yesterday + 10), previous.TotalWork);
    }

    [Fact]
    public async Task DuplicateAndOldWake_DoNotRemoveManualOrNewSleepPause()
    {
        await using var c = await Context.Create();
        await c.Service.ExecuteAsync(WorkDayOperation.SaveSettings, new WorkDaySettings(ResumeAfterSleep: true));
        await c.Start();
        var first = Guid.NewGuid();
        await c.Alive(TimeSpan.FromMinutes(30));
        await c.Service.ExecuteAsync(WorkDayOperation.Suspend, powerCycleId: first);
        c.Clock.Now += TimeSpan.FromHours(1);
        var wakeAt = c.Clock.Now;
        await c.Service.ExecuteAsync(WorkDayOperation.Resume, powerCycleId: first);
        await c.Run(WorkDayOperation.TogglePause);
        c.Clock.Now += TimeSpan.FromSeconds(1);
        Assert.True((await c.Service.ExecuteAsync(WorkDayOperation.Resume, powerCycleId: first)).Summary.IsPaused);
        Assert.True((await c.Service.ExecuteAsync(WorkDayOperation.Resume, at: wakeAt, powerCycleId: first)).Summary.IsPaused);
        await c.Run(WorkDayOperation.TogglePause);
        var second = Guid.NewGuid();
        c.Clock.Now += TimeSpan.FromSeconds(20);
        await c.Service.ExecuteAsync(WorkDayOperation.Suspend, powerCycleId: second);
        c.Clock.Now += TimeSpan.FromSeconds(20);
        Assert.True((await c.Service.ExecuteAsync(WorkDayOperation.Resume, powerCycleId: first)).Summary.IsPaused);
        Assert.False((await c.Service.ExecuteAsync(WorkDayOperation.Resume, powerCycleId: second)).Summary.IsPaused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PausedOrClosedDay_DoesNotAskAboutTimerGap(bool paused)
    {
        await using var c = await Context.Create();
        await c.Start();
        await c.Run(paused ? WorkDayOperation.TogglePause : WorkDayOperation.EndDay);
        c.Clock.Now += TimeSpan.FromHours(2);
        var after = await c.Run(WorkDayOperation.Refresh);
        Assert.Null(after.Recovery);
        Assert.Equal(TimeSpan.Zero, after.Summary.TotalWork);
    }

    [Fact]
    public async Task DisabledSleepPause_DoesNotInterruptContinuousAccounting()
    {
        await using var c = await Context.Create();
        await c.Service.ExecuteAsync(WorkDayOperation.SaveSettings, new WorkDaySettings(PauseDuringSleep: false));
        await c.Start();
        c.Clock.Now += TimeSpan.FromHours(3);
        var after = await c.Run(WorkDayOperation.Refresh);
        Assert.Null(after.Recovery);
        Assert.Equal(TimeSpan.FromHours(3), after.Summary.TotalWork);
    }

    [Fact]
    public async Task ManualCommandAndExit_CannotSilentlyConfirmGap()
    {
        await using var c = await Context.Create();
        using var vm = new MainWindowViewModel(c.Repository, new TimeSummaryCalculator(), c.Clock);
        await vm.Initialization;
        await vm.ToggleWorkDayAsync();
        c.Clock.Now += TimeSpan.FromHours(2);
        Assert.False(await vm.PrepareForExitAsync());
        Assert.True(vm.IsWorkDayOpen);
        Assert.NotNull(vm.PendingRecovery);
        Assert.False(vm.IsReady);
        Assert.True(await vm.ResolveRecoveryAsync(RecoveryChoice.Exclude));
        Assert.True(await vm.PrepareForExitAsync());
        Assert.False(vm.IsWorkDayOpen);
    }

    [Fact]
    public async Task UnknownReasonAndCheckpoint_PersistAcrossRelaunch()
    {
        await using var c = await Context.Create();
        await c.Start();
        c.Clock.Now += TimeSpan.FromHours(2);
        var before = await c.Run(WorkDayOperation.Refresh);
        c.Clock.Now += TimeSpan.FromMinutes(10);
        var after = await new WorkDayService(c.Repository, c.Clock, Guid.NewGuid()).ExecuteAsync(WorkDayOperation.Initialize);
        Assert.Equal(RecoveryOrigin.MissedUpdates, after.Recovery!.Origin);
        Assert.Equal(before.Recovery!.ConfirmedAt, after.Recovery.ConfirmedAt);
    }

    [Fact]
    public async Task BackwardClock_DoesNotOverwriteCheckpoint()
    {
        await using var c = await Context.Create();
        await c.Start();
        var checkpoint = await c.Scalar("SELECT LastSeenAt FROM ApplicationState;");
        c.Clock.Now -= TimeSpan.FromMinutes(5);
        var conflict = await Assert.ThrowsAsync<ClockConflictException>(() => c.Run(WorkDayOperation.Refresh));
        Assert.Equal(c.Clock.Now, conflict.CurrentAt);
        Assert.Equal(DateTimeOffset.Parse((string)checkpoint!), conflict.SavedAt);
        var frozen = await c.Run(WorkDayOperation.Inspect);
        Assert.Equal(TimeSpan.Zero, frozen.Summary.TotalWork);
        Assert.Equal(checkpoint, await c.Scalar("SELECT LastSeenAt FROM ApplicationState;"));
    }

    [Fact]
    public async Task BackwardClock_OpensReadOnlyWindowAndResumesThroughRecovery()
    {
        await using var c = await Context.Create();
        await c.Start();
        await c.Alive(TimeSpan.FromMinutes(10));
        var confirmed = c.Clock.Now;
        c.Clock.Now -= TimeSpan.FromHours(1);
        using var vm = new MainWindowViewModel(c.Repository, new TimeSummaryCalculator(), c.Clock, Guid.NewGuid());
        await vm.Initialization;
        Assert.True(vm.HasLoaded);
        Assert.True(vm.HasClockConflict);
        Assert.False(vm.IsReady);
        Assert.Equal(TimeSpan.FromMinutes(10), vm.TotalWork);
        Assert.Contains("последняя сохранённая отметка", vm.ClockConflictMessage);
        Assert.False(vm.StartBreakCommand.CanExecute(null));
        Assert.False(await vm.RetryClockAsync());
        Assert.True(vm.HasClockConflict);
        var log = vm.CreateLogViewModel();
        Assert.False(log.CanImport);
        await using var exported = new MemoryStream();
        Assert.True(await log.ExportAsync(exported));
        c.Clock.Now = confirmed.AddMinutes(1);
        Assert.True(await vm.RetryClockAsync());
        Assert.False(vm.HasClockConflict);
        Assert.True(log.CanImport);
        Assert.NotNull(vm.PendingRecovery);
        Assert.Equal(TimeSpan.FromMinutes(10), vm.TotalWork);
        Assert.True(await vm.ResolveRecoveryAsync(RecoveryChoice.Exclude));
        Assert.True(vm.IsPaused);
    }

    [Fact]
    public async Task BackwardClock_DuringRunningSessionFreezesTotalsUntilClockIsFixed()
    {
        await using var c = await Context.Create();
        using var vm = new MainWindowViewModel(c.Repository, new TimeSummaryCalculator(), c.Clock);
        await vm.Initialization;
        await vm.ToggleWorkDayAsync();
        await c.AliveViewModel(vm, TimeSpan.FromMinutes(5));
        var confirmed = c.Clock.Now;
        c.Clock.Now -= TimeSpan.FromMinutes(2);
        await vm.RefreshAsync();
        Assert.True(vm.HasClockConflict);
        Assert.Equal(TimeSpan.FromMinutes(5), vm.TotalWork);
        Assert.False(vm.ToggleWorkDayCommand.CanExecute(null));
        Assert.False(await vm.RetryClockAsync());
        c.Clock.Now = confirmed.AddSeconds(1);
        Assert.True(await vm.RetryClockAsync());
        Assert.True(vm.IsReady);
        Assert.Equal(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1), vm.TotalWork);
    }

    [Fact]
    public async Task RecoveryLog_UsesReadableLocalTime_AndKeepsCheckpointPrecision()
    {
        await using var c = await Context.Create();
        await c.Start();
        c.Clock.Now += TimeSpan.FromTicks(1234567);
        await c.Run(WorkDayOperation.TogglePause);
        await c.Run(WorkDayOperation.TogglePause);
        var confirmed = c.Clock.Now;
        c.Clock.Now += TimeSpan.FromHours(1) + TimeSpan.FromTicks(7654321);
        var pending = await new WorkDayService(c.Repository, c.Clock, Guid.NewGuid()).ExecuteAsync(WorkDayOperation.Initialize);
        Assert.Equal(confirmed, pending.Recovery!.ConfirmedAt);
        await c.Run(WorkDayOperation.ResolveRecovery);
        var log = (string)(await c.Scalar("SELECT Action FROM ActionLog ORDER BY Id DESC LIMIT 1;"))!;
        Assert.Contains("28.10.2026 12:00:00", log);
        Assert.Contains("01:00:00", log);
        Assert.DoesNotContain("1234567", log);
        Assert.DoesNotContain("T09:", log);
    }

    [Fact]
    public void MultiDayDuration_IsExplicitAndHasNoFractions()
    {
        Assert.Equal("2 дн. 03:15:00", RecoveryPresentation.Duration(TimeSpan.FromDays(2) + TimeSpan.FromHours(3.25) + TimeSpan.FromTicks(12345)));
        Assert.Contains("мог включать вашу работу", RecoveryPresentation.Explanation(RecoveryOrigin.LegacyUpgrade));
    }

    private sealed class Context(string directory, SqliteTimeEntryRepository repository, Clock clock) : IAsyncDisposable
    {
        public SqliteTimeEntryRepository Repository { get; } = repository;
        public Clock Clock { get; } = clock;
        public WorkDayService Service { get; } = new(repository, clock);
        public static async Task<Context> Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"Reliability161-{Guid.NewGuid():N}");
            var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
            await repository.InitializeAsync();
            return new Context(directory, repository, new Clock());
        }
        public Task<WorkDaySnapshot> Run(WorkDayOperation operation) => Service.ExecuteAsync(operation);
        public async Task Start(TimeEntryKind mode = TimeEntryKind.Work)
        {
            await Run(WorkDayOperation.Initialize);
            await Run(WorkDayOperation.StartDay);
            if (mode == TimeEntryKind.Break) await Run(WorkDayOperation.StartBreak);
        }
        public async Task Alive(TimeSpan amount)
        {
            var end = Clock.Now + amount;
            while (Clock.Now < end)
            {
                Clock.Now = Clock.Now.AddSeconds(30) < end ? Clock.Now.AddSeconds(30) : end;
                Assert.Null((await Run(WorkDayOperation.Refresh)).Recovery);
            }
        }
        public async Task AliveViewModel(MainWindowViewModel vm, TimeSpan amount)
        {
            var end = Clock.Now + amount;
            while (Clock.Now < end)
            {
                Clock.Now = Clock.Now.AddSeconds(30) < end ? Clock.Now.AddSeconds(30) : end;
                await vm.RefreshAsync();
                Assert.Null(vm.PendingRecovery);
            }
        }
        public async Task<object?> Scalar(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "test.db")}");
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync();
        }
        public async Task Sql(string sql) => await Scalar(sql);
        public ValueTask DisposeAsync() { Directory.Delete(directory, true); return ValueTask.CompletedTask; }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Regression", TimeSpan.FromHours(3), "Regression", "Regression");
    }
}
