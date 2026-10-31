---
name: cs-guide
description: "WorkTimeRegister code map and current patterns: layers, where code goes, time handling, async and UI thread, errors, local files. Invoke with $cs-guide."
---

# cs-guide

Rules are in `AGENTS.md`; it requires code consistent with the existing codebase. This file maps that codebase. SQL: `sqlite`. Tests: `cs-tests`.

## Stack

net10.0 (C# 14), Avalonia 11.3.20 with Fluent and compiled bindings, Microsoft.Data.Sqlite 10.0.1, xUnit v3 3.2.0. No ORM, DI container, MVVM library or mocking library.

## Flow

`Program.Main` (single instance, `--shutdown`) -> `App` (composition root, windows, Dock, sleep) -> `MainWindowViewModel(repository, TimeSummaryCalculator, TimeProvider)` -> `WorkDayService.ExecuteAsync(WorkDayOperation, ...)` -> `ITimeEntryRepository.ProcessWorkDayAsync(WorkDayRequest)` -> `SqliteTimeEntryRepository` (partials `.WorkDays`, `.Runtime`, `.Power`, `.WorkDayMigration`).

## Where things live

- `Models/`: records, enums, domain exceptions (`WorkDay`, `WorkDayRequest`, `WorkDayOperation`, `TimeEntry`, `ClockConflictException`).
- `Data/`: all SQL. Workday rules are in `ProcessWorkDayAsync`, one branch per `WorkDayOperation`, in one transaction; actions are logged via `WriteDayLogAsync`.
- `Services/`: calculators without DB or clock (`TimeSummaryCalculator`, `TimeTransferPlanner`), `WorkDayService`, platform services (Dock, sleep, single instance), files (`AtomicFileService`, `ActionLogJsonService`).
- `ViewModels/`: `ViewModelBase`, `AsyncCommand`, window view models. No SQL.
- `Views/`: windows with a view model are XAML with `x:DataType` (`MainWindow`, `ActionLogWindow`); result dialogs are XAML with named controls and `ShowDialog<T>` (`TransferDialog`, `SettingsDialog`); `RecoveryDialog`, `ExitErrorDialog`, `StartupErrorWindow` are built in C#. Code-behind opens dialogs and passes results to view-model methods (`MainWindow.OpenTransferDialog`).

## Adding a workday operation (existing path)

1. Value in `WorkDayOperation`; fields in `WorkDayRequest` if needed.
2. Branch in `ProcessWorkDayAsync`.
3. View model calls `workDayService.ExecuteAsync(...)` under `stateLock`.
4. `AsyncCommand` with `canExecute` in the `MainWindowViewModel` constructor; button text is a view-model property; `RaiseCanExecuteChanged` after state changes.
5. Button in `MainWindow.axaml`; Dock item in `DockMenuCoordinator` if needed.

## Time

- Now comes from the injected `TimeProvider` (`GetUtcNow()`, `LocalTimeZone`); tests replace it. Current exceptions: `App.ConnectOptionalServices` (power event stamp), `SqliteTimeEntryRepository.StartAsync` (`TimeZoneInfo.Local`).
- Instants are `DateTimeOffset`, durations `TimeSpan`.
- Day window: `WorkDayService.Boundary(date)` to `Boundary(date.AddDays(1))`, end exclusive; it skips invalid local time.
- Day date (`DateOnly`) uses the day's zone (`WorkDays.TimeZoneId`).
- Saved time later than now throws `ClockConflictException` in `ProcessWorkDayAsync`; the UI blocks tracking until the clock is fixed.
- Sleep and wake events may arrive late, twice or never; `ProcessWorkDayAsync` and `.Power` handle stale events.
- User-facing times: `HH:mm`, `dd.MM.yyyy HH:mm:ss`.

## Async and UI thread

- `async void` entry points: `AsyncCommand.Execute` and Avalonia event handlers.
- Platform callbacks go through `Dispatcher.UIThread.Post` or `InvokeAsync` (`App.WireInstance`, `App.ConnectOptionalServices`).
- `MainWindowViewModel` changes state under `stateLock`.
- Microsoft.Data.Sqlite async calls complete synchronously on the UI thread (ANALYSIS.md session 9: 1000 of 1000 timer refreshes).
- Disposal: `App.DisposeDesktopServices`, `MainWindowViewModel.Dispose`.

## Errors

- The user sees `ErrorMessage` or `IntegrationWarning` on the view model; startup failures open `StartupErrorWindow`.
- Rule violations throw `InvalidOperationException` with a Russian user message (`StartPauseAsync`).
- `catch (Exception)` appears at UI and optional-integration boundaries in `App`.

## Local files

- DB: `~/Library/Application Support/WorkTimeRegister/worktime.db` (macOS), `%LOCALAPPDATA%\WorkTimeRegister\worktime.db` (Windows). `dotnet run` uses it.
- The instance channel is current-user only (`PipeOptions.CurrentUserOnly`, owner-only folder in `UnixInstanceSocket`).
- Imports are limited by size and count and checked for format and version (`ActionLogJsonService`, `SizeLimitedReadStream`).
- Files that must not be half-written go through `AtomicFileService`.

## Checks (AGENTS.md: run and report)

- `dotnet build WorkTimeRegister.slnx`: 0 warnings on 2026-10-31.
- `dotnet test WorkTimeRegister.slnx`: 138 of 138 on 2026-10-31.
