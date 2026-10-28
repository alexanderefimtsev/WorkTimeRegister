namespace WorkTimeRegister.Models;

public sealed record TimeTransferPlan(
    TimeEntryKind Source,
    TimeEntryKind Target,
    TimeSpan Amount,
    TimeSpan WorkAdjustment,
    TimeSpan BreakAdjustment,
    TimeSpan CurrentPeriodAdjustment);
