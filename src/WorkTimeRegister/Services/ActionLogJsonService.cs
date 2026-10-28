using System.Text.Json;
using System.Text.Json.Serialization;
using WorkTimeRegister.Models;

namespace WorkTimeRegister.Services;

public sealed class ActionLogJsonService
{
    public const long MaximumImportBytes = 128L * 1024 * 1024;
    private readonly long maximumImportBytes;
    private const string DocumentFormat = "WorkTimeRegister.ActionLog";
    private const int DocumentVersion = 2;
    private const int MaximumEntryCount = 100_000;
    private const int MaximumActionLength = 1_000;
    private static readonly long MaximumDurationSeconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public ActionLogJsonService(long maximumImportBytes = MaximumImportBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumImportBytes);
        this.maximumImportBytes = maximumImportBytes;
    }

    public Task ExportAsync(
        Stream stream,
        IReadOnlyCollection<ActionLogRecord> records,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(records);

        var document = new ActionLogExportDocument
        {
            Format = DocumentFormat,
            Version = DocumentVersion,
            ExportedAt = exportedAt,
            Entries = records.Select(ToExportEntry).ToList()
        };

        return JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<ActionLogRecord>> ImportAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.CanSeek && stream.Length - stream.Position > maximumImportBytes)
            throw new InvalidDataException("JSON-файл превышает допустимый размер для импорта (128 МиБ по умолчанию).");
        using var limited = new SizeLimitedReadStream(stream, maximumImportBytes);
        ActionLogExportDocument document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<ActionLogExportDocument>(
                    limited,
                    SerializerOptions,
                    cancellationToken)
                ?? throw new InvalidDataException("JSON-файл журнала пуст.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Некорректный JSON-файл журнала.", exception);
        }

        if (!string.Equals(document.Format, DocumentFormat, StringComparison.Ordinal))
            throw new InvalidDataException("Файл имеет неподдерживаемый формат журнала.");
        if (document.Version is not (1 or DocumentVersion))
            throw new InvalidDataException($"Версия формата журнала {document.Version} не поддерживается.");
        if (document.Entries is null)
            throw new InvalidDataException("В файле отсутствует список записей журнала.");
        if (document.Entries.Count > MaximumEntryCount)
            throw new InvalidDataException($"Файл содержит более {MaximumEntryCount} записей.");

        var records = new List<ActionLogRecord>(document.Entries.Count);
        foreach (var entry in document.Entries)
        {
            Validate(entry);
            records.Add(new ActionLogRecord(
                0,
                entry.RegisteredAt,
                entry.Action!,
                entry.CurrentMode,
                TimeSpan.FromSeconds(entry.TotalWorkSeconds),
                TimeSpan.FromSeconds(entry.TotalBreakSeconds),
                TimeSpan.FromSeconds(entry.CurrentPeriodSeconds),
                entry.IsPaused)
            {
                WorkDayDate = entry.WorkDayDate,
                IsWorkDayOpen = entry.IsWorkDayOpen
            });
        }

        return records;
    }

    private static ActionLogExportEntry ToExportEntry(ActionLogRecord record) => new()
    {
        RegisteredAt = record.RegisteredAt,
        Action = record.Action,
        CurrentMode = record.CurrentMode,
        IsPaused = record.IsPaused,
        TotalWorkSeconds = ToSeconds(record.TotalWork),
        TotalBreakSeconds = ToSeconds(record.TotalBreak),
        CurrentPeriodSeconds = ToSeconds(record.CurrentPeriod),
        WorkDayDate = record.WorkDayDate,
        IsWorkDayOpen = record.IsWorkDayOpen
    };

    private static void Validate(ActionLogExportEntry? entry)
    {
        if (entry is null)
            throw new InvalidDataException("Список журнала содержит пустую запись.");
        if (entry.RegisteredAt == default)
            throw new InvalidDataException("Запись журнала содержит некорректную дату.");
        if (string.IsNullOrWhiteSpace(entry.Action) || entry.Action.Length > MaximumActionLength)
            throw new InvalidDataException("Запись журнала содержит некорректное действие.");
        if (entry.CurrentMode.HasValue && !Enum.IsDefined(entry.CurrentMode.Value))
            throw new InvalidDataException("Запись журнала содержит неизвестный текущий режим.");
        if (entry.IsPaused && !entry.CurrentMode.HasValue)
            throw new InvalidDataException("Для режима паузы не указана исходная категория.");

        ValidateDuration(entry.TotalWorkSeconds, "Работа сегодня");
        ValidateDuration(entry.TotalBreakSeconds, "Перерыв сегодня");
        ValidateDuration(entry.CurrentPeriodSeconds, "Текущий период");
    }

    private static void ValidateDuration(long seconds, string fieldName)
    {
        if (seconds < 0 || seconds > MaximumDurationSeconds)
            throw new InvalidDataException($"Поле «{fieldName}» содержит некорректное время.");
    }

    private static long ToSeconds(TimeSpan value) => checked((long)value.TotalSeconds);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }

    private sealed class ActionLogExportDocument
    {
        public string? Format { get; init; }
        public int Version { get; init; }
        public DateTimeOffset ExportedAt { get; init; }
        public List<ActionLogExportEntry>? Entries { get; init; }
    }

    private sealed class ActionLogExportEntry
    {
        public DateTimeOffset RegisteredAt { get; init; }
        public string? Action { get; init; }
        public TimeEntryKind? CurrentMode { get; init; }
        public bool IsPaused { get; init; }
        public long TotalWorkSeconds { get; init; }
        public long TotalBreakSeconds { get; init; }
        public long CurrentPeriodSeconds { get; init; }
        public DateOnly? WorkDayDate { get; init; }
        public bool? IsWorkDayOpen { get; init; }
    }
}
