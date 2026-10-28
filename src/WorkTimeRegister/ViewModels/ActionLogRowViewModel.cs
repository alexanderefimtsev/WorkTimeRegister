using WorkTimeRegister.Models;

namespace WorkTimeRegister.ViewModels;

public sealed class ActionLogRowViewModel(ActionLogRecord record)
{
    public string RegisteredAt { get; } = record.RegisteredAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
    public string Action { get; } = record.Action;
    public string CurrentMode { get; } = record.IsPaused
        ? record.CurrentMode switch
        {
            TimeEntryKind.Work => "Пауза (Работа)",
            TimeEntryKind.Break => "Пауза (Перерыв)",
            _ => "Пауза"
        }
        : record.CurrentMode switch
        {
            TimeEntryKind.Work => "Работа",
            TimeEntryKind.Break => "Перерыв",
            _ => "Нет"
        };
    public string TotalWork { get; } = Format(record.TotalWork);
    public string TotalBreak { get; } = Format(record.TotalBreak);
    public string CurrentPeriod { get; } = Format(record.CurrentPeriod);

    private static string Format(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
