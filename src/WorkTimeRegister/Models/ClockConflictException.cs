namespace WorkTimeRegister.Models;

public sealed class ClockConflictException(DateTimeOffset currentAt, DateTimeOffset savedAt) : InvalidOperationException(
    "Время компьютера раньше последней сохранённой активности. Проверьте дату и время.")
{
    public DateTimeOffset CurrentAt { get; } = currentAt;
    public DateTimeOffset SavedAt { get; } = savedAt;
}
