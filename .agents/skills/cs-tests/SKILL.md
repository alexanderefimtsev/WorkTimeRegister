---
name: cs-tests
description: "WorkTimeRegister test commands and current test patterns: xUnit v3, real SQLite in a temp folder, TimeProvider subclass, view-model and Dock menu checks. Invoke with $cs-tests."
---

# cs-tests

## Commands (repo root)

- All: `dotnet test WorkTimeRegister.slnx -m:1 -nodeReuse:false`
- One class: `dotnet test WorkTimeRegister.slnx --filter "FullyQualifiedName~WorkDayServiceTests"`

## Current patterns

- Real SQLite per test: `Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}")`, then `repository.InitializeAsync()`; the folder is deleted in `finally` or `DisposeAsync` (`RecoveryAndSleepTests.Context`).
- No mocks: repository, `WorkDayService`, calculators and view models are real; the test project has no mocking library.
- Clock: `TimeProvider` subclass with a fixed custom zone (`MutableTimeProvider` in `MainWindowViewModelTests`, `Clock` in `RecoveryAndSleepTests`); time moves via `Advance` or `Clock.Current`.
- Instants carry an offset: `new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(3))`. Day bounds are explicit: `new DateTimeOffset(now.Date, now.Offset)` or `WorkDayService.Boundary(date, clock.LocalTimeZone)`. Implicit `DateTime` to `DateTimeOffset` made tests depend on the machine zone (ANALYSIS.md sessions 8, 12).
- Task-returning methods are awaited. `WaitForAsync` (10 ms poll, 2 s cap) exists for `ICommand.Execute`, which is `async void`.
- Sleep events: call `HandlePowerEventAsync` with the instant.
- Direct SQL in tests (`Scalar`, `CountAsync`) reads row counts, `PRAGMA user_version`, `ApplicationState`.
- Migration tests create an old-schema DB in the test, then call `InitializeAsync` (`ReliabilityMigrationTests`, `WorkDayMigrationTests`).
- UI: view-model properties and command `CanExecute`; Dock items via `DockMenuCoordinator` (`Header`, `IsEnabled`). No test opens an Avalonia window.

## Cases required by AGENTS.md

Active work period, active break, completed periods, day totals.

## Manual run

`dotnet run --project src/WorkTimeRegister/WorkTimeRegister.csproj` reads and writes the real user DB (paths in `cs-guide`).
