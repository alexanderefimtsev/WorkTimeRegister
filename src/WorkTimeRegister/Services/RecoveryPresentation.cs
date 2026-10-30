using System.Globalization;
using WorkTimeRegister.Models;

namespace WorkTimeRegister.Services;

public static class RecoveryPresentation
{
    public static string Duration(TimeSpan value) =>
        (value.Days > 0 ? $"{value.Days} дн. " : "") + $"{value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}";

    public static string Moment(DateTimeOffset value, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(value, zone).ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Title(RecoveryOrigin origin) => origin switch
    {
        RecoveryOrigin.LegacyUpgrade => "Обновление прежней базы",
        RecoveryOrigin.MissedUpdates => "Учёт некоторое время не обновлялся",
        RecoveryOrigin.Unspecified => "Проверка неизвестного интервала",
        _ => "Восстановление учёта после сбоя"
    };

    public static string Explanation(RecoveryOrigin origin) => origin switch
    {
        RecoveryOrigin.LegacyUpgrade => "В прежней версии не сохранялись промежуточные отметки. Время сбоя неизвестно. Этот интервал мог включать вашу работу. При исключении фактически отработанное время тоже может быть исключено.",
        RecoveryOrigin.MissedUpdates => "Программа некоторое время не обновляла учёт. Причина разрыва неизвестна: это мог быть сон или задержка программы. Выберите, как учитывать интервал.",
        RecoveryOrigin.Unspecified => "Причина интервала не была сохранена прежней версией. Он мог включать вашу работу. Выберите, как его учитывать.",
        _ => "Предыдущий запуск не завершил учёт штатно. Выберите, сколько времени учесть после последней сохранённой отметки."
    };
}
