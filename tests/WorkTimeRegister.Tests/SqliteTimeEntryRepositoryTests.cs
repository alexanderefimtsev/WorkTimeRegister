using Microsoft.Data.Sqlite;
using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class SqliteTimeEntryRepositoryTests
{
    [Fact]
    public async Task Repository_PersistsTransferAdjustmentAndActionLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
        var now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(3));

        try
        {
            await repository.InitializeAsync();
            await repository.StartAsync(TimeEntryKind.Work, now.AddHours(-1));
            var activeEntry = await repository.GetActiveEntryAsync();
            var plan = new TimeTransferPlan(
                TimeEntryKind.Work,
                TimeEntryKind.Break,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(-15),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(-15));

            await repository.AddTransferAsync(plan, activeEntry!.Id, now);
            await repository.AddActionLogAsync(new ActionLogRecord(
                0,
                now,
                "Перенос",
                TimeEntryKind.Work,
                TimeSpan.FromMinutes(45),
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(45)));

            var updatedEntry = await repository.GetActiveEntryAsync();
            var dayStart = new DateTimeOffset(now.Date, now.Offset);
            var transfers = await repository.GetTransfersAsync(dayStart, dayStart.AddDays(1));
            var log = await repository.GetActionLogAsync(dayStart, dayStart.AddDays(1));

            Assert.Equal(TimeSpan.FromMinutes(-15), updatedEntry!.CurrentPeriodAdjustment);
            Assert.Single(transfers);
            Assert.Equal(TimeSpan.FromMinutes(-15), transfers[0].WorkAdjustment);
            Assert.Single(log);
            Assert.Equal("Перенос", log[0].Action);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ImportActionLogAsync_SkipsExactDuplicates()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
        var now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(3));
        var records = new[]
        {
            new ActionLogRecord(
                0,
                now,
                "Начало работы",
                TimeEntryKind.Work,
                TimeSpan.FromMinutes(10),
                TimeSpan.Zero,
                TimeSpan.FromMinutes(10)),
            new ActionLogRecord(
                0,
                now.AddMinutes(30),
                "Окончание работы",
                null,
                TimeSpan.FromMinutes(30),
                TimeSpan.Zero,
                TimeSpan.Zero)
        };

        try
        {
            await repository.InitializeAsync();

            var firstImportCount = await repository.ImportActionLogAsync(records);
            var secondImportCount = await repository.ImportActionLogAsync(records);
            var dayStart = new DateTimeOffset(now.Date, now.Offset);
            var log = await repository.GetActionLogAsync(dayStart, dayStart.AddDays(1));

            Assert.Equal(2, firstImportCount);
            Assert.Equal(0, secondImportCount);
            Assert.Equal(2, log.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Pause_PersistsAndIsClosedWhenWorkStops()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
        var now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(3));

        try
        {
            await repository.InitializeAsync();
            await repository.StartAsync(TimeEntryKind.Work, now.AddHours(-1));
            var activeEntry = await repository.GetActiveEntryAsync();

            await repository.StartPauseAsync(activeEntry!.Id, now.AddMinutes(-30));
            var activePauses = await repository.GetPausesAsync(now.AddHours(-1), now.AddMinutes(1));
            await repository.AddActionLogAsync(new ActionLogRecord(
                0,
                now.AddMinutes(-30),
                "Пауза: Работа",
                TimeEntryKind.Work,
                TimeSpan.FromMinutes(30),
                TimeSpan.Zero,
                TimeSpan.FromMinutes(30),
                true));
            var log = await repository.GetActionLogAsync(now.AddHours(-1), now.AddMinutes(1));

            Assert.Single(activePauses);
            Assert.Null(activePauses[0].EndedAt);
            Assert.Single(log);
            Assert.True(log[0].IsPaused);

            await repository.StopActiveAsync(now);
            var completedPauses = await repository.GetPausesAsync(now.AddHours(-1), now.AddMinutes(1));

            Assert.Single(completedPauses);
            Assert.Equal(now.UtcDateTime, completedPauses[0].EndedAt?.UtcDateTime);
            Assert.Null(await repository.GetActiveEntryAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task InitializeAsync_MigratesExistingActionLogWithoutDeletingRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "test.db");
        Directory.CreateDirectory(directory);

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ActionLog (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        RegisteredAt TEXT NOT NULL,
                        Action TEXT NOT NULL,
                        CurrentMode INTEGER NULL,
                        TotalWorkSeconds INTEGER NOT NULL,
                        TotalBreakSeconds INTEGER NOT NULL,
                        CurrentPeriodSeconds INTEGER NOT NULL
                    );
                    INSERT INTO ActionLog (
                        RegisteredAt, Action, CurrentMode,
                        TotalWorkSeconds, TotalBreakSeconds, CurrentPeriodSeconds)
                    VALUES ('2026-08-20T07:00:00.0000000Z', 'Начало работы', 0, 0, 0, 0);
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var repository = new SqliteTimeEntryRepository(databasePath);
            await repository.InitializeAsync();
            var records = await repository.GetActionLogAsync(
                new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero));

            Assert.Single(records);
            Assert.False(records[0].IsPaused);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
