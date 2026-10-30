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
    private ClockConflictException? clockConflict;
    private readonly Queue<(bool Sleeping, DateTimeOffset At, Guid Cycle)> powerEvents = new();
    private Guid powerCycle;
    private bool receivingSleep;
    private string? integrationWarning;

    public MainWindowViewModel(ITimeEntryRepository repository, TimeSummaryCalculator calculator, TimeProvider timeProvider, Guid? session = null)
    {
        this.repository = repository;
        this.timeProvider = timeProvider;
        workDayService = new WorkDayService(repository, timeProvider, session);
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
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, async (_, _) =>
        {
            if (clockConflict is not null)
                OnPropertyChanged(nameof(ClockConflictMessage));
            else
                await RefreshAsync();
        });
        Initialization = InitializeCoreAsync();
    }

    public Task Initialization { get; }
    public string WorkTime => Format(TotalWork);
    public string BreakTime => Format(TotalBreak);
    public string CurrentPeriod => Format(CurrentPeriodValue);
    public string CurrentPeriodTitle => clockConflict is not null ? "Проверьте системные часы" :
        pendingRecovery is not null ? "Требуется восстановление" : IsPaused ? "На паузе…" : ActiveKind switch
    {
        TimeEntryKind.Work => "Работаю…",
        TimeEntryKind.Break => "На перерыве…",
        _ => workDay?.StartedAt is null ? "Рабочий день не начат" : "Рабочий день завершён"
    };
    public string Status => clockConflict is not null ? "Учёт приостановлен до исправления часов" :
        pendingRecovery is not null ? "Выберите способ учёта неизвестного интервала" : IsPaused ? $"На паузе: {ModeName(ActiveKind!.Value)}" : ActiveKind switch
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
    public string? IntegrationWarning
    {
        get => integrationWarning;
        set { integrationWarning = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasIntegrationWarning)); }
    }
    public bool HasIntegrationWarning => !string.IsNullOrWhiteSpace(IntegrationWarning);
    public bool HasPendingPowerEvents => powerEvents.Count > 0;
    public bool HasClockConflict => clockConflict is not null;
    public string ClockConflictMessage => clockConflict is null ? "" :
        $"Сейчас на компьютере {LocalTime(timeProvider.GetUtcNow()):dd.MM.yyyy HH:mm:ss}, " +
        $"последняя сохранённая отметка — {LocalTime(clockConflict.SavedAt):dd.MM.yyyy HH:mm:ss}. " +
        "Итоги показаны на последнюю подтверждённую отметку. Исправьте часы и нажмите «Проверить время».";
    public TimeZoneInfo LocalTimeZone => timeProvider.LocalTimeZone;
    public WorkDaySettings Settings => settings;
    public bool IsReady => isInitialized && !isExiting && !disposed && pendingRecovery is null && clockConflict is null;
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

    public LogWindowViewModel CreateLogViewModel() => new(repository, timeProvider, canImport: () => !HasClockConflict);

    public async Task<bool> RetryClockAsync()
    {
        await Initialization;
        await stateLock.WaitAsync();
        try
        {
            if (clockConflict is null || isExiting || disposed)
                return false;
            var snapshot = await workDayService.ExecuteAsync(WorkDayOperation.Initialize);
            clockConflict = null;
            Apply(snapshot);
            OnPropertyChanged(nameof(HasClockConflict));
            OnPropertyChanged(nameof(ClockConflictMessage));
            ErrorMessage = null;
            if (pendingRecovery is null)
                timer.Start();
            else
                timer.Stop();
            return true;
        }
        catch (ClockConflictException exception)
        {
            clockConflict = exception;
            OnPropertyChanged(nameof(ClockConflictMessage));
            return false;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Не удалось проверить время: {exception.Message}";
            return false;
        }
        finally { stateLock.Release(); }
    }

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
            powerEvents.Clear();
            receivingSleep = false;
            powerCycle = Guid.Empty;
            OnPropertyChanged(nameof(HasPendingPowerEvents));
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
            if (!isInitialized || isExiting || disposed || clockConflict is not null)
                return;
            if (pendingRecovery is not null && pendingRecovery.Origin != RecoveryOrigin.MissedUpdates)
                return;
            if (sleeping && !receivingSleep)
                powerCycle = Guid.NewGuid();
            receivingSleep = sleeping;
            powerEvents.Enqueue((sleeping, at, powerCycle));
            await DrainPowerEventsAsync();
        }
        catch (Exception exception) { ErrorMessage = $"Не удалось обработать смену режима питания: {exception.Message}"; }
        finally { stateLock.Release(); }
    }

    public async Task RetryPowerEventsAsync()
    {
        await Initialization;
        await stateLock.WaitAsync();
        try { if (!disposed && !isExiting) await DrainPowerEventsAsync(); }
        finally { stateLock.Release(); }
    }

    private async Task<bool> DrainPowerEventsAsync()
    {
        while (powerEvents.TryPeek(out var item))
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Apply(await workDayService.ExecuteAsync(item.Sleeping ? WorkDayOperation.Suspend : WorkDayOperation.Resume,
                        at: item.At, powerCycleId: item.Cycle));
                    powerEvents.Dequeue();
                    break;
                }
                catch (Exception exception)
                {
                    ErrorMessage = $"Не удалось сохранить событие сна или пробуждения: {exception.Message}. Событие сохранено в очереди. Нажмите «Повторить»; до обработки итоги не обновляются.";
                    if (attempt == 2)
                    {
                        OnPropertyChanged(nameof(HasPendingPowerEvents));
                        return false;
                    }
                    await Task.Delay(attempt == 0 ? 250 : 750);
                }
            }
        }
        ErrorMessage = null;
        OnPropertyChanged(nameof(HasPendingPowerEvents));
        return true;
    }

    public async Task RefreshAsync()
    {
        await Initialization;
        if (!IsReady || HasPendingPowerEvents)
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
            if (clockConflict is not null)
                throw new InvalidOperationException("Сначала исправьте системное время и повторите проверку.");
            if (pendingRecovery is not null)
                throw new InvalidOperationException("Сначала выберите способ учёта неизвестного интервала.");
            if (!await DrainPowerEventsAsync() || pendingRecovery is not null)
                return false;
            isExiting = true;
            timer.Stop();
            RefreshCommands();
            Apply(await workDayService.ExecuteAsync(WorkDayOperation.Exit));
            if (pendingRecovery is not null)
                throw new InvalidOperationException("Сначала выберите способ учёта неизвестного интервала.");
            return true;
        }
        catch (ClockConflictException exception)
        {
            isExiting = false;
            await EnterClockConflictAsync(exception);
            return false;
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
        catch (ClockConflictException exception)
        {
            await EnterClockConflictAsync(exception);
            isInitialized = true;
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
            if (operation == WorkDayOperation.Refresh && HasPendingPowerEvents)
                return false;
            var desiredPause = !IsPaused;
            if (HasPendingPowerEvents && (!await DrainPowerEventsAsync() || pendingRecovery is not null))
                return false;
            Apply(await workDayService.ExecuteAsync(operation, value,
                paused: operation == WorkDayOperation.TogglePause ? desiredPause : null));
            if (pendingRecovery is null && operation is WorkDayOperation.StartDay or WorkDayOperation.EndDay or
                WorkDayOperation.TogglePause or WorkDayOperation.StartWork or WorkDayOperation.StartBreak or WorkDayOperation.ToggleMode)
            {
                receivingSleep = false;
                powerCycle = Guid.Empty;
            }
            if (clearError)
                ErrorMessage = null;
            return pendingRecovery is null;
        }
        catch (ClockConflictException exception)
        {
            await EnterClockConflictAsync(exception);
            return false;
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
            if (HasPendingPowerEvents && (!await DrainPowerEventsAsync() || pendingRecovery is not null))
                throw new InvalidOperationException("Сначала завершите обработку события питания.");
            Apply(await workDayService.ExecuteAsync(operation, adjustment: adjustment, category: kind, amount: amount));
            if (pendingRecovery is not null)
                throw new InvalidOperationException("Сначала выберите способ учёта неизвестного интервала.");
            ErrorMessage = null;
            return null;
        }
        catch (ClockConflictException exception)
        {
            await EnterClockConflictAsync(exception);
            return exception.Message;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"Не удалось изменить время: {exception.Message}";
            return exception.Message;
        }
        finally { stateLock.Release(); }
    }

    private async Task EnterClockConflictAsync(ClockConflictException exception)
    {
        Apply(await workDayService.ExecuteAsync(WorkDayOperation.Inspect));
        clockConflict = exception;
        ErrorMessage = null;
        OnPropertyChanged(nameof(HasClockConflict));
        OnPropertyChanged(nameof(ClockConflictMessage));
        OnPropertyChanged(nameof(CurrentPeriodTitle));
        OnPropertyChanged(nameof(Status));
        RefreshCommands();
        timer.Start();
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
