using WorkTimeRegister.Models;

namespace WorkTimeRegister.Data;

public interface ITimeEntryRepository
{
    Task<WorkDaySnapshot> ProcessWorkDayAsync(WorkDayRequest request);
    Task<IReadOnlyList<TimeEntry>> GetEntriesAsync(DateTimeOffset from, DateTimeOffset to);
    Task<IReadOnlyList<TimeTransfer>> GetTransfersAsync(DateTimeOffset from, DateTimeOffset to);
    Task<IReadOnlyList<TimePause>> GetPausesAsync(DateTimeOffset from, DateTimeOffset to);
    Task<IReadOnlyList<ActionLogRecord>> GetActionLogAsync(DateTimeOffset from, DateTimeOffset to);
    Task<TimeEntry?> GetActiveEntryAsync();
    Task StartAsync(TimeEntryKind kind, DateTimeOffset startedAt);
    Task StopActiveAsync(DateTimeOffset endedAt);
    Task StartPauseAsync(long activeEntryId, DateTimeOffset startedAt);
    Task EndPauseAsync(DateTimeOffset endedAt);
    Task AddTransferAsync(TimeTransferPlan plan, long? activeEntryId, DateTimeOffset registeredAt);
    Task AddActionLogAsync(ActionLogRecord record);
    Task<int> ImportActionLogAsync(IReadOnlyCollection<ActionLogRecord> records);
}
