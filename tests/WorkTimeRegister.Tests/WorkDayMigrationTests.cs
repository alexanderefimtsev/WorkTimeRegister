using Microsoft.Data.Sqlite;
using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class WorkDayMigrationTests
{
    [Fact]
    public async Task Migration_BacksUpSplitsPeriodsAndPausesAndPreservesDailyTotals()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkDayMigrationTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.db");
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 29, 1, 0, 0, TimeSpan.FromHours(3)));
        var previousStart = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.FromHours(3));
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE TimeEntries(Id INTEGER PRIMARY KEY AUTOINCREMENT, Kind INTEGER NOT NULL,
                        StartedAt TEXT NOT NULL, EndedAt TEXT NULL, CurrentPeriodAdjustmentSeconds INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE TimePauses(Id INTEGER PRIMARY KEY AUTOINCREMENT,TimeEntryId INTEGER NOT NULL,
                        StartedAt TEXT NOT NULL,EndedAt TEXT NULL);
                    CREATE TABLE TimeTransfers(Id INTEGER PRIMARY KEY AUTOINCREMENT,RegisteredAt TEXT NOT NULL,
                        WorkAdjustmentSeconds INTEGER NOT NULL,BreakAdjustmentSeconds INTEGER NOT NULL);
                    CREATE TABLE ActionLog(Id INTEGER PRIMARY KEY AUTOINCREMENT,RegisteredAt TEXT NOT NULL,Action TEXT NOT NULL,
                        CurrentMode INTEGER NULL,TotalWorkSeconds INTEGER NOT NULL,TotalBreakSeconds INTEGER NOT NULL,
                        CurrentPeriodSeconds INTEGER NOT NULL,IsPaused INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO TimeEntries VALUES(1,0,'2026-10-27T20:00:00.0000000Z','2026-10-27T22:00:00.0000000Z',0);
                    INSERT INTO TimeEntries VALUES(2,1,'2026-10-28T20:00:00.0000000Z',NULL,120);
                    INSERT INTO TimePauses VALUES(1,2,'2026-10-28T20:45:00.0000000Z','2026-10-28T21:15:00.0000000Z');
                    INSERT INTO TimeTransfers VALUES(1,'2026-10-28T20:30:00.0000000Z',-600,600);
                    INSERT INTO ActionLog VALUES(1,'2026-10-28T20:30:00.0000000Z','Перенос',1,3000,2400,2400,0);
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var repository = new SqliteTimeEntryRepository(path);
            await repository.InitializeAsync(clock);
            var backupPath = Assert.Single(Directory.GetFiles(directory, "legacy.db.before-workdays-*.bak"));
            await using (var backup = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly"))
            {
                await backup.OpenAsync(TestContext.Current.CancellationToken);
                using var command = backup.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM TimeEntries;";
                Assert.Equal(2L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='WorkDays';";
                Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
            var entries = await repository.GetEntriesAsync(previousStart.AddDays(-1), previousStart.AddDays(2));
            Assert.Equal(4, entries.Count);
            var pauses = await repository.GetPausesAsync(previousStart, previousStart.AddDays(2));
            Assert.Equal(2, pauses.Count);
            var transfers = await repository.GetTransfersAsync(previousStart, previousStart.AddDays(1));
            var yesterday = new TimeSummaryCalculator().Calculate(entries, previousStart, previousStart.AddDays(1),
                clock.GetUtcNow(), transfers, pauses);
            Assert.Equal(TimeSpan.FromMinutes(50), yesterday.TotalWork);
            Assert.Equal(TimeSpan.FromMinutes(55), yesterday.TotalBreak);
            var service = new WorkDayService(repository, clock);
            Assert.NotNull((await service.ExecuteAsync(WorkDayOperation.Initialize)).Recovery);
            var today = await service.ExecuteAsync(WorkDayOperation.ResolveRecovery, recovery: RecoveryChoice.Include);
            Assert.True(today.Day!.IsOpen);
            Assert.Equal(TimeEntryKind.Break, today.Summary.ActiveKind);
            Assert.Equal(TimeSpan.FromMinutes(45), today.Summary.TotalBreak);
            Assert.Equal(TimeSpan.FromMinutes(47), today.Summary.CurrentPeriod);
            Assert.True(today.Summary.IsPaused);
            var log = Assert.Single(await repository.GetActionLogAsync(previousStart, previousStart.AddDays(1)));
            Assert.Equal("Перенос", log.Action);
            Assert.Equal(TimeSpan.FromMinutes(50), log.TotalWork);
            Assert.Equal(TimeSpan.FromMinutes(40), log.TotalBreak);
            Assert.Equal(new DateOnly(2026, 10, 28), log.WorkDayDate);
            await repository.InitializeAsync(clock);
            Assert.Single(Directory.GetFiles(directory, "legacy.db.before-workdays-*.bak"));
            Assert.Equal(4, (await repository.GetEntriesAsync(previousStart.AddDays(-1), previousStart.AddDays(2))).Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Migration_SplitsAnOpenPauseAndKeepsTheCurrentDayPaused()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkDayMigrationTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.db");
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 29, 1, 0, 0, TimeSpan.FromHours(3)));
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE TimeEntries(Id INTEGER PRIMARY KEY AUTOINCREMENT,Kind INTEGER NOT NULL,StartedAt TEXT NOT NULL,
                        EndedAt TEXT NULL,CurrentPeriodAdjustmentSeconds INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE TimePauses(Id INTEGER PRIMARY KEY AUTOINCREMENT,TimeEntryId INTEGER NOT NULL,StartedAt TEXT NOT NULL,EndedAt TEXT NULL);
                    INSERT INTO TimeEntries VALUES(1,0,'2026-10-28T20:00:00.0000000Z',NULL,0);
                    INSERT INTO TimePauses VALUES(1,1,'2026-10-28T20:45:00.0000000Z',NULL);
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var repository = new SqliteTimeEntryRepository(path);
            await repository.InitializeAsync(clock);
            var service = new WorkDayService(repository, clock);
            Assert.NotNull((await service.ExecuteAsync(WorkDayOperation.Initialize)).Recovery);
            var snapshot = await service.ExecuteAsync(WorkDayOperation.ResolveRecovery, recovery: RecoveryChoice.Exclude);
            Assert.True(snapshot.Summary.IsPaused);
            Assert.Equal(TimeSpan.Zero, snapshot.Summary.TotalWork);
            Assert.Equal(TimeSpan.Zero, snapshot.Summary.CurrentPeriod);
            var pauses = await repository.GetPausesAsync(clock.GetUtcNow().AddDays(-1), clock.GetUtcNow().AddDays(1));
            Assert.Equal(2, pauses.Count);
            Assert.Single(pauses, pause => pause.EndedAt is null);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FixedClock(DateTimeOffset current) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("MigrationTests", current.Offset, "MigrationTests", "MigrationTests");
        public override DateTimeOffset GetUtcNow() => current.ToUniversalTime();
    }
}
