using WorkTimeRegister.Models;

namespace WorkTimeRegister.Services;

public sealed class TimeSummaryCalculator
{
    public TimeSummary Calculate(
        IEnumerable<TimeEntry> entries,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        DateTimeOffset now,
        IEnumerable<TimeTransfer>? transfers = null,
        IEnumerable<TimePause>? pauses = null)
    {
        var pauseList = pauses?.ToList() ?? [];
        var work = TimeSpan.Zero;
        var breaks = TimeSpan.Zero;
        var current = TimeSpan.Zero;
        TimeEntryKind? activeKind = null;
        var isPaused = false;

        foreach (var entry in entries)
        {
            var start = entry.StartedAt < dayStart ? dayStart : entry.StartedAt;
            var effectiveEnd = entry.EndedAt ?? now;
            var end = effectiveEnd > dayEnd ? dayEnd : effectiveEnd;
            var duration = end > start
                ? end - start - GetPausedDuration(pauseList, entry.Id, start, end, now)
                : TimeSpan.Zero;
            if (duration < TimeSpan.Zero)
                duration = TimeSpan.Zero;

            if (entry.Kind == TimeEntryKind.Work)
                work += duration;
            else
                breaks += duration;

            if (entry.EndedAt is null)
            {
                activeKind = entry.Kind;
                var elapsed = now > entry.StartedAt ? now - entry.StartedAt : TimeSpan.Zero;
                elapsed -= GetPausedDuration(pauseList, entry.Id, entry.StartedAt, now, now);
                current = elapsed + entry.CurrentPeriodAdjustment;
                if (current < TimeSpan.Zero)
                    current = TimeSpan.Zero;
                isPaused = pauseList.Any(pause => pause.TimeEntryId == entry.Id && pause.EndedAt is null);
            }
        }

        foreach (var transfer in transfers ?? [])
        {
            work += transfer.WorkAdjustment;
            breaks += transfer.BreakAdjustment;
        }

        return new TimeSummary(work, breaks, current, activeKind, isPaused);
    }

    private static TimeSpan GetPausedDuration(
        IEnumerable<TimePause> pauses,
        long entryId,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now)
    {
        var result = TimeSpan.Zero;
        foreach (var pause in pauses.Where(pause => pause.TimeEntryId == entryId))
        {
            var start = pause.StartedAt < from ? from : pause.StartedAt;
            var effectiveEnd = pause.EndedAt ?? now;
            var end = effectiveEnd > to ? to : effectiveEnd;
            if (end > start)
                result += end - start;
        }

        return result;
    }
}
