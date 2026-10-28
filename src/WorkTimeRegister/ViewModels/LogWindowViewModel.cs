using System.Collections.ObjectModel;
using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;

namespace WorkTimeRegister.ViewModels;

public sealed class LogWindowViewModel : ViewModelBase
{
    private readonly ITimeEntryRepository repository;
    private readonly TimeProvider timeProvider;
    private readonly ActionLogJsonService jsonService;
    private DateTimeOffset? from;
    private DateTimeOffset? to;
    private string? errorMessage;
    private string? statusMessage;

    public LogWindowViewModel(
        ITimeEntryRepository repository,
        TimeProvider timeProvider,
        ActionLogJsonService? jsonService = null)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
        this.jsonService = jsonService ?? new ActionLogJsonService();
        var today = timeProvider.GetLocalNow().Date;
        from = new DateTimeOffset(today, timeProvider.GetLocalNow().Offset);
        to = from;
        ApplyFilterCommand = new AsyncCommand(LoadAsync);
        _ = LoadAsync();
    }

    public DateTimeOffset? From
    {
        get => from;
        set
        {
            from = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SuggestedExportFileName));
        }
    }

    public DateTimeOffset? To
    {
        get => to;
        set
        {
            to = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SuggestedExportFileName));
        }
    }

    public string SuggestedExportFileName
    {
        get
        {
            var fallback = timeProvider.GetLocalNow().Date;
            var fromDate = From?.Date ?? fallback;
            var toDate = To?.Date ?? fromDate;
            return $"WorkTimeRegister-journal-{fromDate:yyyy-MM-dd}_{toDate:yyyy-MM-dd}.json";
        }
    }
    public string? ErrorMessage { get => errorMessage; private set { errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? StatusMessage { get => statusMessage; private set { statusMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public ObservableCollection<ActionLogRowViewModel> Entries { get; } = [];
    public AsyncCommand ApplyFilterCommand { get; }

    public void ReportFileError(string operation, Exception exception)
    {
        StatusMessage = null;
        ErrorMessage = $"Не удалось {operation}: {exception.Message}";
    }

    public async Task<bool> ExportAsync(Stream stream)
    {
        try
        {
            var records = await GetFilteredRecordsAsync();
            await jsonService.ExportAsync(stream, records, timeProvider.GetUtcNow());
            await stream.FlushAsync();
            ErrorMessage = null;
            StatusMessage = $"Экспортировано записей: {records.Count}.";
            return true;
        }
        catch (Exception exception)
        {
            StatusMessage = null;
            ErrorMessage = $"Не удалось экспортировать журнал: {exception.Message}";
            return false;
        }
    }

    public async Task<bool> ImportAsync(Stream stream)
    {
        try
        {
            var records = await jsonService.ImportAsync(stream);
            var importedCount = await repository.ImportActionLogAsync(records);
            await LoadCoreAsync();
            var duplicateCount = records.Count - importedCount;
            ErrorMessage = null;
            StatusMessage = duplicateCount == 0
                ? $"Импортировано записей: {importedCount}."
                : $"Импортировано записей: {importedCount}, пропущено дубликатов: {duplicateCount}.";
            return true;
        }
        catch (Exception exception)
        {
            StatusMessage = null;
            ErrorMessage = $"Не удалось импортировать журнал: {exception.Message}";
            return false;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            await LoadCoreAsync();
            ErrorMessage = null;
            StatusMessage = null;
        }
        catch (Exception exception)
        {
            StatusMessage = null;
            ErrorMessage = exception.Message;
        }
    }

    private async Task LoadCoreAsync()
    {
        var records = await GetFilteredRecordsAsync();
        Entries.Clear();
        foreach (var record in records)
            Entries.Add(new ActionLogRowViewModel(record));
    }

    private async Task<IReadOnlyList<ActionLogRecord>> GetFilteredRecordsAsync()
    {
        if (!From.HasValue || !To.HasValue)
            throw new InvalidOperationException("Укажите начало и конец периода.");
        if (From.Value.Date > To.Value.Date)
            throw new InvalidOperationException("Начало периода не может быть позже конца.");

        var start = new DateTimeOffset(From.Value.Date, From.Value.Offset);
        var end = new DateTimeOffset(To.Value.Date, To.Value.Offset).AddDays(1);
        return await repository.GetActionLogAsync(start, end);
    }
}
