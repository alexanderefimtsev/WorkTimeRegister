using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class TimeSummaryCalculatorTests
{
    private static readonly DateTimeOffset DayStart = new(2026, 8, 19, 0, 0, 0, TimeSpan.FromHours(3));
    private readonly TimeSummaryCalculator calculator = new();

    [Fact]
    public void Calculate_IncludesCompletedWorkAndBreaks()
    {
        var entries = new[]
        {
            Entry(1, TimeEntryKind.Work, 9, 0, 12, 0),
            Entry(2, TimeEntryKind.Break, 12, 0, 12, 30),
            Entry(3, TimeEntryKind.Work, 12, 30, 17, 0)
        };

        var result = calculator.Calculate(entries, DayStart, DayStart.AddDays(1), DayStart.AddHours(18));

        Assert.Equal(TimeSpan.FromHours(7.5), result.TotalWork);
        Assert.Equal(TimeSpan.FromMinutes(30), result.TotalBreak);
        Assert.Null(result.ActiveKind);
    }

    [Fact]
    public void Calculate_UpdatesActiveWorkAndCurrentPeriod()
    {
        var entries = new[] { Entry(1, TimeEntryKind.Work, 9, 15, null, null) };

        var result = calculator.Calculate(entries, DayStart, DayStart.AddDays(1), DayStart.AddHours(11));

        Assert.Equal(TimeSpan.FromHours(1.75), result.TotalWork);
        Assert.Equal(TimeSpan.FromHours(1.75), result.CurrentPeriod);
        Assert.Equal(TimeEntryKind.Work, result.ActiveKind);
    }

    [Fact]
    public void Calculate_UpdatesActiveBreak()
    {
        var entries = new[] { Entry(1, TimeEntryKind.Break, 13, 0, null, null) };

        var result = calculator.Calculate(entries, DayStart, DayStart.AddDays(1), DayStart.AddHours(13.5));

        Assert.Equal(TimeSpan.FromMinutes(30), result.TotalBreak);
        Assert.Equal(TimeSpan.FromMinutes(30), result.CurrentPeriod);
        Assert.Equal(TimeEntryKind.Break, result.ActiveKind);
    }

    [Fact]
    public void Calculate_ClipsPeriodToCurrentDay()
    {
        var entry = new TimeEntry(1, TimeEntryKind.Work, DayStart.AddHours(-1), DayStart.AddHours(2));

        var result = calculator.Calculate([entry], DayStart, DayStart.AddDays(1), DayStart.AddHours(3));

        Assert.Equal(TimeSpan.FromHours(2), result.TotalWork);
    }

    [Fact]
    public void Calculate_AppliesTransferTotalsAndCurrentAdjustment()
    {
        var entry = new TimeEntry(
            1,
            TimeEntryKind.Work,
            DayStart.AddHours(9),
            null,
            TimeSpan.FromMinutes(-20));
        var transfer = new TimeTransfer(
            1,
            DayStart.AddHours(10),
            TimeSpan.FromMinutes(-30),
            TimeSpan.FromMinutes(30));

        var result = calculator.Calculate(
            [entry],
            DayStart,
            DayStart.AddDays(1),
            DayStart.AddHours(10),
            [transfer]);

        Assert.Equal(TimeSpan.FromMinutes(30), result.TotalWork);
        Assert.Equal(TimeSpan.FromMinutes(30), result.TotalBreak);
        Assert.Equal(TimeSpan.FromMinutes(40), result.CurrentPeriod);
    }

    [Fact]
    public void Calculate_OpenPause_FreezesActiveWorkAndCurrentPeriod()
    {
        var entry = Entry(1, TimeEntryKind.Work, 9, 0, null, null);
        var pause = new TimePause(1, entry.Id, DayStart.AddHours(10), null);

        var result = calculator.Calculate(
            [entry],
            DayStart,
            DayStart.AddDays(1),
            DayStart.AddHours(11),
            pauses: [pause]);

        Assert.Equal(TimeSpan.FromHours(1), result.TotalWork);
        Assert.Equal(TimeSpan.FromHours(1), result.CurrentPeriod);
        Assert.Equal(TimeEntryKind.Work, result.ActiveKind);
        Assert.True(result.IsPaused);
    }

    [Fact]
    public void Calculate_CompletedPause_IsExcludedFromBreak()
    {
        var entry = Entry(1, TimeEntryKind.Break, 13, 0, 14, 0);
        var pause = new TimePause(
            1,
            entry.Id,
            DayStart.AddHours(13.25),
            DayStart.AddHours(13.75));

        var result = calculator.Calculate(
            [entry],
            DayStart,
            DayStart.AddDays(1),
            DayStart.AddHours(15),
            pauses: [pause]);

        Assert.Equal(TimeSpan.FromMinutes(30), result.TotalBreak);
        Assert.False(result.IsPaused);
    }

    [Fact]
    public void Calculate_AfterResume_ContinuesSamePeriodWithoutPausedDuration()
    {
        var entry = Entry(1, TimeEntryKind.Work, 9, 0, null, null);
        var pause = new TimePause(
            1,
            entry.Id,
            DayStart.AddHours(10),
            DayStart.AddHours(11));

        var result = calculator.Calculate(
            [entry],
            DayStart,
            DayStart.AddDays(1),
            DayStart.AddHours(12),
            pauses: [pause]);

        Assert.Equal(TimeSpan.FromHours(2), result.TotalWork);
        Assert.Equal(TimeSpan.FromHours(2), result.CurrentPeriod);
        Assert.Equal(TimeEntryKind.Work, result.ActiveKind);
        Assert.False(result.IsPaused);
    }

    [Fact]
    public void Calculate_PauseAcrossMidnight_IsClippedForDailyTotal()
    {
        var entry = new TimeEntry(
            1,
            TimeEntryKind.Work,
            DayStart.AddHours(-1),
            null);
        var pause = new TimePause(
            1,
            entry.Id,
            DayStart.AddMinutes(-30),
            DayStart.AddMinutes(30));

        var result = calculator.Calculate(
            [entry],
            DayStart,
            DayStart.AddDays(1),
            DayStart.AddHours(1),
            pauses: [pause]);

        Assert.Equal(TimeSpan.FromMinutes(30), result.TotalWork);
        Assert.Equal(TimeSpan.FromHours(1), result.CurrentPeriod);
        Assert.False(result.IsPaused);
    }

    private static TimeEntry Entry(long id, TimeEntryKind kind, int startHour, int startMinute, int? endHour, int? endMinute) =>
        new(id, kind, DayStart.AddHours(startHour).AddMinutes(startMinute),
            endHour.HasValue ? DayStart.AddHours(endHour.Value).AddMinutes(endMinute!.Value) : null);
}
