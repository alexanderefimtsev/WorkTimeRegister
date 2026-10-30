using Microsoft.Data.Sqlite;
using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class ReliabilityMigrationTests
{
    [Fact]
    public async Task ExistingDatabase_IsBackedUpAndMigratedOnlyOnceWithoutLosingTime()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ReliabilityMigration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.db");
        var repository = new SqliteTimeEntryRepository(path);
        try
        {
            await repository.InitializeAsync();
            var start = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
            await repository.StartAsync(TimeEntryKind.Work, start);
            await repository.StopActiveAsync(start.AddHours(2));
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE ApplicationState; ALTER TABLE WorkDaySettings DROP COLUMN PauseDuringSleep; ALTER TABLE WorkDaySettings DROP COLUMN ResumeAfterSleep; PRAGMA user_version=0;";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await repository.InitializeAsync();
            var backup = Assert.Single(Directory.GetFiles(directory, "*.before-reliability-*.bak"));
            await repository.InitializeAsync();
            Assert.Single(Directory.GetFiles(directory, "*.before-reliability-*.bak"));
            var entries = await repository.GetEntriesAsync(start, start.AddDays(1));
            Assert.Equal(TimeSpan.FromHours(2), new TimeSummaryCalculator().Calculate(entries, start, start.AddDays(1), start.AddDays(1)).TotalWork);
            await using var backupConnection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly");
            await backupConnection.OpenAsync(TestContext.Current.CancellationToken);
            using var probe = backupConnection.CreateCommand();
            probe.CommandText = "SELECT COUNT(*) FROM TimeEntries;";
            Assert.Equal(1L, await probe.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FutureSchema_IsRejectedBeforeAnyDataModification()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"FutureSchema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=999; CREATE TABLE PreserveMe(Value TEXT); INSERT INTO PreserveMe VALUES('original');";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => new SqliteTimeEntryRepository(path).InitializeAsync());
            await using var probeConnection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            await probeConnection.OpenAsync(TestContext.Current.CancellationToken);
            using var probe = probeConnection.CreateCommand();
            probe.CommandText = "SELECT Value FROM PreserveMe;";
            Assert.Equal("original", await probe.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeWithOpenLegacyPeriod_RequestsRecoveryInsteadOfSilentlyContinuing(bool withAdjustment)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"LegacyRecovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.db");
        var repository = new SqliteTimeEntryRepository(path);
        try
        {
            await repository.InitializeAsync();
            var start = DateTimeOffset.UtcNow.AddHours(-1);
            await repository.StartAsync(TimeEntryKind.Work, start);
            var checkpoint = withAdjustment ? start.AddMinutes(30) : start;
            if (withAdjustment)
                await repository.ProcessWorkDayAsync(new WorkDayRequest(WorkDayOperation.AdjustTime, checkpoint,
                    TimeZoneInfo.Utc, Adjustment: TimeAdjustmentOperation.Remove, Amount: TimeSpan.FromMinutes(15)));
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TABLE ApplicationState; ALTER TABLE WorkDaySettings DROP COLUMN PauseDuringSleep; ALTER TABLE WorkDaySettings DROP COLUMN ResumeAfterSleep; PRAGMA user_version=0;";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await repository.InitializeAsync();
            var snapshot = await new WorkDayService(repository, TimeProvider.System).ExecuteAsync(WorkDayOperation.Initialize);
            Assert.NotNull(snapshot.Recovery);
            Assert.Equal(RecoveryOrigin.LegacyUpgrade, snapshot.Recovery.Origin);
            var reopened = await new WorkDayService(repository, TimeProvider.System, Guid.NewGuid()).ExecuteAsync(WorkDayOperation.Initialize);
            Assert.Equal(RecoveryOrigin.LegacyUpgrade, reopened.Recovery!.Origin);
            Assert.Equal(checkpoint.ToUniversalTime(), snapshot.Recovery.ConfirmedAt);
            Assert.Equal(withAdjustment ? TimeSpan.FromMinutes(15) : TimeSpan.Zero, snapshot.Summary.TotalWork);
            Assert.Single(Directory.GetFiles(directory, "*.before-reliability-*.bak"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task VersionTwoUpgrade_PreservesPendingRecoveryAndMakesOneBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"VersionTwo-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "test.db");
        var repository = new SqliteTimeEntryRepository(path);
        try
        {
            await repository.InitializeAsync();
            var now = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
            await repository.ProcessWorkDayAsync(new WorkDayRequest(WorkDayOperation.StartDay, now, TimeZoneInfo.Utc));
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE ApplicationState SET RecoveryFrom=LastSeenAt, RecoveryTo='2026-10-28T10:00:00.0000000Z';
                    ALTER TABLE ApplicationState DROP COLUMN RecoveryKind;
                    ALTER TABLE ApplicationState DROP COLUMN SleepCycleId;
                    ALTER TABLE ApplicationState DROP COLUMN LastPowerEventAt;
                    ALTER TABLE ApplicationState DROP COLUMN LastUserActionAt;
                    PRAGMA user_version=2;
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await repository.InitializeAsync();
            await repository.InitializeAsync();
            Assert.Single(Directory.GetFiles(directory, "*.before-reliability-*.bak"));
            var pending = await repository.ProcessWorkDayAsync(new WorkDayRequest(WorkDayOperation.Initialize,
                now.AddHours(1), TimeZoneInfo.Utc, SessionId: Guid.NewGuid()));
            Assert.Equal(RecoveryOrigin.Unspecified, pending.Recovery!.Origin);
            Assert.Equal(now, pending.Recovery.ConfirmedAt);
            Assert.Equal(TimeSpan.FromHours(1), pending.Recovery.Duration);
            Assert.Equal(TimeSpan.Zero, pending.Summary.TotalWork);
        }
        finally { Directory.Delete(directory, true); }
    }
}
