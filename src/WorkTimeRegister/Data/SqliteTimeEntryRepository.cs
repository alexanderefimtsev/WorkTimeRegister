using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkTimeRegister.Models;

namespace WorkTimeRegister.Data;

public sealed partial class SqliteTimeEntryRepository(string databasePath) : ITimeEntryRepository
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString();

    public async Task InitializeAsync(TimeProvider? timeProvider = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await BackupBeforeWorkDayMigrationAsync(connection);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS TimeEntries (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Kind INTEGER NOT NULL,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                CurrentPeriodAdjustmentSeconds INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_TimeEntries_Active
                ON TimeEntries((1)) WHERE EndedAt IS NULL;
            CREATE TABLE IF NOT EXISTS TimePauses (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimeEntryId INTEGER NOT NULL,
                StartedAt TEXT NOT NULL,
                EndedAt TEXT NULL,
                FOREIGN KEY (TimeEntryId) REFERENCES TimeEntries(Id)
            );
            CREATE INDEX IF NOT EXISTS IX_TimePauses_Period
                ON TimePauses(StartedAt, EndedAt);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_TimePauses_Active
                ON TimePauses((1)) WHERE EndedAt IS NULL;
            CREATE TABLE IF NOT EXISTS TimeTransfers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RegisteredAt TEXT NOT NULL,
                WorkAdjustmentSeconds INTEGER NOT NULL,
                BreakAdjustmentSeconds INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_TimeTransfers_RegisteredAt
                ON TimeTransfers(RegisteredAt);
            CREATE TABLE IF NOT EXISTS ActionLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RegisteredAt TEXT NOT NULL,
                Action TEXT NOT NULL,
                CurrentMode INTEGER NULL,
                TotalWorkSeconds INTEGER NOT NULL,
                TotalBreakSeconds INTEGER NOT NULL,
                CurrentPeriodSeconds INTEGER NOT NULL,
                IsPaused INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_ActionLog_RegisteredAt
                ON ActionLog(RegisteredAt);
            """;
        await command.ExecuteNonQueryAsync();
        await EnsureCurrentPeriodAdjustmentColumnAsync(connection);
        await EnsureActionLogIsPausedColumnAsync(connection);
        await MigrateWorkDaysAsync(connection, timeProvider ?? TimeProvider.System);
    }

    public async Task<IReadOnlyList<TimeEntry>> GetEntriesAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<TimeEntry>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, StartedAt, EndedAt, CurrentPeriodAdjustmentSeconds
            FROM TimeEntries
            WHERE StartedAt < $to AND (EndedAt IS NULL OR EndedAt > $from)
            ORDER BY StartedAt;
            """;
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new TimeEntry(
                reader.GetInt64(0),
                (TimeEntryKind)reader.GetInt32(1),
                Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : Parse(reader.GetString(3)),
                TimeSpan.FromSeconds(reader.GetInt64(4))));
        }

        return result;
    }

    public async Task<IReadOnlyList<TimeTransfer>> GetTransfersAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<TimeTransfer>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, RegisteredAt, WorkAdjustmentSeconds, BreakAdjustmentSeconds
            FROM TimeTransfers
            WHERE RegisteredAt >= $from AND RegisteredAt < $to
            ORDER BY RegisteredAt;
            """;
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new TimeTransfer(
                reader.GetInt64(0),
                Parse(reader.GetString(1)),
                TimeSpan.FromSeconds(reader.GetInt64(2)),
                TimeSpan.FromSeconds(reader.GetInt64(3))));
        }

        return result;
    }

    public async Task<IReadOnlyList<TimePause>> GetPausesAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<TimePause>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TimeEntryId, StartedAt, EndedAt
            FROM TimePauses
            WHERE StartedAt < $to AND (EndedAt IS NULL OR EndedAt > $from)
            ORDER BY StartedAt;
            """;
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new TimePause(
                reader.GetInt64(0),
                reader.GetInt64(1),
                Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : Parse(reader.GetString(3))));
        }

        return result;
    }

    public async Task<IReadOnlyList<ActionLogRecord>> GetActionLogAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<ActionLogRecord>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, RegisteredAt, Action, CurrentMode,
                   TotalWorkSeconds, TotalBreakSeconds, CurrentPeriodSeconds, IsPaused,
                   WorkDayDate, IsWorkDayOpen
            FROM ActionLog
            WHERE RegisteredAt >= $from AND RegisteredAt < $to
            ORDER BY RegisteredAt DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ActionLogRecord(
                reader.GetInt64(0),
                Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : (TimeEntryKind)reader.GetInt32(3),
                TimeSpan.FromSeconds(reader.GetInt64(4)),
                TimeSpan.FromSeconds(reader.GetInt64(5)),
                TimeSpan.FromSeconds(reader.GetInt64(6)),
                reader.GetInt64(7) != 0)
            {
                WorkDayDate = reader.IsDBNull(8) ? null : DateOnly.ParseExact(reader.GetString(8), "yyyy-MM-dd"),
                IsWorkDayOpen = reader.IsDBNull(9) ? null : reader.GetInt32(9) != 0
            });
        }

        return result;
    }

    public async Task<TimeEntry?> GetActiveEntryAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, StartedAt, CurrentPeriodAdjustmentSeconds
            FROM TimeEntries WHERE EndedAt IS NULL LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new TimeEntry(
                reader.GetInt64(0),
                (TimeEntryKind)reader.GetInt32(1),
                Parse(reader.GetString(2)),
                null,
                TimeSpan.FromSeconds(reader.GetInt64(3)))
            : null;
    }

    public async Task StartAsync(TimeEntryKind kind, DateTimeOffset startedAt)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var zone = TimeZoneInfo.Local.GetUtcOffset(startedAt) == startedAt.Offset
            ? TimeZoneInfo.Local
            : TimeZoneInfo.CreateCustomTimeZone($"UTC{startedAt:zzz}", startedAt.Offset, "Local", "Local");
        var date = DateOnly.FromDateTime(startedAt.Date);
        var day = await EnsureDayAsync(connection, transaction, date, zone);
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE WorkDays SET StartedAt=COALESCE(StartedAt,$at), EndedAt=NULL, CloseReason=NULL WHERE Id=$id;",
            ("$at", Format(startedAt)), ("$id", day.Id));
        await StartPeriodAsync(connection, transaction, day.Id, kind, startedAt, false);
        await transaction.CommitAsync();
    }

    public async Task StopActiveAsync(DateTimeOffset endedAt)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var openDay = await ReadOpenDayAsync(connection, (SqliteTransaction)transaction);
        var summary = await ReadSummaryAsync(connection, (SqliteTransaction)transaction, openDay, endedAt);

        var pauseCommand = connection.CreateCommand();
        pauseCommand.Transaction = (SqliteTransaction)transaction;
        pauseCommand.CommandText = "UPDATE TimePauses SET EndedAt = $endedAt WHERE EndedAt IS NULL;";
        pauseCommand.Parameters.AddWithValue("$endedAt", Format(endedAt));
        await pauseCommand.ExecuteNonQueryAsync();

        var entryCommand = connection.CreateCommand();
        entryCommand.Transaction = (SqliteTransaction)transaction;
        entryCommand.CommandText = "UPDATE TimeEntries SET EndedAt = $endedAt WHERE EndedAt IS NULL;";
        entryCommand.Parameters.AddWithValue("$endedAt", Format(endedAt));
        await entryCommand.ExecuteNonQueryAsync();

        if (openDay is not null)
            await ExecuteSqlAsync(connection, (SqliteTransaction)transaction,
                "UPDATE WorkDays SET EndedAt=$at,LastMode=$mode,WasPaused=$paused,CloseReason=$reason WHERE Id=$id;",
                ("$at", Format(endedAt)), ("$mode", (int)(summary.ActiveKind ?? openDay.LastMode)),
                ("$paused", summary.IsPaused ? 1 : 0), ("$reason", (int)WorkDayCloseReason.Manual), ("$id", openDay.Id));

        await transaction.CommitAsync();
    }

    public async Task StartPauseAsync(long activeEntryId, DateTimeOffset startedAt)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TimePauses (TimeEntryId, StartedAt)
            SELECT $entryId, $startedAt
            WHERE EXISTS (
                SELECT 1 FROM TimeEntries WHERE Id = $entryId AND EndedAt IS NULL)
              AND NOT EXISTS (
                SELECT 1 FROM TimePauses WHERE EndedAt IS NULL);
            """;
        command.Parameters.AddWithValue("$entryId", activeEntryId);
        command.Parameters.AddWithValue("$startedAt", Format(startedAt));
        if (await command.ExecuteNonQueryAsync() == 0)
            throw new InvalidOperationException("Не удалось поставить активный период на паузу.");
    }

    public async Task EndPauseAsync(DateTimeOffset endedAt)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE TimePauses SET EndedAt = $endedAt WHERE EndedAt IS NULL;";
        command.Parameters.AddWithValue("$endedAt", Format(endedAt));
        if (await command.ExecuteNonQueryAsync() == 0)
            throw new InvalidOperationException("Активная пауза не найдена.");
    }

    public async Task AddTransferAsync(TimeTransferPlan plan, long? activeEntryId, DateTimeOffset registeredAt)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var transferDay = await EnsureDayAsync(connection, (SqliteTransaction)transaction,
            DateOnly.FromDateTime(registeredAt.Date),
            TimeZoneInfo.CreateCustomTimeZone($"UTC{registeredAt:zzz}", registeredAt.Offset, "Local", "Local"));

        var transferCommand = connection.CreateCommand();
        transferCommand.Transaction = (SqliteTransaction)transaction;
        transferCommand.CommandText = """
            INSERT INTO TimeTransfers (RegisteredAt, WorkAdjustmentSeconds, BreakAdjustmentSeconds, WorkDayId)
            VALUES ($registeredAt, $workAdjustment, $breakAdjustment, $day);
            """;
        transferCommand.Parameters.AddWithValue("$registeredAt", Format(registeredAt));
        transferCommand.Parameters.AddWithValue("$workAdjustment", ToSeconds(plan.WorkAdjustment));
        transferCommand.Parameters.AddWithValue("$breakAdjustment", ToSeconds(plan.BreakAdjustment));
        transferCommand.Parameters.AddWithValue("$day", transferDay.Id);
        await transferCommand.ExecuteNonQueryAsync();

        if (activeEntryId.HasValue && plan.CurrentPeriodAdjustment != TimeSpan.Zero)
        {
            var entryCommand = connection.CreateCommand();
            entryCommand.Transaction = (SqliteTransaction)transaction;
            entryCommand.CommandText = """
                UPDATE TimeEntries
                SET CurrentPeriodAdjustmentSeconds = CurrentPeriodAdjustmentSeconds + $adjustment
                WHERE Id = $id AND EndedAt IS NULL;
                """;
            entryCommand.Parameters.AddWithValue("$adjustment", ToSeconds(plan.CurrentPeriodAdjustment));
            entryCommand.Parameters.AddWithValue("$id", activeEntryId.Value);
            await entryCommand.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task AddActionLogAsync(ActionLogRecord record)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ActionLog (
                RegisteredAt, Action, CurrentMode,
                TotalWorkSeconds, TotalBreakSeconds, CurrentPeriodSeconds, IsPaused, WorkDayDate, IsWorkDayOpen)
            VALUES (
                $registeredAt, $action, $currentMode,
                $totalWork, $totalBreak, $currentPeriod, $isPaused, $workDayDate, $isWorkDayOpen);
            """;
        command.Parameters.AddWithValue("$registeredAt", Format(record.RegisteredAt));
        command.Parameters.AddWithValue("$action", record.Action);
        command.Parameters.AddWithValue("$currentMode", record.CurrentMode.HasValue ? (int)record.CurrentMode.Value : DBNull.Value);
        command.Parameters.AddWithValue("$totalWork", ToSeconds(record.TotalWork));
        command.Parameters.AddWithValue("$totalBreak", ToSeconds(record.TotalBreak));
        command.Parameters.AddWithValue("$currentPeriod", ToSeconds(record.CurrentPeriod));
        command.Parameters.AddWithValue("$isPaused", record.IsPaused ? 1 : 0);
        command.Parameters.AddWithValue("$workDayDate", (object?)record.WorkDayDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
        command.Parameters.AddWithValue("$isWorkDayOpen", record.IsWorkDayOpen.HasValue ? (record.IsWorkDayOpen.Value ? 1 : 0) : DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> ImportActionLogAsync(IReadOnlyCollection<ActionLogRecord> records)
    {
        if (records.Count == 0)
            return 0;

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var importedCount = 0;

        foreach (var record in records)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO ActionLog (
                    RegisteredAt, Action, CurrentMode,
                    TotalWorkSeconds, TotalBreakSeconds, CurrentPeriodSeconds, IsPaused, WorkDayDate, IsWorkDayOpen)
                SELECT
                    $registeredAt, $action, $currentMode,
                    $totalWork, $totalBreak, $currentPeriod, $isPaused, $workDayDate, $isWorkDayOpen
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM ActionLog
                    WHERE RegisteredAt = $registeredAt
                      AND Action = $action
                      AND (CurrentMode = $currentMode OR (CurrentMode IS NULL AND $currentMode IS NULL))
                      AND TotalWorkSeconds = $totalWork
                      AND TotalBreakSeconds = $totalBreak
                      AND CurrentPeriodSeconds = $currentPeriod
                      AND IsPaused = $isPaused
                );
                """;
            command.Parameters.AddWithValue("$registeredAt", Format(record.RegisteredAt));
            command.Parameters.AddWithValue("$action", record.Action);
            command.Parameters.AddWithValue("$currentMode", record.CurrentMode.HasValue ? (int)record.CurrentMode.Value : DBNull.Value);
            command.Parameters.AddWithValue("$totalWork", ToSeconds(record.TotalWork));
            command.Parameters.AddWithValue("$totalBreak", ToSeconds(record.TotalBreak));
            command.Parameters.AddWithValue("$currentPeriod", ToSeconds(record.CurrentPeriod));
            command.Parameters.AddWithValue("$isPaused", record.IsPaused ? 1 : 0);
            command.Parameters.AddWithValue("$workDayDate", (object?)record.WorkDayDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
            command.Parameters.AddWithValue("$isWorkDayOpen", record.IsWorkDayOpen.HasValue ? (record.IsWorkDayOpen.Value ? 1 : 0) : DBNull.Value);
            importedCount += await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return importedCount;
    }

    private static async Task EnsureCurrentPeriodAdjustmentColumnAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(TimeEntries);";
        await using var reader = await command.ExecuteReaderAsync();
        var columnExists = false;
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), "CurrentPeriodAdjustmentSeconds", StringComparison.OrdinalIgnoreCase))
            {
                columnExists = true;
                break;
            }
        }

        await reader.DisposeAsync();
        if (columnExists)
            return;

        var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = """
            ALTER TABLE TimeEntries
            ADD COLUMN CurrentPeriodAdjustmentSeconds INTEGER NOT NULL DEFAULT 0;
            """;
        await alterCommand.ExecuteNonQueryAsync();
    }

    private static async Task EnsureActionLogIsPausedColumnAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(ActionLog);";
        await using var reader = await command.ExecuteReaderAsync();
        var columnExists = false;
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), "IsPaused", StringComparison.OrdinalIgnoreCase))
            {
                columnExists = true;
                break;
            }
        }

        await reader.DisposeAsync();
        if (columnExists)
            return;

        var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = """
            ALTER TABLE ActionLog
            ADD COLUMN IsPaused INTEGER NOT NULL DEFAULT 0;
            """;
        await alterCommand.ExecuteNonQueryAsync();
    }

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    private static long ToSeconds(TimeSpan value) => checked((long)value.TotalSeconds);
}
