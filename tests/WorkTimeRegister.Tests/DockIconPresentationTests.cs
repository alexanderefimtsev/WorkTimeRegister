using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;
using WorkTimeRegister.ViewModels;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class DockIconPresentationTests
{
    [Fact]
    public void Create_MapsWorkBreakPauseAndIdleStates()
    {
        Assert.Null(DockIconPresentation.Create(
            null,
            false,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero));

        var work = DockIconPresentation.Create(
            TimeEntryKind.Work,
            false,
            new TimeSpan(1, 2, 3),
            new TimeSpan(7, 8, 9),
            TimeSpan.FromMinutes(45));
        Assert.Equal(DockIconMode.Work, work!.Mode);
        Assert.Equal("РАБОТА", work.ModeText);
        Assert.Equal("1h 2m", work.TimeText);
        Assert.Equal("7h 8m", work.TotalTimeText);

        var rest = DockIconPresentation.Create(
            TimeEntryKind.Break,
            false,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromHours(6),
            new TimeSpan(0, 48, 12));
        Assert.Equal(DockIconMode.Break, rest!.Mode);
        Assert.Equal("ПЕРЕРЫВ", rest.ModeText);
        Assert.Equal("15m", rest.TimeText);
        Assert.Equal("48m", rest.TotalTimeText);

        var pause = DockIconPresentation.Create(
            TimeEntryKind.Break,
            true,
            TimeSpan.FromHours(27) + TimeSpan.FromSeconds(4),
            TimeSpan.FromHours(30),
            TimeSpan.FromHours(31) + TimeSpan.FromMinutes(2));
        Assert.Equal(DockIconMode.Pause, pause!.Mode);
        Assert.Equal("ПАУЗА", pause.ModeText);
        Assert.Equal("27h", pause.TimeText);
        Assert.Equal("31h 2m", pause.TotalTimeText);
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "0m")]
    [InlineData(60, "1m")]
    [InlineData(3599, "59m")]
    [InlineData(3600, "1h")]
    [InlineData(3900, "1h 5m")]
    [InlineData(-1, "0m")]
    [InlineData(90061, "25h 1m")]
    public void Create_FormatsWholeMinutesWithoutLeadingZerosOrSeconds(int seconds, string expected)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        var presentation = DockIconPresentation.Create(TimeEntryKind.Work, false, duration, duration, TimeSpan.Zero)!;
        Assert.Equal(expected, presentation.TimeText);
        Assert.Equal(expected, presentation.TotalTimeText);
    }

    [Fact]
    public async Task Coordinator_UpdatesPausesAndResetsTheDockIcon()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));
        var timeProvider = new FixedTimeProvider(
            new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(3)));

        try
        {
            await repository.InitializeAsync();
            await repository.StartAsync(TimeEntryKind.Work, timeProvider.GetLocalNow().AddHours(-1));
            using var viewModel = new MainWindowViewModel(
                repository,
                new TimeSummaryCalculator(),
                timeProvider);
            var service = new RecordingDockIconService();
            var coordinator = new DockIconCoordinator(viewModel, service);

            await WaitForAsync(() => service.LastPresentation?.Mode == DockIconMode.Work);
            Assert.Equal("1h", service.LastPresentation!.TimeText);
            Assert.Equal("1h", service.LastPresentation.TotalTimeText);

            await viewModel.TogglePauseAsync();
            Assert.Equal(DockIconMode.Pause, service.LastPresentation!.Mode);
            Assert.Equal("1h", service.LastPresentation.TimeText);
            Assert.Equal("1h", service.LastPresentation.TotalTimeText);

            coordinator.Dispose();
            Assert.Null(service.LastPresentation);
            Assert.True(service.IsDisposed);
            var updateCount = service.UpdateCount;

            await viewModel.TogglePauseAsync();
            Assert.Equal(updateCount, service.UpdateCount);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private sealed class RecordingDockIconService : IDockIconService
    {
        private readonly object sync = new();
        private DockIconPresentation? lastPresentation;
        private int updateCount;

        public DockIconPresentation? LastPresentation
        {
            get
            {
                lock (sync)
                    return lastPresentation;
            }
        }

        public int UpdateCount
        {
            get
            {
                lock (sync)
                    return updateCount;
            }
        }

        public bool IsDisposed { get; private set; }

        public void Update(DockIconPresentation? presentation)
        {
            lock (sync)
            {
                lastPresentation = presentation;
                updateCount++;
            }
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private readonly TimeZoneInfo timeZone = TimeZoneInfo.CreateCustomTimeZone(
            "WorkTimeRegisterDockIconTests",
            current.Offset,
            "WorkTimeRegisterDockIconTests",
            "WorkTimeRegisterDockIconTests");

        public override TimeZoneInfo LocalTimeZone => timeZone;
        public override DateTimeOffset GetUtcNow() => current.ToUniversalTime();
    }
}
