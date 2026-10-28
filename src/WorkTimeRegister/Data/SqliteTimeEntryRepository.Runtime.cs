using Microsoft.Data.Sqlite;
using WorkTimeRegister.Models;

namespace WorkTimeRegister.Data;

public sealed partial class SqliteTimeEntryRepository
{
    private const int SchemaVersion = 2;

    private static async Task ValidateSchemaVersionAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) > SchemaVersion)
            throw new InvalidDataException("База создана более новой версией программы. Установите последнюю версию приложения.");
    }

    private async Task MigrateReliabilityAsync(SqliteConnection connection)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(await probe.ExecuteScalarAsync()) == SchemaVersion)
            return;
        probe.CommandText = "SELECT COUNT(*) FROM TimeEntries;";
        if (Convert.ToInt64(await probe.ExecuteScalarAsync()) > 0)
        {
            var path = $"{databasePath}.before-reliability-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.bak";
            await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            await backup.OpenAsync();
            connection.BackupDatabase(backup);
        }

        await using var transaction = connection.BeginTransaction();
        await ExecuteSqlAsync(connection, transaction, """
            ALTER TABLE WorkDaySettings ADD COLUMN PauseDuringSleep INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE WorkDaySettings ADD COLUMN ResumeAfterSleep INTEGER NOT NULL DEFAULT 0;
            CREATE TABLE ApplicationState (
                Id INTEGER PRIMARY KEY CHECK(Id=1),
                SessionId TEXT NULL,
                LastSeenAt TEXT NULL,
                IsRunning INTEGER NOT NULL DEFAULT 0,
                RecoveryFrom TEXT NULL,
                RecoveryTo TEXT NULL,
                SleepStartedAt TEXT NULL,
                SleepAutoPaused INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO ApplicationState(Id, LastSeenAt, IsRunning)
            SELECT 1,
                (SELECT MAX(StartedAt) FROM (
                    SELECT StartedAt FROM TimeEntries WHERE EndedAt IS NULL
                    UNION ALL
                    SELECT StartedAt FROM TimePauses WHERE EndedAt IS NULL
                    UNION ALL
                    SELECT p.EndedAt FROM TimePauses p JOIN TimeEntries e ON e.Id=p.TimeEntryId
                        WHERE e.EndedAt IS NULL AND p.EndedAt IS NOT NULL
                    UNION ALL
                    SELECT t.RegisteredAt FROM TimeTransfers t JOIN WorkDays d ON d.Id=t.WorkDayId
                        WHERE d.StartedAt IS NOT NULL AND d.EndedAt IS NULL
                )),
                CASE WHEN EXISTS(SELECT 1 FROM WorkDays WHERE StartedAt IS NOT NULL AND EndedAt IS NULL) THEN 1 ELSE 0 END;
            PRAGMA user_version=2;
            """);
        await transaction.CommitAsync();
    }

    private sealed record RuntimeState(string? SessionId, DateTimeOffset? LastSeenAt, bool IsRunning,
        DateTimeOffset? RecoveryFrom, DateTimeOffset? RecoveryTo, DateTimeOffset? SleepStartedAt, bool SleepAutoPaused);

    private static async Task<RuntimeState> ReadRuntimeAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = CreateSql(connection, transaction,
            "SELECT SessionId,LastSeenAt,IsRunning,RecoveryFrom,RecoveryTo,SleepStartedAt,SleepAutoPaused FROM ApplicationState WHERE Id=1;");
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new RuntimeState(reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : Parse(reader.GetString(1)), reader.GetInt32(2) != 0,
            reader.IsDBNull(3) ? null : Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : Parse(reader.GetString(5)), reader.GetInt32(6) != 0);
    }

    private static async Task<RecoveryInfo?> DetectRecoveryAsync(SqliteConnection connection, SqliteTransaction transaction,
        WorkDayRequest request, WorkDay? openDay, RuntimeState state, WorkDaySettings settings)
    {
        if (openDay is null)
            return null;
        var interrupted = request.Operation == WorkDayOperation.Initialize && state.IsRunning &&
                          state.SessionId != request.SessionId.ToString();
        var from = state.RecoveryFrom ?? (interrupted ? state.LastSeenAt : null);
        if (!from.HasValue)
            return null;
        var to = request.Operation == WorkDayOperation.Initialize ? request.Now : state.RecoveryTo ?? request.Now;
        if (to < from.Value)
            throw new InvalidOperationException("Время компьютера раньше последней сохранённой активности. Проверьте дату и время.");
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE ApplicationState SET RecoveryFrom=$from, RecoveryTo=$to, SessionId=$session WHERE Id=1;",
            ("$from", Format(from.Value)), ("$to", Format(to)), ("$session", request.SessionId.ToString()));
        var summary = await ReadSummaryAsync(connection, transaction, openDay, from.Value);
        var countableEnd = !settings.AutoContinueAtMidnight && openDay.DayEnd < to ? openDay.DayEnd : to;
        var maximum = countableEnd > from.Value ? countableEnd - from.Value : TimeSpan.Zero;
        return new RecoveryInfo(from.Value, to, summary.ActiveKind ?? openDay.LastMode, summary.IsPaused, maximum);
    }

    private static async Task<DateTimeOffset?> ApplyRecoveryAsync(SqliteConnection connection, SqliteTransaction transaction,
        WorkDayRequest request, WorkDay day, RecoveryInfo recovery)
    {
        if (!Enum.IsDefined(request.Recovery))
            throw new InvalidOperationException("Неизвестный вариант восстановления.");
        var amount = request.Recovery switch
        {
            RecoveryChoice.Include => recovery.MaximumCountableDuration,
            RecoveryChoice.Custom => request.Amount,
            _ => TimeSpan.Zero
        };
        if (amount < TimeSpan.Zero || amount > recovery.MaximumCountableDuration)
            throw new InvalidOperationException("Указанное время превышает допустимый интервал с учётом границы рабочего дня.");
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE ApplicationState SET RecoveryFrom=NULL, RecoveryTo=NULL, SleepStartedAt=NULL, SleepAutoPaused=0 WHERE Id=1;");
        return recovery.WasPaused ? null : recovery.ConfirmedAt + amount;
    }

    private static async Task EnsureRecoveryPauseAsync(SqliteConnection connection, SqliteTransaction transaction,
        WorkDay? day, DateTimeOffset? at, DateTimeOffset through)
    {
        if (day is null || !at.HasValue || at.Value > through)
            return;
        await ExecuteSqlAsync(connection, transaction, """
            INSERT INTO TimePauses(TimeEntryId, StartedAt)
            SELECT Id,MAX(StartedAt,$at) FROM TimeEntries
            WHERE WorkDayId=$day AND EndedAt IS NULL
              AND NOT EXISTS(SELECT 1 FROM TimePauses WHERE EndedAt IS NULL);
            """, ("$at", Format(at.Value)), ("$day", day.Id));
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE WorkDays SET WasPaused=1 WHERE Id=$day;", ("$day", day.Id));
    }

    private static async Task SaveCheckpointAsync(SqliteConnection connection, SqliteTransaction transaction,
        WorkDayRequest request, RuntimeState state)
    {
        if (request.Operation == WorkDayOperation.Refresh && state.LastSeenAt.HasValue &&
            request.Now >= state.LastSeenAt.Value && request.Now - state.LastSeenAt.Value < TimeSpan.FromSeconds(15))
            return;
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE ApplicationState SET SessionId=$session, LastSeenAt=$at, IsRunning=$running WHERE Id=1;",
            ("$session", request.SessionId.ToString()), ("$at", Format(request.Now)),
            ("$running", request.Operation == WorkDayOperation.Exit ? 0 : 1));
    }
}
