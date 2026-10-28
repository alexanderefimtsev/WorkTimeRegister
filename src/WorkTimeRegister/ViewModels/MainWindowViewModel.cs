using Avalonia.Threading;
using WorkTimeRegister.Data;
using WorkTimeRegister.Models;
using WorkTimeRegister.Services;

namespace WorkTimeRegister.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ITimeEntryRepository repository;
    private readonly TimeProvider timeProvider;
    private readonly WorkDayService workDayService;
    private readonly DispatcherTimer timer;
    private readonly SemaphoreSlim stateLock = new(1, 1);
    private TimeSummary currentSummary = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null);
    private WorkDay? workDay;
    private WorkDaySettings settings = new();
    private DateOnly currentDate;
    private bool isInitialized;
    private bool isExiting;
    private bool disposed;
    private string? errorMessage;
    private RecoveryInfo? pendingRecovery;

    public MainWindowViewModel(ITimeEntryRepository repository, TimeSummaryCalculator calculator, TimeProvider timeProvider)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
        workDayService = new WorkDayService(repository, timeProvider);
        currentDate = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        StartWorkCommand = new AsyncCommand(() => RunActionAsync(WorkDayOperation.StartWork),
            () => CanChangeMode && ActiveKind != TimeEntryKind.Work);
        StartBreakCommand = new AsyncCommand(() => RunActionAsync(WorkDayOperation.StartBreak),
            () => CanChangeMode && ActiveKind == TimeEntryKind.Work);
        EndBreakCommand = new AsyncCommand(() => RunActionAsync(WorkDayOperation.StartWork),
            () => CanChangeMode && ActiveKind == TimeEntryKind.Break);
        ModeSwitchCommand = new AsyncCommand(() => RunActionAsync(WorkDayOperation.ToggleMode), () => CanChangeMode);
        ToggleBreakCommand = new AsyncCommand(() => RunActionAsync(WorkDayOperation.ToggleMode), () => CanChangeMode);
        TogglePauseCommand = new AsyncCommand(TogglePauseAsync, () => IsReady && IsWorkDayOpen);
        EndWorkCommand = new AsyncCommand(() => RunActionAsync(WorkDayOperation.EndDay), () => IsReady && IsWorkDayOpen);
        ToggleWorkDayCommand = new AsyncCommand(ToggleWorkDayAsync, () => IsReady);
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, async (_, _) => await RefreshAsync());
        Initialization = InitializeCoreAsync();
    }

    public Task Initialization { get; }
    public string WorkTime => Format(TotalWork);
    public string BreakTime => Format(TotalBreak);
    public string CurrentPeriod => Format(CurrentPeriodValue);
    public string CurrentPeriodTitle => pendingRecovery is not null ? "Требуется восстановление" : IsPaused ? "На паузе…" : ActiveKind switch
    {
        TimeEntryKind.Work => "Работаю…",
        TimeEntryKind.Break => "На перерыве…",
        _ => workDay?.StartedAt is null ? "Рабочий день не начат" : "Рабочий день завершён"
    };
    public string Status => pendingRecovery is not null ? "Выберите способ учёта времени после сбоя" : IsPaused ? $"На паузе: {ModeName(ActiveKind!.Value)}" : ActiveKind switch
    {
        TimeEntryKind.Work => "Сейчас идёт работа",
        TimeEntryKind.Break => "Сейчас идёт перерыв",
        _ => CurrentPeriodTitle
    };
    public string WorkDayStatus => $"{currentDate:dd.MM.yyyy} · " + (IsWorkDayOpen
        ? $"Рабочий день открыт с {LocalTime(workDay!.StartedAt!.Value):HH:mm}"
        : workDay?.StartedAt is null ? "Рабочий день не начат" : "Рабочий день завершён");
    public string WorkDayActionText => IsWorkDayOpen ? "Закончить рабочий день" : "Начать рабочий день";
    public string BreakActionText => ActiveKind == TimeEntryKind.Break ? "Закончить перерыв" : "Начать перерыв";
    public string PauseActionText => IsPaused ? "Продолжить" : "Пауза";
    public string ModeSwitchActionText => ActiveKind == TimeEntryKind.Work ? "Начать перерыв" : "Продолжить работу";
    public string? ErrorMessage { get => errorMessage; private set { errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public WorkDaySettings Settings => settings;
    public bool IsReady => isInitialized && !isExiting && !disposed && pendingRecovery is null;
    public bool HasLoaded => isInitialized;
    public RecoveryInfo? PendingRecovery => pendingRecovery;
    public bool IsWorkDayOpen => workDay?.IsOpen == true;
    private bool CanChangeMode => IsReady && IsWorkDayOpen && !IsPaused;
    public AsyncCommand StartWorkCommand { get; }
    public AsyncCommand StartBreakCommand { get; }
    public AsyncCommand EndBreakCommand { get; }
    public AsyncCommand ModeSwitchCommand { get; }
    public AsyncCommand ToggleBreakCommand { get; }
    public AsyncCommand TogglePauseCommand { get; }
    public AsyncCommand EndWorkCommand { get; }
    public AsyncCommand ToggleWorkDayCommand { get; }
    public TimeSpan TotalWork => currentSummary.TotalWork;
    public TimeSpan TotalBreak => currentSummary.TotalBreak;
    public TimeSpan CurrentPeriodValue => currentSummary.CurrentPeriod;
    public TimeEntryKind? ActiveKind => currentSummary.ActiveKind;
    public bool IsPaused => currentSummary.IsPaused;
    public bool IsNotPaused => !IsPaused;
    public bool CanAdjustCurrentPeriod => IsReady && IsWorkDayOpen && ActiveKind.HasValue;
    public bool CanReduceWork => IsReady && TotalWork > TimeSpan.Zero;
    public bool CanReduceBreak => IsReady && TotalBreak > TimeSpan.Zero;
    public bool CanReduceCurrentPeriod => CanAdjustCurrentPeriod && CurrentPeriodValue > TimeSpan.Zero;

    public LogWindowViewModel CreateLogViewModel() => new(repository, timeProvider);

    public Task<string?> TransferAsync(TimeEntryKind source, TimeSpan amount) =>
        AdjustTimeAsync(TimeAdjustmentOperation.Transfer, source, amount);

    public Task<string?> AdjustTimeAsync(TimeAdjustmentOperation operation, TimeEntryKind kind, TimeSpan amount) =>
        ApplyAdjustmentAsync(WorkDayOperation.AdjustTime, operation, kind, amount);

    public Task<string?> AdjustCurrentPeriodAsync(TimeAdjustmentOperation operation, TimeSpan amount) =>
        ApplyAdjustmentAsync(WorkDayOperation.AdjustCurrentPeriod, operation, ActiveKind ?? TimeEntryKind.Work, amount);

    public Task TogglePauseAsync() => RunActionAsync(WorkDayOperation.TogglePause);

    public Task ToggleWorkDayAsync() => RunActionAsync(IsWorkDayOpen ? WorkDayOperation.EndDay : WorkDayOperation.StartDay);

    public async Task<bool> SaveSettingsAsync(WorkDaySettings value) =>
        await RunActionAsync(WorkDayOperation.SaveSettings, value);

    public async Task<bool> ResolveRecoveryAsync(RecoveryChoice choice, TimeSpan amount = default)
    {
        await Initialization;
        await stateLock.WaitAsync();
        try
        {
            if (pendingRecovery is null || isExiting || disposed)
                return false;
            Apply(await workDayService.ExecuteAsync(WorkDayOperation.ResolveRecovery, amount: amount, recovery: choice));
            ErrorMessage = null;
            timer.Start();
            return true;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Не удалось восстановить учёт: {exception.Message}";
            return false;
        }
        finally { stateLock.Release(); }
    }

    public async Task HandlePowerEventAsync(bool sleeping, DateTimeOffset at)
    {
        await Initialization;
        await stateLock.WaitAsync();
        try
        {
            if (!IsReady)
                return;
            Apply(await workDayService.ExecuteAsync(sleeping ? WorkDayOperation.Suspend : WorkDayOperation.Resume, at: at));
        }
        catch (Exception exception) { ErrorMessage = $"Не удалось обработать смену режима питания: {exception.Message}"; }
        finally { stateLock.Release(); }
    }

    public async Task RefreshAsync()
    {
        await Initialization;
        if (!IsReady)
            return;
        await RunActionAsync(WorkDayOperation.Refresh, clearError: false);
    }

    public async Task<bool> PrepareForExitAsync()
    {
        await Initialization;
        await stateLock.WaitAsync();
        try
        {
            if (isExiting)
                return true;
            if (!isInitialized)
                throw new InvalidOperationException("Данные не загружены. Завершение рабочего дня не подтверждено.");
            if (pendingRecovery is not null)
                throw new InvalidOperationException("Сначала завершите восстановление после сбоя.");
            isExiting = true;
            timer.Stop();
            RefreshCommands();
            Apply(await workDayService.ExecuteAsync(WorkDayOperation.Exit));
            return true;
        }
        catch (Exception exception)
        {
            isExiting = false;
            ErrorMessage = $"Не удалось завершить рабочий день при выходе: {exception.Message}";
            if (pendingRecovery is null)
                timer.Start();
            RefreshCommands();
            return false;
        }
        finally { stateLock.Release(); }
    }

    private async Task InitializeCoreAsync()
    {
        await stateLock.WaitAsync();
        try
        {
            var snapshot = await workDayService.ExecuteAsync(WorkDayOperation.Initialize);
            isInitialized = true;
            Apply(snapshot);
            if (pendingRecovery is null)
                timer.Start();
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Не удалось загрузить рабочий день: {exception.Message}";
        }
        finally { stateLock.Release(); }
    }

    private async Task<bool> RunActionAsync(WorkDayOperation operation, WorkDaySettings? value = null, bool clearError = true)
    {
        await Initialization;
        await stateLock.WaitAsync();
        try
        {
            if (!IsReady)
                return false;
            Apply(await workDayService.ExecuteAsync(operation, value));
            if (clearError)
                ErrorMessage = null;
            return true;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Не удалось обновить рабочий день: {exception.Message}";
            return false;
        }
        finally { stateLock.Release(); }
    }

    private async Task<string?> ApplyAdjustmentAsync(WorkDayOperation operation,
        TimeAdjustmentOperation adjustment, TimeEntryKind kind, TimeSpan amount)
    {
        await Initialization;
        await stateLock.WaitAsync();
        try
        {
            if (!IsReady)
                throw new InvalidOperationException("Приложение ещё загружается или завершает работу.");
            Apply(await workDayService.ExecuteAsync(operation, adjustment: adjustment, category: kind, amount: amount));
            ErrorMessage = null;
            return null;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Не удалось изменить время: {exception.Message}";
            return exception.Message;
        }
        finally { stateLock.Release(); }
    }

    private void Apply(WorkDaySnapshot snapshot)
    {
        workDay = snapshot.Day;
        currentDate = snapshot.Date;
        currentSummary = snapshot.Summary;
        settings = snapshot.Settings;
        pendingRecovery = snapshot.Recovery;
        foreach (var property in new[]
                 {
                     nameof(WorkTime), nameof(BreakTime), nameof(CurrentPeriod), nameof(CurrentPeriodTitle), nameof(Status),
                     nameof(WorkDayStatus), nameof(WorkDayActionText), nameof(BreakActionText), nameof(PauseActionText),
                     nameof(ModeSwitchActionText), nameof(TotalWork), nameof(TotalBreak), nameof(CurrentPeriodValue),
                     nameof(ActiveKind), nameof(IsPaused), nameof(IsNotPaused), nameof(IsWorkDayOpen), nameof(Settings),
                     nameof(IsReady), nameof(CanAdjustCurrentPeriod), nameof(CanReduceWork), nameof(CanReduceBreak),
                     nameof(CanReduceCurrentPeriod), nameof(PendingRecovery), nameof(HasLoaded)
                 })
            OnPropertyChanged(property);
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(IsReady));
        foreach (var command in new[]
                 {
                     StartWorkCommand, StartBreakCommand, EndBreakCommand, ModeSwitchCommand, ToggleBreakCommand,
                     TogglePauseCommand, EndWorkCommand, ToggleWorkDayCommand
                 })
            command.RaiseCanExecuteChanged();
    }

    private DateTimeOffset LocalTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, timeProvider.LocalTimeZone);
    private static string Format(TimeSpan value) => $"{(long)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
    private static string ModeName(TimeEntryKind kind) => kind == TimeEntryKind.Work ? "Работа" : "Перерыв";

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
    }
}
