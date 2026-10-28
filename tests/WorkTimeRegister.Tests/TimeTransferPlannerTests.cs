using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class TimeTransferPlannerTests
{
    private readonly TimeTransferPlanner planner = new();

    [Fact]
    public void CreatePlan_WhenTargetMatchesCurrentMode_AddsToCurrentPeriod()
    {
        var summary = new TimeSummary(
            TimeSpan.FromHours(2),
            TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(20),
            TimeEntryKind.Work);

        var result = planner.CreatePlan(summary, TimeEntryKind.Break, TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.FromMinutes(15), result.WorkAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(-15), result.BreakAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(15), result.CurrentPeriodAdjustment);
    }

    [Fact]
    public void CreatePlan_WhenSourceMatchesCurrentMode_SubtractsCurrentPeriodOnlyToZero()
    {
        var summary = new TimeSummary(
            TimeSpan.FromHours(2),
            TimeSpan.FromHours(1),
            TimeSpan.FromMinutes(10),
            TimeEntryKind.Work);

        var result = planner.CreatePlan(summary, TimeEntryKind.Work, TimeSpan.FromMinutes(30));

        Assert.Equal(TimeSpan.FromMinutes(-30), result.WorkAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(30), result.BreakAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(-10), result.CurrentPeriodAdjustment);
    }

    [Fact]
    public void CreatePlan_RejectsAmountGreaterThanSource()
    {
        var summary = new TimeSummary(
            TimeSpan.FromMinutes(10),
            TimeSpan.Zero,
            TimeSpan.Zero,
            null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            planner.CreatePlan(summary, TimeEntryKind.Work, TimeSpan.FromMinutes(11)));

        Assert.Contains("превышает", exception.Message);
    }

    [Fact]
    public void CreatePlan_RejectsNonPositiveAmount()
    {
        var summary = new TimeSummary(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            planner.CreatePlan(summary, TimeEntryKind.Work, TimeSpan.Zero));
    }

    [Fact]
    public void CreateAdjustmentPlan_AddToActiveCategory_IncreasesTotalAndCurrentPeriod()
    {
        var summary = new TimeSummary(
            TimeSpan.FromHours(2),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(20),
            TimeEntryKind.Work);

        var result = planner.CreateAdjustmentPlan(
            summary,
            TimeAdjustmentOperation.Add,
            TimeEntryKind.Work,
            TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.FromMinutes(15), result.WorkAdjustment);
        Assert.Equal(TimeSpan.Zero, result.BreakAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(15), result.CurrentPeriodAdjustment);
    }

    [Fact]
    public void CreateAdjustmentPlan_AddToInactiveCategory_DoesNotChangeCurrentPeriod()
    {
        var summary = new TimeSummary(
            TimeSpan.FromHours(2),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(20),
            TimeEntryKind.Work);

        var result = planner.CreateAdjustmentPlan(
            summary,
            TimeAdjustmentOperation.Add,
            TimeEntryKind.Break,
            TimeSpan.FromMinutes(15));

        Assert.Equal(TimeSpan.Zero, result.WorkAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(15), result.BreakAdjustment);
        Assert.Equal(TimeSpan.Zero, result.CurrentPeriodAdjustment);
    }

    [Fact]
    public void CreateAdjustmentPlan_RemoveFromActiveCategory_ReducesCurrentPeriodOnlyToZero()
    {
        var summary = new TimeSummary(
            TimeSpan.FromHours(2),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(10),
            TimeEntryKind.Work);

        var result = planner.CreateAdjustmentPlan(
            summary,
            TimeAdjustmentOperation.Remove,
            TimeEntryKind.Work,
            TimeSpan.FromMinutes(25));

        Assert.Equal(TimeSpan.FromMinutes(-25), result.WorkAdjustment);
        Assert.Equal(TimeSpan.Zero, result.BreakAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(-10), result.CurrentPeriodAdjustment);
    }

    [Fact]
    public void CreateAdjustmentPlan_RemoveMoreThanAvailable_Throws()
    {
        var summary = new TimeSummary(
            TimeSpan.FromMinutes(10),
            TimeSpan.Zero,
            TimeSpan.Zero,
            null);

        Assert.Throws<InvalidOperationException>(() =>
            planner.CreateAdjustmentPlan(
                summary,
                TimeAdjustmentOperation.Remove,
                TimeEntryKind.Work,
                TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void CreateAdjustmentPlan_ClearActiveCategory_ResetsTotalAndCurrentPeriod()
    {
        var summary = new TimeSummary(
            TimeSpan.FromHours(2),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(12),
            TimeEntryKind.Break);

        var result = planner.CreateAdjustmentPlan(
            summary,
            TimeAdjustmentOperation.Clear,
            TimeEntryKind.Break,
            TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, result.WorkAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(-30), result.BreakAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(-12), result.CurrentPeriodAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Amount);
    }

    [Fact]
    public void CreateAdjustmentPlan_ClearInactiveCategory_DoesNotChangeCurrentPeriod()
    {
        var summary = new TimeSummary(
            TimeSpan.FromHours(2),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(12),
            TimeEntryKind.Break);

        var result = planner.CreateAdjustmentPlan(
            summary,
            TimeAdjustmentOperation.Clear,
            TimeEntryKind.Work,
            TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromHours(-2), result.WorkAdjustment);
        Assert.Equal(TimeSpan.Zero, result.BreakAdjustment);
        Assert.Equal(TimeSpan.Zero, result.CurrentPeriodAdjustment);
    }

    [Fact]
    public void CreateCurrentPeriodPlan_Remove_RejectsAmountGreaterThanCurrentPeriod()
    {
        var summary = new TimeSummary(
            TimeSpan.FromHours(2),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(10),
            TimeEntryKind.Work);

        Assert.Throws<InvalidOperationException>(() =>
            planner.CreateCurrentPeriodPlan(
                summary,
                TimeAdjustmentOperation.Remove,
                TimeSpan.FromMinutes(11)));
    }

    [Fact]
    public void CreateCurrentPeriodPlan_Clear_RemovesOnlyCurrentPeriodFromActiveCategory()
    {
        var summary = new TimeSummary(
            TimeSpan.FromHours(2),
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(15),
            TimeEntryKind.Work,
            true);

        var result = planner.CreateCurrentPeriodPlan(
            summary,
            TimeAdjustmentOperation.Clear,
            TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMinutes(-15), result.WorkAdjustment);
        Assert.Equal(TimeSpan.Zero, result.BreakAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(-15), result.CurrentPeriodAdjustment);
        Assert.Equal(TimeSpan.FromMinutes(15), result.Amount);
    }

    [Fact]
    public void CreateCurrentPeriodPlan_Clear_DoesNotMakeTodayTotalNegative()
    {
        var summary = new TimeSummary(
            TimeSpan.FromMinutes(10),
            TimeSpan.Zero,
            TimeSpan.FromHours(2),
            TimeEntryKind.Work);

        var result = planner.CreateCurrentPeriodPlan(
            summary,
            TimeAdjustmentOperation.Clear,
            TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromMinutes(-10), result.WorkAdjustment);
        Assert.Equal(TimeSpan.FromHours(-2), result.CurrentPeriodAdjustment);
    }
}
