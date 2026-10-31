---
name: cs-tests
description: "Правила тестов WorkTimeRegister на xUnit v3: команды, настоящая SQLite во временной папке, время через TimeProvider, проверка ViewModel и меню Dock вместо открытия окон, ручной запуск без записи в базу пользователя. Вызывается пользователем: $cs-tests."
---

# Тесты WorkTimeRegister

Канон проекта: `AGENTS.md`. Код и слои описаны в `cs-guide`, SQL и миграции в `sqlite`.

## Команды

Из корня репозитория.

| Что | Команда |
| --- | --- |
| Весь набор | `dotnet test WorkTimeRegister.slnx -m:1 -nodeReuse:false` |
| Один класс | `dotnet test WorkTimeRegister.slnx --filter "FullyQualifiedName~WorkDayServiceTests"` |

## Без ожидания по часам

- В тестах нет `Thread.Sleep` и новых `Task.Delay` для ожидания результата. Время сдвигается часами теста (`Advance`, `Clock.Current = ...`), а не ожиданием.
- Метод, который возвращает `Task` (`ToggleWorkDayAsync`, `TogglePauseAsync`, `WorkDayService.ExecuteAsync`), тест ждёт через `await`.
- Существующий `WaitForAsync` (опрос каждые 10 мс, не дольше 2 с) нужен только после `ICommand.Execute`: `AsyncCommand.Execute` это `async void`, и его `Task` тесту недоступен. В остальных случаях `WaitForAsync` не нужен.

## Что настоящее, что подменяется

- База настоящая: SQLite в своей временной папке на каждый тест, `Path.Combine(Path.GetTempPath(), $"WorkTimeRegisterTests-{Guid.NewGuid():N}")`, затем `repository.InitializeAsync()`. Папка удаляется в `finally` или в `DisposeAsync` контекста, как `RecoveryAndSleepTests.Context`.
- `SqliteTimeEntryRepository`, `WorkDayService`, `TimeSummaryCalculator`, `TimeTransferPlanner` и ViewModel не подменяются. Moq и ручных реализаций `ITimeEntryRepository` в тестах нет, новых не появляется.
- Подменяется только то, что приходит извне процесса:
  - часы: подкласс `TimeProvider` с фиксированным часовым поясом (`MutableTimeProvider` в `MainWindowViewModelTests`, `Clock` в `RecoveryAndSleepTests`);
  - события сна: вызов `HandlePowerEventAsync` с нужным моментом;
  - путь к базе и временный каталог.
- Новый тест кладётся в существующий класс по теме и берёт его часы и контекст. Новый файл тестов с ещё одной копией часов появляется, только если темы нет. Общий помощник для часов вместо копий в нескольких файлах: правка чужих тестов, только по согласованию.
- `FakeTimeProvider` из `Microsoft.Extensions.TimeProvider.Testing` и Avalonia.Headless: новые зависимости, только с разрешения.

## Даты в тестах

- Момент задаётся со смещением: `new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(3))`.
- Граница дня строится явно: `new DateTimeOffset(now.Date, now.Offset)` или `WorkDayService.Boundary(date, clock.LocalTimeZone)`. `now.Date` без смещения там, где ждут `DateTimeOffset`, получает смещение машины.
- Часовой пояс часов теста задаётся через `TimeZoneInfo.CreateCustomTimeZone`, не берётся из машины.

## Как проверять

- Через публичный метод того слоя, который меняли: расчёт через `TimeSummaryCalculator.Calculate`, правило дня через `WorkDayService.ExecuteAsync`, данные через методы `SqliteTimeEntryRepository`, доступность кнопок через `CanExecute` команд ViewModel.
- После записи значение читается обратно отдельным вызовом: следующая операция `Refresh` или `Initialize`, новый `WorkDayService` после `Exit`, или `Get*Async` с окном дня. Ответ самой операции записи сохранение не доказывает.
- Прямой SQL в тесте (`Scalar`, `CountAsync`) только для того, что публичный API не показывает: число строк, `PRAGMA user_version`, состояние `ApplicationState`.
- Миграция проверяется на базе старого вида, созданной в самом тесте, как `ReliabilityMigrationTests` и `WorkDayMigrationTests`: данные после `InitializeAsync`, `user_version` и одна резервная копия рядом с базой.
- Ожидаемое значение пишется числом в тесте (`TimeSpan.FromMinutes(70)`), не вычисляется тем же кодом, который проверяется.

## Что покрыть при правке учёта

Из `AGENTS.md`: активный рабочий период, активный перерыв, пауза, завершённые периоды, итог дня. Кроме них, где правка их касается:

- переход через полночь;
- выход и повторный запуск (`Exit`, затем `Initialize` с новым `WorkDayService`);
- аварийное завершение и выбор восстановления (`RecoveryChoice`);
- сон и пробуждение, их повтор и пропуск;
- перевод часов назад (`ClockConflictException`);
- пустой день и нулевые длительности.

В каждом случае проверяются итоги работы и перерыва, отсутствие отрицательных периодов и двойного учёта.

## Интерфейс

Тестов, которые открывают окна Avalonia, в проекте нет. Интерфейс проверяется так:

- состояние и тексты кнопок: свойства ViewModel (`WorkDayActionText`, `ModeSwitchActionText`) и `CanExecute` команд;
- меню Dock: `DockMenuCoordinator`, свойства `Header` и `IsEnabled` пунктов;
- ручной запуск. `dotnet run --project src/WorkTimeRegister/WorkTimeRegister.csproj` открывает настоящую базу пользователя (`~/Library/Application Support/WorkTimeRegister/worktime.db` на macOS, `%LOCALAPPDATA%\WorkTimeRegister\worktime.db` на Windows) и пишет в неё. Без разрешения пользователя запуск идёт только из копии репозитория во временной папке, где путь к базе ведёт во временный файл, как в сессии 8.

## Тест на найденную ошибку

- Сначала тест, который падает на текущем коде, потом правка, потом тот же тест проходит. В отчёте оба прогона.
- Упавший тест не ослабляется. Сначала причина по выводу теста и коду, потом решение: ошибка в коде или в ожидании теста.

## Перед сдачей

- Весь набор проходит.
- В отчёте точные команды и строка итога `Passed!` или `Failed!` с числами (`AGENTS.md`).
- Временные папки тестов удалены, файлы `.db` и `.bak` в репозиторий не попадают (`.gitignore`).
