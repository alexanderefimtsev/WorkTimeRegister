using System.Security.Cryptography;
using System.Text;
using WorkTimeRegister.Services;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class SingleInstanceServiceTests
{
    [Fact]
    public async Task SecondInstance_NotifiesExistingProcessAndCannotTakeItsLease()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Instance-{Guid.NewGuid():N}", "test.db");
        var activated = 0;
        using (var first = SingleInstanceService.TryAcquire(path))
        {
            Assert.NotNull(first);
            first.ActivateRequested = () => { activated++; return Task.FromResult(true); };
            first.ExitRequested = () => Task.FromResult(false);
            Assert.Null(SingleInstanceService.TryAcquire(path));
            Assert.True(await SingleInstanceService.SendAsync(path, exit: false, TestContext.Current.CancellationToken));
            Assert.Equal(1, activated);
            Assert.False(await SingleInstanceService.SendAsync(path, exit: true, TestContext.Current.CancellationToken));
        }
        using var restarted = SingleInstanceService.TryAcquire(path);
        Assert.NotNull(restarted);
    }

    [Fact]
    public async Task MissingInstance_LeaseCheckReturnsImmediately()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Instance-{Guid.NewGuid():N}", "test.db");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(SingleInstanceService.HasActiveLease(path));
        Assert.True(await SingleInstanceService.RequestShutdownAsync(path, TestContext.Current.CancellationToken));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        using var active = SingleInstanceService.TryAcquire(path);
        Assert.NotNull(active);
        Assert.True(SingleInstanceService.HasActiveLease(path));
    }

    [Fact]
    public async Task Shutdown_WaitsUntilTheActiveLeaseIsReleased()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Instance-{Guid.NewGuid():N}", "test.db");
        using var first = SingleInstanceService.TryAcquire(path);
        Assert.NotNull(first);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ExitRequested = () =>
        {
            requested.TrySetResult();
            return Task.FromResult(true);
        };
        var shutdown = SingleInstanceService.RequestShutdownAsync(path, TestContext.Current.CancellationToken);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(shutdown.IsCompleted);
        first.Dispose();
        Assert.True(await shutdown);
    }

    [Fact]
    public async Task LongCyrillicDataPath_StillSupportsInstanceNotification()
    {
        var path = Path.Combine(Path.GetTempPath(), "Длинная-папка-" + new string('я', 120),
            $"Instance-{Guid.NewGuid():N}", "test.db");
        using var first = SingleInstanceService.TryAcquire(path);
        Assert.NotNull(first);
        first.ActivateRequested = () => Task.FromResult(true);
        Assert.True(await SingleInstanceService.SendAsync(path, exit: false, TestContext.Current.CancellationToken));
        Assert.Null(SingleInstanceService.TryAcquire(path));
    }

    [Fact]
    public void LegacyLease_PreventsConcurrentUpgradeLaunch()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(Path.GetTempPath(), $"Instance-{Guid.NewGuid():N}", "test.db");
        var identity = "WorkTimeRegister-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..24];
        var legacyPath = Path.Combine(Path.GetTempPath(), identity + ".lock");
        using (var legacy = new FileStream(legacyPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(SingleInstanceService.TryAcquire(path));
            Assert.True(SingleInstanceService.HasActiveLease(path));
        }
        using var upgraded = SingleInstanceService.TryAcquire(path);
        Assert.NotNull(upgraded);
    }

    [Fact]
    public async Task Activation_RetriesWhileFirstInstanceFinishesStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Instance-{Guid.NewGuid():N}", "test.db");
        using var first = SingleInstanceService.TryAcquire(path);
        Assert.NotNull(first);
        var activation = SingleInstanceService.ActivateExistingAsync(path, TestContext.Current.CancellationToken);
        await Task.Delay(350, TestContext.Current.CancellationToken);
        var received = 0;
        first.ActivateRequested = () => { received++; return Task.FromResult(true); };
        Assert.True(await activation);
        Assert.Equal(1, received);
    }

    [Fact]
    public void PersistentLease_PreventsDuplicateAfterTemporaryLocksAreRemoved()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(Path.GetTempPath(), $"Instance-{Guid.NewGuid():N}", "test.db");
        var identity = "WorkTimeRegister-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..24];
        var user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))))[..12];
        var previousPath = Path.Combine("/tmp", "wtr-" + user, "lease-" + identity[^24..]);
        var legacyPath = Path.Combine(Path.GetTempPath(), identity + ".lock");
        using var first = SingleInstanceService.TryAcquire(path);
        Assert.NotNull(first);
        File.Delete(previousPath);
        File.Delete(legacyPath);
        Assert.Null(SingleInstanceService.TryAcquire(path));
        Assert.True(SingleInstanceService.HasActiveLease(path));
    }
}
