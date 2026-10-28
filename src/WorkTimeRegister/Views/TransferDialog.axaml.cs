using Avalonia.Controls;
using Avalonia.Interactivity;
using WorkTimeRegister.Models;

namespace WorkTimeRegister.Views;

public sealed record TransferDialogResult(
    TimeEntryKind Source,
    TimeSpan Amount,
    TimeAdjustmentOperation Operation = TimeAdjustmentOperation.Transfer);

public sealed partial class TransferDialog : Window
{
    private readonly TimeSpan workTime;
    private readonly TimeSpan breakTime;
    private readonly bool selectionLocked;
    private readonly TimeSpan? availableOverride;
    private readonly string? lockedTargetLabel;

    public TransferDialog() : this(TimeSpan.Zero, TimeSpan.Zero)
    {
    }

    public TransferDialog(TimeSpan workTime, TimeSpan breakTime)
    {
        this.workTime = workTime;
        this.breakTime = breakTime;
        InitializeComponent();
        UpdateControls();
    }

    public TransferDialog(
        TimeSpan workTime,
        TimeSpan breakTime,
        TimeAdjustmentOperation operation,
        TimeEntryKind kind,
        TimeSpan availableTime,
        string targetLabel)
    {
        this.workTime = workTime;
        this.breakTime = breakTime;
        selectionLocked = true;
        availableOverride = availableTime;
        lockedTargetLabel = targetLabel;
        InitializeComponent();
        SelectOperation(operation);
        SelectKind(kind);
        Height = operation == TimeAdjustmentOperation.Clear ? 350 : 410;
        MinHeight = Height;
        UpdateControls();
    }

    private void OperationChanged(object? sender, RoutedEventArgs e) => UpdateControls();
    private void DirectionChanged(object? sender, RoutedEventArgs e) => UpdateControls();
    private void CategoryChanged(object? sender, RoutedEventArgs e) => UpdateControls();

    private void UpdateControls()
    {
        if (AvailableTimeText is null ||
            WorkToBreakButton is null ||
            TransferOperationButton is null ||
            AddOperationButton is null ||
            RemoveOperationButton is null ||
            ClearOperationButton is null ||
            WorkCategoryButton is null ||
            TransferDirectionPanel is null ||
            CategoryPanel is null ||
            AmountPanel is null ||
            DialogTitleText is null ||
            OperationPanel is null ||
            LockedTargetText is null ||
            ApplyButton is null ||
            ErrorText is null)
            return;

        var operation = GetOperation();
        var kind = GetSelectedKind(operation);
        var available = availableOverride ?? (kind == TimeEntryKind.Work ? workTime : breakTime);
        var isTransfer = operation == TimeAdjustmentOperation.Transfer;
        var isClear = operation == TimeAdjustmentOperation.Clear;

        OperationPanel.IsVisible = !selectionLocked;
        TransferDirectionPanel.IsVisible = !selectionLocked && isTransfer;
        CategoryPanel.IsVisible = !selectionLocked && !isTransfer;
        LockedTargetText.IsVisible = selectionLocked;
        LockedTargetText.Text = selectionLocked ? lockedTargetLabel : string.Empty;
        AmountPanel.IsVisible = !isClear;
        DialogTitleText.Text = operation switch
        {
            TimeAdjustmentOperation.Transfer => "Перенос времени",
            TimeAdjustmentOperation.Add => "Добавление времени",
            TimeAdjustmentOperation.Remove => "Уменьшение времени",
            TimeAdjustmentOperation.Clear => "Очистка времени",
            _ => "Изменение времени"
        };
        ApplyButton.Content = operation switch
        {
            TimeAdjustmentOperation.Transfer => "Перенести",
            TimeAdjustmentOperation.Add => "Добавить",
            TimeAdjustmentOperation.Remove => "Убрать",
            TimeAdjustmentOperation.Clear => "Очистить",
            _ => "Применить"
        };
        AvailableTimeText.Text = operation switch
        {
            TimeAdjustmentOperation.Add when selectionLocked => $"Текущее значение: {Format(available)}",
            TimeAdjustmentOperation.Add => $"Сейчас в категории: {Format(available)}",
            TimeAdjustmentOperation.Clear => $"Будет очищено: {Format(available)}",
            TimeAdjustmentOperation.Remove when selectionLocked => $"Доступно для вычитания: {Format(available)}",
            _ => $"Доступно: {Format(available)}"
        };
        ErrorText.Text = string.Empty;
    }

    private void TransferClicked(object? sender, RoutedEventArgs e)
    {
        var operation = GetOperation();
        var source = GetSelectedKind(operation);
        var amount = TimeSpan.Zero;

        if (operation != TimeAdjustmentOperation.Clear &&
            (!TryParseDuration(AmountTextBox.Text, out amount) || amount <= TimeSpan.Zero))
        {
            ErrorText.Text = "Введите положительное время в формате ЧЧ:ММ:СС.";
            return;
        }

        var available = availableOverride ?? (source == TimeEntryKind.Work ? workTime : breakTime);
        if ((operation is TimeAdjustmentOperation.Transfer or TimeAdjustmentOperation.Remove) && amount > available)
        {
            ErrorText.Text = "Количество времени превышает доступное время.";
            return;
        }

        Close(new TransferDialogResult(source, amount, operation));
    }

    private void CancelClicked(object? sender, RoutedEventArgs e) => Close((TransferDialogResult?)null);

    private static bool TryParseDuration(string? value, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var parts = value?.Split(':', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 3 } ||
            !int.TryParse(parts[0], out var hours) ||
            !int.TryParse(parts[1], out var minutes) ||
            !int.TryParse(parts[2], out var seconds) ||
            hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or > 59)
            return false;

        try
        {
            result = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private TimeAdjustmentOperation GetOperation()
    {
        if (AddOperationButton.IsChecked == true)
            return TimeAdjustmentOperation.Add;
        if (RemoveOperationButton.IsChecked == true)
            return TimeAdjustmentOperation.Remove;
        if (ClearOperationButton.IsChecked == true)
            return TimeAdjustmentOperation.Clear;
        return TimeAdjustmentOperation.Transfer;
    }

    private TimeEntryKind GetSelectedKind(TimeAdjustmentOperation operation)
    {
        if (operation == TimeAdjustmentOperation.Transfer)
            return WorkToBreakButton.IsChecked == true ? TimeEntryKind.Work : TimeEntryKind.Break;
        return WorkCategoryButton.IsChecked == true ? TimeEntryKind.Work : TimeEntryKind.Break;
    }

    private void SelectOperation(TimeAdjustmentOperation operation)
    {
        TransferOperationButton.IsChecked = operation == TimeAdjustmentOperation.Transfer;
        AddOperationButton.IsChecked = operation == TimeAdjustmentOperation.Add;
        RemoveOperationButton.IsChecked = operation == TimeAdjustmentOperation.Remove;
        ClearOperationButton.IsChecked = operation == TimeAdjustmentOperation.Clear;
    }

    private void SelectKind(TimeEntryKind kind)
    {
        WorkToBreakButton.IsChecked = kind == TimeEntryKind.Work;
        BreakToWorkButton.IsChecked = kind == TimeEntryKind.Break;
        WorkCategoryButton.IsChecked = kind == TimeEntryKind.Work;
        BreakCategoryButton.IsChecked = kind == TimeEntryKind.Break;
    }

    private static string Format(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
