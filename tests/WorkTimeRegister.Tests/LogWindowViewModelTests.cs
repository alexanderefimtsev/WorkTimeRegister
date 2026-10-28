using WorkTimeRegister.Data;
using WorkTimeRegister.ViewModels;
using Xunit;

namespace WorkTimeRegister.Tests;

public sealed class LogWindowViewModelTests
{
    [Fact]
    public async Task SuggestedExportFileName_ContainsSelectedPeriod()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}");
        var repository = new SqliteTimeEntryRepository(Path.Combine(directory, "test.db"));

        try
        {
            await repository.InitializeAsync();
            var viewModel = new LogWindowViewModel(repository, TimeProvider.System)
            {
                From = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(3)),
                To = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.FromHours(3))
            };

            Assert.Equal(
                "WorkTimeRegister-journal-2026-10-01_2026-10-13.json",
                viewModel.SuggestedExportFileName);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
