using Microsoft.Data.Sqlite;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;

namespace WorkTimeRegister.Data;

public sealed partial class SqliteTimeEntryRepository
{
    public async Task<WorkDaySnapshot> ProcessWorkDayAsync(WorkDayRequest request)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        var settings = await ReadSettingsAsync(connection, transaction);
        var openDay = await ReadOpenDayAsync(connection, transaction);
        var zone = openDay is null ? request.TimeZone : ResolveTimeZone(openDay.TimeZoneId, request.TimeZone);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.Now, zone).DateTime);

        while (openDay is not null && request.Now >= openDay.DayEnd)
        {
            var boundary = openDay.DayEnd;
            var previous = await ReadSummaryAsync(connection, transaction, openDay, boundary);
            await CloseDayAsync(connection, transaction, openDay, boundary, WorkDayCloseReason.Midnight,
                "Автоматическое завершение рабочего дня", boundary.AddTicks(-1));
            if (!settings.AutoContinueAtMidnight)
                break;

            var nextDate = openDay.Date.AddDays(1);
            await OpenDayAsync(connection, transaction, nextDate, zone, boundary,
                previous.ActiveKind ?? openDay.LastMode, previous.IsPaused,
                "Автоматическое продолжение рабочего дня");
            openDay = await ReadOpenDayAsync(connection, transaction);
        }

        var day = await ReadDayAsync(connection, transaction, date);
        switch (request.Operation)
        {
            case WorkDayOperation.Initialize:
                if (day is { IsOpen: false, CloseReason: WorkDayCloseReason.ApplicationExit } && settings.RestoreOnLaunch)
                    await OpenDayAsync(connection, transaction, date, zone, request.Now,
                        day.LastMode, day.WasPaused, "Восстановление рабочего дня после выхода");
                else if ((day is null || day.StartedAt is null) && settings.AutoStartOnLaunch)
                    await OpenDayAsync(connection, transaction, date, zone, request.Now,
                        TimeEntryKind.Work, false, "Автоматическое начало рабочего дня при запуске");
                break;
            case WorkDayOperation.StartDay:
                if (day?.IsOpen != true)
                    await OpenDayAsync(connection, transaction, date, zone, request.Now,
                        TimeEntryKind.Work, false,
                        day?.StartedAt is null ? "Начало рабочего дня" : "Повторное начало рабочего дня");
                break;
            case WorkDayOperation.EndDay:
            case WorkDayOperation.Exit:
                if (day?.IsOpen == true)
                    await CloseDayAsync(connection, transaction, day, request.Now,
                        request.Operation == WorkDayOperation.Exit ? WorkDayCloseReason.ApplicationExit : WorkDayCloseReason.Manual,
                        request.Operation == WorkDayOperation.Exit ? "Завершение рабочего дня при выходе" : "Завершение рабочего дня",
                        request.Now);
                break;
            case WorkDayOperation.StartWork:
            case WorkDayOperation.StartBreak:
            case WorkDayOperation.ToggleMode:
                RequireOpenDay(day);
                var summary = await ReadSummaryAsync(connection, transaction, day!, request.Now);
                if (summary.IsPaused)
                    throw new InvalidOperationException("Сначала снимите текущий период с паузы.");
                var mode = request.Operation switch
                {
                    WorkDayOperation.StartWork => TimeEntryKind.Work,
                    WorkDayOperation.StartBreak => TimeEntryKind.Break,
                    _ => summary.ActiveKind == TimeEntryKind.Work ? TimeEntryKind.Break : TimeEntryKind.Work
                };
                if (summary.ActiveKind != mode)
                {
                    await StopPeriodsAsync(connection, transaction, day!, request.Now);
                    await StartPeriodAsync(connection, transaction, day!.Id, mode, request.Now, false);
                    await WriteDayLogAsync(connection, transaction, day, request.Now,
                        mode == TimeEntryKind.Break ? "Начало перерыва" : "Окончание перерыва", request.Now);
                }
                break;
            case WorkDayOperation.TogglePause:
                RequireOpenDay(day);
                var beforePause = await ReadSummaryAsync(connection, transaction, day!, request.Now);
                var entryId = await ActiveEntryIdAsync(connection, transaction, day!.Id);
                if (beforePause.IsPaused)
                    await ExecuteSqlAsync(connection, transaction,
                        "UPDATE TimePauses SET EndedAt=$at WHERE TimeEntryId=$entry AND EndedAt IS NULL;",
                        ("$at", Format(request.Now)), ("$entry", entryId));
                else
                    await ExecuteSqlAsync(connection, transaction,
                        "INSERT INTO TimePauses(TimeEntryId, StartedAt) VALUES($entry,$at);",
                        ("$entry", entryId), ("$at", Format(request.Now)));
                await ExecuteSqlAsync(connection, transaction,
                    "UPDATE WorkDays SET WasPaused=$paused WHERE Id=$id;",
                    ("$paused", beforePause.IsPaused ? 0 : 1), ("$id", day.Id));
                await WriteDayLogAsync(connection, transaction, day, request.Now,
                    $"{(beforePause.IsPaused ? "Продолжение" : "Пауза")}: {ModeName(beforePause.ActiveKind!.Value)}", request.Now);
                break;
            case WorkDayOperation.SaveSettings:
                var updated = request.Settings ?? throw new InvalidOperationException("Настройки не указаны.");
                await ExecuteSqlAsync(connection, transaction,
                    "UPDATE WorkDaySettings SET AutoStartOnLaunch=$start, RestoreOnLaunch=$restore, AutoContinueAtMidnight=$continue WHERE Id=1;",
                    ("$start", updated.AutoStartOnLaunch ? 1 : 0),
                    ("$restore", updated.RestoreOnLaunch ? 1 : 0),
                    ("$continue", updated.AutoContinueAtMidnight ? 1 : 0));
                if (settings != updated)
                    await WriteDayLogAsync(connection, transaction, day, request.Now,
                        $"Настройки рабочего дня: начало при запуске — {OnOff(updated.AutoStartOnLaunch)}; " +
                        $"восстановление — {OnOff(updated.RestoreOnLaunch)}; " +
                        $"продолжение после полуночи — {OnOff(updated.AutoContinueAtMidnight)}", request.Now, date);
                settings = updated;
                break;
            case WorkDayOperation.AdjustTime:
            case WorkDayOperation.AdjustCurrentPeriod:
                day ??= await EnsureDayAsync(connection, transaction, date, zone);
                var beforeAdjustment = await ReadSummaryAsync(connection, transaction, day, request.Now);
                var planner = new TimeTransferPlanner();
                var plan = request.Operation == WorkDayOperation.AdjustCurrentPeriod
                    ? planner.CreateCurrentPeriodPlan(beforeAdjustment, request.Adjustment, request.Amount)
                    : planner.CreateAdjustmentPlan(beforeAdjustment, request.Adjustment, request.Category, request.Amount);
                await ExecuteSqlAsync(connection, transaction,
                    "INSERT INTO TimeTransfers(RegisteredAt, WorkAdjustmentSeconds, BreakAdjustmentSeconds, WorkDayId) VALUES($at,$work,$break,$day);",
                    ("$at", Format(request.Now)), ("$work", ToSeconds(plan.WorkAdjustment)),
                    ("$break", ToSeconds(plan.BreakAdjustment)), ("$day", day.Id));
                if (plan.CurrentPeriodAdjustment != TimeSpan.Zero)
                    await ExecuteSqlAsync(connection, transaction,
                        "UPDATE TimeEntries SET CurrentPeriodAdjustmentSeconds=CurrentPeriodAdjustmentSeconds+$amount WHERE WorkDayId=$day AND EndedAt IS NULL;",
                        ("$amount", ToSeconds(plan.CurrentPeriodAdjustment)), ("$day", day.Id));
                await WriteDayLogAsync(connection, transaction, day, request.Now,
                    AdjustmentAction(request, plan), request.Now);
                break;
        }

        day = await ReadDayAsync(connection, transaction, date);
        var result = new WorkDaySnapshot(day, date,
            await ReadSummaryAsync(connection, transaction, day, request.Now), settings);
        await transaction.CommitAsync();
        return result;
    }

    private static async Task OpenDayAsync(SqliteConnection connection, SqliteTransaction transaction,
        DateOnly date, TimeZoneInfo zone, DateTimeOffset at, TimeEntryKind mode, bool paused, string action)
    {
        var day = await EnsureDayAsync(connection, transaction, date, zone);
        if (day.IsOpen)
            return;
        if (day.EndedAt > at)
            throw new InvalidOperationException("Время компьютера раньше последнего завершения рабочего дня.");
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE WorkDays SET StartedAt=COALESCE(StartedAt,$at), EndedAt=NULL, LastMode=$mode, WasPaused=$paused, CloseReason=NULL WHERE Id=$id;",
            ("$at", Format(at)), ("$mode", (int)mode), ("$paused", paused ? 1 : 0), ("$id", day.Id));
        await StartPeriodAsync(connection, transaction, day.Id, mode, at, paused);
        day = (await ReadDayAsync(connection, transaction, date))!;
        await WriteDayLogAsync(connection, transaction, day, at, action, at);
    }

    private static async Task CloseDayAsync(SqliteConnection connection, SqliteTransaction transaction,
        WorkDay day, DateTimeOffset at, WorkDayCloseReason reason, string action, DateTimeOffset registeredAt)
    {
        var summary = await ReadSummaryAsync(connection, transaction, day, at);
        await StopPeriodsAsync(connection, transaction, day, at);
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE WorkDays SET EndedAt=$at, LastMode=$mode, WasPaused=$paused, CloseReason=$reason WHERE Id=$id;",
            ("$at", Format(at)), ("$mode", (int)(summary.ActiveKind ?? day.LastMode)),
            ("$paused", summary.IsPaused ? 1 : 0), ("$reason", (int)reason), ("$id", day.Id));
        day = day with { EndedAt = at, CloseReason = reason };
        await WriteDayLogAsync(connection, transaction, day, at, action, registeredAt);
    }

    private static async Task StartPeriodAsync(SqliteConnection connection, SqliteTransaction transaction,
        long dayId, TimeEntryKind mode, DateTimeOffset at, bool paused)
    {
        await ExecuteSqlAsync(connection, transaction,
            "INSERT INTO TimeEntries(Kind, StartedAt, WorkDayId) VALUES($kind,$at,$day);",
            ("$kind", (int)mode), ("$at", Format(at)), ("$day", dayId));
        if (paused)
            await ExecuteSqlAsync(connection, transaction,
                "INSERT INTO TimePauses(TimeEntryId, StartedAt) VALUES(last_insert_rowid(),$at);", ("$at", Format(at)));
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE WorkDays SET LastMode=$mode, WasPaused=$paused WHERE Id=$day;",
            ("$mode", (int)mode), ("$paused", paused ? 1 : 0), ("$day", dayId));
    }

    private static async Task StopPeriodsAsync(SqliteConnection connection, SqliteTransaction transaction, WorkDay day, DateTimeOffset at)
    {
        var future = await ScalarAsync(connection, transaction,
            "SELECT COUNT(*) FROM TimeEntries WHERE WorkDayId=$day AND EndedAt IS NULL AND StartedAt>$at;",
            ("$day", day.Id), ("$at", Format(at)));
        if (Convert.ToInt64(future) != 0)
            throw new InvalidOperationException("Время компьютера раньше начала текущего периода.");
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE TimePauses SET EndedAt=MAX(StartedAt,$at) WHERE EndedAt IS NULL AND TimeEntryId IN (SELECT Id FROM TimeEntries WHERE WorkDayId=$day);",
            ("$at", Format(at)), ("$day", day.Id));
        await ExecuteSqlAsync(connection, transaction,
            "UPDATE TimeEntries SET EndedAt=$at WHERE WorkDayId=$day AND EndedAt IS NULL;",
            ("$at", Format(at)), ("$day", day.Id));
    }

    private static async Task<WorkDay> EnsureDayAsync(SqliteConnection connection, SqliteTransaction transaction, DateOnly date, TimeZoneInfo zone)
    {
        await ExecuteSqlAsync(connection, transaction,
            "INSERT OR IGNORE INTO WorkDays(WorkDate, DayStart, DayEnd, TimeZoneId) VALUES($date,$start,$end,$zone);",
            ("$date", date.ToString("yyyy-MM-dd")), ("$start", Format(WorkDayService.Boundary(date, zone))),
            ("$end", Format(WorkDayService.Boundary(date.AddDays(1), zone))), ("$zone", zone.Id));
        return (await ReadDayAsync(connection, transaction, date))!;
    }

    private static async Task<TimeSummary> ReadSummaryAsync(SqliteConnection connection, SqliteTransaction transaction,
        WorkDay? day, DateTimeOffset now)
    {
        if (day is null)
            return new TimeSummary(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null);
        var entries = new List<TimeEntry>();
        var transfers = new List<TimeTransfer>();
        var pauses = new List<TimePause>();
        using (var command = CreateSql(connection, transaction,
                   "SELECT Id, Kind, StartedAt, EndedAt, CurrentPeriodAdjustmentSeconds FROM TimeEntries WHERE WorkDayId=$id ORDER BY StartedAt;", ("$id", day.Id)))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                entries.Add(new TimeEntry(reader.GetInt64(0), (TimeEntryKind)reader.GetInt32(1), Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : Parse(reader.GetString(3)), TimeSpan.FromSeconds(reader.GetInt64(4))));
        using (var command = CreateSql(connection, transaction,
                   "SELECT Id, RegisteredAt, WorkAdjustmentSeconds, BreakAdjustmentSeconds FROM TimeTransfers WHERE WorkDayId=$id;", ("$id", day.Id)))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                transfers.Add(new TimeTransfer(reader.GetInt64(0), Parse(reader.GetString(1)),
                    TimeSpan.FromSeconds(reader.GetInt64(2)), TimeSpan.FromSeconds(reader.GetInt64(3))));
        using (var command = CreateSql(connection, transaction,
                   "SELECT p.Id, p.TimeEntryId, p.StartedAt, p.EndedAt FROM TimePauses p JOIN TimeEntries e ON e.Id=p.TimeEntryId WHERE e.WorkDayId=$id;", ("$id", day.Id)))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                pauses.Add(new TimePause(reader.GetInt64(0), reader.GetInt64(1), Parse(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : Parse(reader.GetString(3))));
        return new TimeSummaryCalculator().Calculate(entries, day.DayStart, day.DayEnd, now, transfers, pauses);
    }

    private static async Task WriteDayLogAsync(SqliteConnection connection, SqliteTransaction transaction,
        WorkDay? day, DateTimeOffset now, string action, DateTimeOffset registeredAt, DateOnly? date = null)
    {
        var summary = await ReadSummaryAsync(connection, transaction, day, now);
        await ExecuteSqlAsync(connection, transaction, """
            INSERT INTO ActionLog(RegisteredAt, Action, CurrentMode, TotalWorkSeconds, TotalBreakSeconds,
                CurrentPeriodSeconds, IsPaused, WorkDayId, WorkDayDate, IsWorkDayOpen)
            VALUES($at,$action,$mode,$work,$break,$period,$paused,$day,$date,$open);
            """, ("$at", Format(registeredAt)), ("$action", action), ("$mode", summary.ActiveKind.HasValue ? (int)summary.ActiveKind.Value : null),
            ("$work", ToSeconds(summary.TotalWork)), ("$break", ToSeconds(summary.TotalBreak)),
            ("$period", ToSeconds(summary.CurrentPeriod)), ("$paused", summary.IsPaused ? 1 : 0),
            ("$day", day?.Id), ("$date", (day?.Date ?? date)?.ToString("yyyy-MM-dd")), ("$open", day?.IsOpen == true ? 1 : 0));
    }

    private static async Task<WorkDay?> ReadDayAsync(SqliteConnection connection, SqliteTransaction transaction, DateOnly date) =>
        await ReadWorkDayAsync(connection, transaction, "WorkDate=$value", date.ToString("yyyy-MM-dd"));

    private static async Task<WorkDay?> ReadOpenDayAsync(SqliteConnection connection, SqliteTransaction transaction) =>
        await ReadWorkDayAsync(connection, transaction, "StartedAt IS NOT NULL AND EndedAt IS NULL AND 1=$value", 1);

    private static async Task<WorkDay?> ReadWorkDayAsync(SqliteConnection connection, SqliteTransaction transaction, string filter, object value)
    {
        using var command = CreateSql(connection, transaction,
            $"SELECT Id, WorkDate, DayStart, DayEnd, TimeZoneId, StartedAt, EndedAt, LastMode, WasPaused, CloseReason FROM WorkDays WHERE {filter} LIMIT 1;", ("$value", value));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new WorkDay(reader.GetInt64(0), DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd"),
                Parse(reader.GetString(2)), Parse(reader.GetString(3)), reader.GetString(4),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
                (TimeEntryKind)reader.GetInt32(7), reader.GetInt32(8) != 0,
                reader.IsDBNull(9) ? null : (WorkDayCloseReason)reader.GetInt32(9))
            : null;
    }

    private static async Task<WorkDaySettings> ReadSettingsAsync(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = CreateSql(connection, transaction,
            "SELECT AutoStartOnLaunch, RestoreOnLaunch, AutoContinueAtMidnight FROM WorkDaySettings WHERE Id=1;");
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new WorkDaySettings(reader.GetInt32(0) != 0, reader.GetInt32(1) != 0, reader.GetInt32(2) != 0);
    }

    private static async Task<long> ActiveEntryIdAsync(SqliteConnection connection, SqliteTransaction transaction, long dayId) =>
        Convert.ToInt64(await ScalarAsync(connection, transaction,
            "SELECT Id FROM TimeEntries WHERE WorkDayId=$day AND EndedAt IS NULL;", ("$day", dayId))
            ?? throw new InvalidOperationException("Активный период не найден."));

    private static SqliteCommand CreateSql(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static async Task ExecuteSqlAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateSql(connection, transaction, sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateSql(connection, transaction, sql, parameters);
        return await command.ExecuteScalarAsync();
    }

    private static void RequireOpenDay(WorkDay? day)
    {
        if (day?.IsOpen != true)
            throw new InvalidOperationException("Сначала начните рабочий день.");
    }

    private static TimeZoneInfo ResolveTimeZone(string id, TimeZoneInfo fallback)
    {
        if (id == fallback.Id)
            return fallback;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { return fallback; }
        catch (InvalidTimeZoneException) { return fallback; }
    }

    private static string ModeName(TimeEntryKind kind) => kind == TimeEntryKind.Work ? "Работа" : "Перерыв";
    private static string OnOff(bool value) => value ? "включено" : "выключено";
    private static string DurationText(TimeSpan value) => $"{(long)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";

    private static string AdjustmentAction(WorkDayRequest request, TimeTransferPlan plan)
    {
        if (request.Operation == WorkDayOperation.AdjustCurrentPeriod)
            return request.Adjustment switch
            {
                TimeAdjustmentOperation.Add => $"Добавлено {DurationText(plan.Amount)} в текущий период «{ModeName(plan.Source)}»",
                TimeAdjustmentOperation.Remove => $"Убрано {DurationText(plan.Amount)} из текущего периода «{ModeName(plan.Source)}»",
                _ => $"Очищен текущий период «{ModeName(plan.Source)}» ({DurationText(plan.Amount)})"
            };
        return request.Adjustment switch
        {
            TimeAdjustmentOperation.Transfer => $"Перенос {DurationText(plan.Amount)}: {ModeName(plan.Source)} → {ModeName(plan.Target)}",
            TimeAdjustmentOperation.Add => $"Добавлено {DurationText(plan.Amount)} в «{ModeName(plan.Source)}»",
            TimeAdjustmentOperation.Remove => $"Убрано {DurationText(plan.Amount)} из «{ModeName(plan.Source)}»",
            _ => $"Очищено время «{ModeName(plan.Source)}» ({DurationText(plan.Amount)})"
        };
    }
}
