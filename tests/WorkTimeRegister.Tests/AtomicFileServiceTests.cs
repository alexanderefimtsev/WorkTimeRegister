using WorkTimeRegister.Services;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class AtomicFileServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedExport_PreservesExistingFileAndRemovesTemporaryFile(bool throws)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AtomicExport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "journal.json");
        try
        {
            await File.WriteAllTextAsync(path, "original journal", TestContext.Current.CancellationToken);
            async Task<bool> Write(Stream stream)
            {
                await stream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
                if (throws) throw new IOException("Simulated write failure");
                return false;
            }
            if (throws)
                await Assert.ThrowsAsync<IOException>(() => new AtomicFileService().WriteAsync(path, Write));
            else
                Assert.False(await new AtomicFileService().WriteAsync(path, Write));
            Assert.Equal("original journal", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task SuccessfulExport_ReplacesExistingFileAfterCompleteWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AtomicExport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "journal.json");
        try
        {
            await File.WriteAllTextAsync(path, "long previous journal", TestContext.Current.CancellationToken);
            Assert.True(await new AtomicFileService().WriteAsync(path, async stream =>
            {
                Assert.Equal("long previous journal", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
                await stream.WriteAsync(new byte[] { 65, 66 }, TestContext.Current.CancellationToken);
                return true;
            }));
            Assert.Equal("AB", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
