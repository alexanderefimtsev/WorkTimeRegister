using System.Text;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class ActionLogJsonServiceTests
{
    private readonly ActionLogJsonService service = new();

    [Fact]
    public async Task ExportAndImport_PreservesJournalData()
    {
        var registeredAt = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.FromHours(3));
        var records = new[]
        {
            new ActionLogRecord(
                42,
                registeredAt,
                "Добавлено 00:15:00 в «Работа»",
                TimeEntryKind.Work,
                TimeSpan.FromHours(2),
                TimeSpan.FromMinutes(20),
                TimeSpan.FromMinutes(30),
                true)
            {
                WorkDayDate = new DateOnly(2026, 8, 20),
                IsWorkDayOpen = true
            },
            new ActionLogRecord(
                43,
                registeredAt.AddHours(1),
                "Окончание работы",
                null,
                TimeSpan.FromHours(3),
                TimeSpan.FromMinutes(20),
                TimeSpan.Zero)
        };
        await using var stream = new MemoryStream();

        await service.ExportAsync(
            stream,
            records,
            registeredAt.AddHours(2),
            TestContext.Current.CancellationToken);
        var json = Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;
        var imported = await service.ImportAsync(stream, TestContext.Current.CancellationToken);

        Assert.Contains("\"format\": \"WorkTimeRegister.ActionLog\"", json);
        Assert.Contains("\"currentMode\": \"work\"", json);
        Assert.Contains("\"isPaused\": true", json);
        Assert.Equal(2, imported.Count);
        Assert.Equal(0, imported[0].Id);
        Assert.Equal(records[0].RegisteredAt, imported[0].RegisteredAt);
        Assert.Equal(records[0].Action, imported[0].Action);
        Assert.Equal(records[0].CurrentMode, imported[0].CurrentMode);
        Assert.Equal(records[0].TotalWork, imported[0].TotalWork);
        Assert.Equal(records[0].TotalBreak, imported[0].TotalBreak);
        Assert.Equal(records[0].CurrentPeriod, imported[0].CurrentPeriod);
        Assert.True(imported[0].IsPaused);
        Assert.Equal(records[0].WorkDayDate, imported[0].WorkDayDate);
        Assert.Equal(records[0].IsWorkDayOpen, imported[0].IsWorkDayOpen);
        Assert.Contains("\"version\": 2", json);
        Assert.Null(imported[1].CurrentMode);
    }

    [Fact]
    public async Task Import_RejectsUnsupportedFormat()
    {
        await using var stream = CreateStream("""
            {
              "format": "Unknown",
              "version": 1,
              "entries": []
            }
            """);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportAsync(stream, TestContext.Current.CancellationToken));

        Assert.Contains("неподдерживаемый формат", exception.Message);
    }

    [Fact]
    public async Task Import_RejectsNegativeDuration()
    {
        await using var stream = CreateStream("""
            {
              "format": "WorkTimeRegister.ActionLog",
              "version": 1,
              "entries": [
                {
                  "registeredAt": "2026-08-20T10:00:00+03:00",
                  "action": "Начало работы",
                  "currentMode": "work",
                  "totalWorkSeconds": -1,
                  "totalBreakSeconds": 0,
                  "currentPeriodSeconds": 0
                }
              ]
            }
            """);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportAsync(stream, TestContext.Current.CancellationToken));

        Assert.Contains("Работа сегодня", exception.Message);
    }

    [Fact]
    public async Task Import_OldJsonWithoutPauseField_RemainsSupported()
    {
        await using var stream = CreateStream("""
            {
              "format": "WorkTimeRegister.ActionLog",
              "version": 1,
              "entries": [
                {
                  "registeredAt": "2026-08-20T10:00:00+03:00",
                  "action": "Начало работы",
                  "currentMode": "work",
                  "totalWorkSeconds": 0,
                  "totalBreakSeconds": 0,
                  "currentPeriodSeconds": 0
                }
              ]
            }
            """);

        var records = await service.ImportAsync(stream, TestContext.Current.CancellationToken);

        Assert.Single(records);
        Assert.False(records[0].IsPaused);
        Assert.Null(records[0].WorkDayDate);
        Assert.Null(records[0].IsWorkDayOpen);
    }

    private static MemoryStream CreateStream(string json) => new(Encoding.UTF8.GetBytes(json));
}
