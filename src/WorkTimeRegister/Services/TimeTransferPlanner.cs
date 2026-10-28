using WorkTimeRegister.Models;

namespace WorkTimeRegister.Services;

public sealed class TimeTransferPlanner
{
    public TimeTransferPlan CreatePlan(TimeSummary summary, TimeEntryKind source, TimeSpan amount)
        => CreateAdjustmentPlan(summary, TimeAdjustmentOperation.Transfer, source, amount);

    public TimeTransferPlan CreateAdjustmentPlan(
        TimeSummary summary,
        TimeAdjustmentOperation operation,
        TimeEntryKind kind,
        TimeSpan amount)
    {
        return operation switch
        {
            TimeAdjustmentOperation.Transfer => CreateTransferPlan(summary, kind, amount),
            TimeAdjustmentOperation.Add => CreateAddPlan(summary, kind, amount),
            TimeAdjustmentOperation.Remove => CreateRemovePlan(summary, kind, amount),
            TimeAdjustmentOperation.Clear => CreateClearPlan(summary, kind),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    public TimeTransferPlan CreateCurrentPeriodPlan(
        TimeSummary summary,
        TimeAdjustmentOperation operation,
        TimeSpan amount)
    {
        var kind = summary.ActiveKind
            ?? throw new InvalidOperationException("Нет активного периода для изменения.");

        return operation switch
        {
            TimeAdjustmentOperation.Add => CreateAddPlan(summary, kind, amount),
            TimeAdjustmentOperation.Remove => CreateCurrentPeriodRemovePlan(summary, kind, amount),
            TimeAdjustmentOperation.Clear => CreateCurrentPeriodClearPlan(summary, kind),
            _ => throw new InvalidOperationException("Для текущего периода доступны добавление, вычитание и очистка.")
        };
    }

    private static TimeTransferPlan CreateTransferPlan(TimeSummary summary, TimeEntryKind source, TimeSpan amount)
    {
        EnsurePositive(amount);
        var sourceTotal = source == TimeEntryKind.Work ? summary.TotalWork : summary.TotalBreak;
        EnsureAvailable(amount, sourceTotal);

        var target = source == TimeEntryKind.Work ? TimeEntryKind.Break : TimeEntryKind.Work;
        var currentAdjustment = TimeSpan.Zero;
        if (summary.ActiveKind == target)
            currentAdjustment = amount;
        else if (summary.ActiveKind == source)
            currentAdjustment = -Min(amount, summary.CurrentPeriod);

        var workAdjustment = source == TimeEntryKind.Work ? -amount : amount;
        var breakAdjustment = source == TimeEntryKind.Break ? -amount : amount;
        return new TimeTransferPlan(source, target, amount, workAdjustment, breakAdjustment, currentAdjustment);
    }

    private static TimeTransferPlan CreateAddPlan(TimeSummary summary, TimeEntryKind kind, TimeSpan amount)
    {
        EnsurePositive(amount);
        var currentAdjustment = summary.ActiveKind == kind ? amount : TimeSpan.Zero;
        return CreateSingleCategoryPlan(kind, amount, amount, currentAdjustment);
    }

    private static TimeTransferPlan CreateRemovePlan(TimeSummary summary, TimeEntryKind kind, TimeSpan amount)
    {
        EnsurePositive(amount);
        var available = kind == TimeEntryKind.Work ? summary.TotalWork : summary.TotalBreak;
        EnsureAvailable(amount, available);
        var currentAdjustment = summary.ActiveKind == kind
            ? -Min(amount, summary.CurrentPeriod)
            : TimeSpan.Zero;
        return CreateSingleCategoryPlan(kind, amount, -amount, currentAdjustment);
    }

    private static TimeTransferPlan CreateCurrentPeriodRemovePlan(
        TimeSummary summary,
        TimeEntryKind kind,
        TimeSpan amount)
    {
        EnsurePositive(amount);
        EnsureAvailable(amount, summary.CurrentPeriod);
        var categoryAvailable = kind == TimeEntryKind.Work ? summary.TotalWork : summary.TotalBreak;
        return CreateSingleCategoryPlan(kind, amount, -Min(amount, categoryAvailable), -amount);
    }

    private static TimeTransferPlan CreateCurrentPeriodClearPlan(TimeSummary summary, TimeEntryKind kind)
    {
        var categoryAvailable = kind == TimeEntryKind.Work ? summary.TotalWork : summary.TotalBreak;
        return CreateSingleCategoryPlan(
            kind,
            summary.CurrentPeriod,
            -Min(summary.CurrentPeriod, categoryAvailable),
            -summary.CurrentPeriod);
    }

    private static TimeTransferPlan CreateClearPlan(TimeSummary summary, TimeEntryKind kind)
    {
        var amount = kind == TimeEntryKind.Work ? summary.TotalWork : summary.TotalBreak;
        var currentAdjustment = summary.ActiveKind == kind ? -summary.CurrentPeriod : TimeSpan.Zero;
        return CreateSingleCategoryPlan(kind, amount, -amount, currentAdjustment);
    }

    private static TimeTransferPlan CreateSingleCategoryPlan(
        TimeEntryKind kind,
        TimeSpan amount,
        TimeSpan categoryAdjustment,
        TimeSpan currentAdjustment)
    {
        var workAdjustment = kind == TimeEntryKind.Work ? categoryAdjustment : TimeSpan.Zero;
        var breakAdjustment = kind == TimeEntryKind.Break ? categoryAdjustment : TimeSpan.Zero;
        return new TimeTransferPlan(kind, kind, amount, workAdjustment, breakAdjustment, currentAdjustment);
    }

    private static void EnsurePositive(TimeSpan amount)
    {
        if (amount <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(amount), "Количество времени должно быть больше нуля.");
    }

    private static void EnsureAvailable(TimeSpan amount, TimeSpan available)
    {
        if (amount > available)
            throw new InvalidOperationException("Количество времени превышает доступное время.");
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;
}
