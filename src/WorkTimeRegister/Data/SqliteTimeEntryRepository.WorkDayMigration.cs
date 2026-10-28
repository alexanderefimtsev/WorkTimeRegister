using Microsoft.Data.Sqlite;
using WorkTimeRegister.Models;

namespace WorkTimeRegister.Data;

public sealed partial class SqliteTimeEntryRepository
{
    private async Task BackupBeforeWorkDayMigrationAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%')
                AND NOT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='WorkDays');
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync()) != 1)
            return;
        var backupPath = $"{databasePath}.before-workdays-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.bak";
        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
        await backup.OpenAsync();
        connection.BackupDatabase(backup);
    }

    private static async Task MigrateWorkDaysAsync(SqliteConnection connection, TimeProvider timeProvider)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkDays';";
        if (Convert.ToInt32(await probe.ExecuteScalarAsync()) != 0)
            return;

        await using var transaction = connection.BeginTransaction();
        await ExecuteSqlAsync(connection, transaction, """
            CREATE TABLE WorkDays (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorkDate TEXT NOT NULL UNIQUE,
                DayStart TEXT NOT NULL,
                DayEnd TEXT NOT NULL,
                TimeZoneId TEXT NOT NULL,
                StartedAt TEXT NULL,
                EndedAt TEXT NULL,
                LastMode INTEGER NOT NULL DEFAULT 0,
                WasPaused INTEGER NOT NULL DEFAULT 0,
                CloseReason INTEGER NULL
            );
            CREATE UNIQUE INDEX IX_WorkDays_Open ON WorkDays((1))
                WHERE StartedAt IS NOT NULL AND EndedAt IS NULL;
            CREATE TABLE WorkDaySettings (
                Id INTEGER PRIMARY KEY CHECK(Id=1),
                AutoStartOnLaunch INTEGER NOT NULL DEFAULT 0,
                RestoreOnLaunch INTEGER NOT NULL DEFAULT 0,
                AutoContinueAtMidnight INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO WorkDaySettings(Id) VALUES(1);
            ALTER TABLE TimeEntries ADD COLUMN WorkDayId INTEGER NULL REFERENCES WorkDays(Id);
            ALTER TABLE TimeTransfers ADD COLUMN WorkDayId INTEGER NULL REFERENCES WorkDays(Id);
            ALTER TABLE ActionLog ADD COLUMN WorkDayId INTEGER NULL REFERENCES WorkDays(Id);
            ALTER TABLE ActionLog ADD COLUMN WorkDayDate TEXT NULL;
            ALTER TABLE ActionLog ADD COLUMN IsWorkDayOpen INTEGER NULL;
            CREATE INDEX IX_TimeEntries_WorkDay ON TimeEntries(WorkDayId);
            CREATE INDEX IX_TimeTransfers_WorkDay ON TimeTransfers(WorkDayId);
            """);

        var entries = new List<TimeEntry>();
        var pauses = new List<TimePause>();
        using (var command = CreateSql(connection, transaction,
                   "SELECT Id,Kind,StartedAt,EndedAt,CurrentPeriodAdjustmentSeconds FROM TimeEntries ORDER BY StartedAt,Id;"))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                entries.Add(new TimeEntry(reader.GetInt64(0), (TimeEntryKind)reader.GetInt32(1), Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : Parse(reader.GetString(3)), TimeSpan.FromSeconds(reader.GetInt64(4))));
        using (var command = CreateSql(connection, transaction, "SELECT Id,TimeEntryId,StartedAt,EndedAt FROM TimePauses;"))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                pauses.Add(new TimePause(reader.GetInt64(0), reader.GetInt64(1), Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : Parse(reader.GetString(3))));

        var now = timeProvider.GetUtcNow();
        var zone = timeProvider.LocalTimeZone;
        // Release the unique open-pause slot before splitting cross-midnight pauses.
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE TimePauses SET EndedAt=$now WHERE EndedAt IS NULL;", ("$now", Format(now)));
        foreach (var entry in entries)
        {
            var pieces = new List<(long EntryId, DateTimeOffset Start, DateTimeOffset End, bool IsOpen)>();
            var start = entry.StartedAt;
            var effectiveEnd = entry.EndedAt ?? (now > start ? now : start);
            var first = true;
            while (true)
            {
                var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
                var day = await EnsureDayAsync(connection, transaction, date, zone);
                var end = effectiveEnd < day.DayEnd ? effectiveEnd : day.DayEnd;
                var last = end == effectiveEnd && (entry.EndedAt.HasValue || effectiveEnd < day.DayEnd);
                var open = !entry.EndedAt.HasValue && last;
                var adjustment = last ? ToSeconds(entry.CurrentPeriodAdjustment) : 0;
                long entryId;
                if (first)
                {
                    entryId = entry.Id;
                    await ExecuteSqlAsync(connection, transaction,
                        "UPDATE TimeEntries SET EndedAt=$end, WorkDayId=$day, CurrentPeriodAdjustmentSeconds=$adjustment WHERE Id=$id;",
                        ("$end", open ? null : Format(end)), ("$day", day.Id), ("$adjustment", adjustment), ("$id", entryId));
                }
                else
                {
                    await ExecuteSqlAsync(connection, transaction,
                        "INSERT INTO TimeEntries(Kind,StartedAt,EndedAt,WorkDayId,CurrentPeriodAdjustmentSeconds) VALUES($kind,$start,$end,$day,$adjustment);",
                        ("$kind", (int)entry.Kind), ("$start", Format(start)), ("$end", open ? null : Format(end)),
                        ("$day", day.Id), ("$adjustment", adjustment));
                    entryId = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT last_insert_rowid();"));
                }
                pieces.Add((entryId, start, end, open));
                await ExecuteSqlAsync(connection, transaction, """
                    UPDATE WorkDays SET StartedAt=CASE WHEN StartedAt IS NULL OR StartedAt>$start THEN $start ELSE StartedAt END,
                        EndedAt=CASE WHEN $open=1 THEN NULL WHEN EndedAt IS NULL OR EndedAt<$end THEN $end ELSE EndedAt END,
                        LastMode=$mode, WasPaused=$paused, CloseReason=$reason WHERE Id=$id;
                    """, ("$start", Format(start)), ("$end", Format(end)), ("$open", open ? 1 : 0),
                    ("$mode", (int)entry.Kind), ("$paused", open && pauses.Any(p => p.TimeEntryId == entry.Id && p.EndedAt is null) ? 1 : 0),
                    ("$reason", open ? null : (int)(end == day.DayEnd ? WorkDayCloseReason.Midnight : WorkDayCloseReason.Migration)),
                    ("$id", day.Id));
                if (last)
                    break;
                start = end;
                first = false;
            }

            foreach (var pause in pauses.Where(p => p.TimeEntryId == entry.Id))
            {
                var firstPausePiece = true;
                foreach (var piece in pieces)
                {
                    var pauseStart = pause.StartedAt > piece.Start ? pause.StartedAt : piece.Start;
                    var pauseEnd = pause.EndedAt ?? now;
                    if (pauseEnd > piece.End)
                        pauseEnd = piece.End;
                    var open = pause.EndedAt is null && piece.IsOpen;
                    if (pauseEnd < pauseStart || (pauseEnd == pauseStart && !open && pause.EndedAt != pause.StartedAt))
                        continue;
                    if (firstPausePiece)
                        await ExecuteSqlAsync(connection, transaction,
                            "UPDATE TimePauses SET TimeEntryId=$entry, StartedAt=$start, EndedAt=$end WHERE Id=$id;",
                            ("$entry", piece.EntryId), ("$start", Format(pauseStart)), ("$end", open ? null : Format(pauseEnd)), ("$id", pause.Id));
                    else
                        await ExecuteSqlAsync(connection, transaction,
                            "INSERT INTO TimePauses(TimeEntryId,StartedAt,EndedAt) VALUES($entry,$start,$end);",
                            ("$entry", piece.EntryId), ("$start", Format(pauseStart)), ("$end", open ? null : Format(pauseEnd)));
                    firstPausePiece = false;
                }
            }
        }

        var transfers = new List<(long Id, DateTimeOffset At)>();
        using (var command = CreateSql(connection, transaction, "SELECT Id,RegisteredAt FROM TimeTransfers;"))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                transfers.Add((reader.GetInt64(0), Parse(reader.GetString(1))));
        foreach (var transfer in transfers)
        {
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(transfer.At, zone).DateTime);
            var day = await EnsureDayAsync(connection, transaction, date, zone);
            await ExecuteSqlAsync(connection, transaction,
                "UPDATE TimeTransfers SET WorkDayId=$day WHERE Id=$id;", ("$day", day.Id), ("$id", transfer.Id));
        }

        var logs = new List<(long Id, DateTimeOffset At)>();
        using (var command = CreateSql(connection, transaction, "SELECT Id,RegisteredAt FROM ActionLog;"))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                logs.Add((reader.GetInt64(0), Parse(reader.GetString(1))));
        foreach (var log in logs)
        {
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(log.At, zone).DateTime);
            var day = await ReadDayAsync(connection, transaction, date);
            await ExecuteSqlAsync(connection, transaction,
                "UPDATE ActionLog SET WorkDayId=$day,WorkDayDate=$date WHERE Id=$id;",
                ("$day", day?.Id), ("$date", date.ToString("yyyy-MM-dd")), ("$id", log.Id));
        }
        await transaction.CommitAsync();
    }
}
