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
}
