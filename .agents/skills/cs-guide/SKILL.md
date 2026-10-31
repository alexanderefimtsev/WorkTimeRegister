---
name: cs-guide
description: "Как устроен код WorkTimeRegister и как в него добавлять: стек и версии, слои, куда класть код, время и часовые пояса, async и поток интерфейса, ошибки, культура, безопасность локальных файлов, правила против лишнего кода. Вызывается пользователем: $cs-guide."
---

# Руководство по коду WorkTimeRegister

Канон проекта: `AGENTS.md`. Этот скилл добавляет к нему правила и ни одно не отменяет; при расхождении выигрывает `AGENTS.md`. SQL и схема описаны в `sqlite`, тесты в `cs-tests`, ревью в `cs-review`, разбор ошибки в `investigate`.

## Стек

| Что | Версия | Где видно |
| --- | --- | --- |
| Платформа | `net10.0`, C# 14 по умолчанию для этой цели | `src/WorkTimeRegister/WorkTimeRegister.csproj` |
| Интерфейс | Avalonia 11.3.20, тема Fluent, compiled bindings по умолчанию | там же |
| База | Microsoft.Data.Sqlite 10.0.1, SQLitePCLRaw.bundle_e_sqlite3 2.1.13 | там же |
| Тесты | xUnit v3 3.2.0 | `tests/WorkTimeRegister.Tests/WorkTimeRegister.Tests.csproj` |

- Возможности языка новее C# 14 не используются. Microsoft не поддерживает версию языка новее той, что привязана к целевой платформе, и не советует `LangVersion` со значением `latest`. `LangVersion` в проекте не задан, так и остаётся.
- Переход на другую версию .NET или Avalonia, смена темы или UI-фреймворка: отдельное решение владельца репозитория, не часть задачи (`AGENTS.md`).
- Новая NuGet-зависимость только с разрешения пользователя. EF Core, Dapper, MVVM-библиотеки, DI-контейнер и Moq в проекте не используются. EF Core рассмотрен и не выбран в сессии 9 `ANALYSIS.md`.

## Слои

```
Program.Main          SingleInstanceService: один экземпляр, --shutdown
  App                 корень сборки объектов, окна, Dock, сон
    MainWindowViewModel(repository, TimeSummaryCalculator, TimeProvider)
      WorkDayService.ExecuteAsync(WorkDayOperation, ...)
        ITimeEntryRepository.ProcessWorkDayAsync(WorkDayRequest)
          SqliteTimeEntryRepository (partial: .WorkDays, .Runtime, .Power, .WorkDayMigration)
Views/*.axaml         привязки к свойствам и AsyncCommand через x:DataType
```

| Папка | Что там живёт |
| --- | --- |
| `Models/` | records, enum и исключения предметной области: `WorkDay`, `WorkDayRequest`, `TimeEntry`, `WorkDayOperation`, `ClockConflictException` |
| `Data/` | `ITimeEntryRepository` и `SqliteTimeEntryRepository`; весь SQL проекта |
| `Services/` | расчёты (`TimeSummaryCalculator`, `TimeTransferPlanner`), `WorkDayService`, службы платформы (Dock, сон, один экземпляр), файлы (`AtomicFileService`, `ActionLogJsonService`) |
| `ViewModels/` | `ViewModelBase` с `INotifyPropertyChanged`, `AsyncCommand`, ViewModel окон |
| `Views/` | окна в XAML с code-behind; `RecoveryDialog`, `ExitErrorDialog` и `StartupErrorWindow` собраны кодом C# |

## Куда класть код

- Расчёт времени идёт в `TimeSummaryCalculator` или `TimeTransferPlanner`. Это классы без базы и без часов: на вход списки периодов и моменты, на выход итог.
- Правила рабочего дня сейчас живут в `ProcessWorkDayAsync` (`SqliteTimeEntryRepository.WorkDays.cs`), находки А1 и К1 в `ANALYSIS.md`. Новая операция дня добавляется веткой этого метода, тело ветки уходит в private метод того же partial-файла. Вынос правил дня из репозитория: отдельная задача, только по согласованию.
- ViewModel не создаёт `SqliteConnection` и не пишет SQL. Code-behind окна не содержит правил учёта: он открывает диалог и передаёт результат в метод ViewModel, как `MainWindow.OpenTransferDialog`.
- Новое окно или диалог пишется в XAML. Окно со своей ViewModel задаёт `x:DataType`, как `ActionLogWindow.axaml`. Диалог, который возвращает результат через `ShowDialog<T>`, читает именованные элементы в code-behind, как `TransferDialog.axaml` и `SettingsDialog.axaml`. Окна, собранные целиком кодом C#, не повторяются (К3).
- Новый класс создаётся, только если ни один существующий не ведёт эту тему. Обёртка из одного-двух методов вокруг существующего класса, новый `Helper` или `Manager` рядом с владельцем темы: признак неверного дизайна.

## Новая операция учёта, по шагам

1. Значение в `WorkDayOperation` (`Models/WorkDay.cs`); новые поля запроса в `WorkDayRequest`, если нужны.
2. Ветка в `ProcessWorkDayAsync` в той же транзакции, что и остальная работа операции; запись в `ActionLog` через `WriteDayLogAsync`.
3. Вызов из ViewModel через `workDayService.ExecuteAsync(...)` под `stateLock`, как остальные действия `MainWindowViewModel`.
4. Команда `AsyncCommand` в конструкторе `MainWindowViewModel` с условием `canExecute`; текст кнопки свойством ViewModel; после смены состояния команды обновляются через `RaiseCanExecuteChanged`.
5. Кнопка в `MainWindow.axaml` с привязкой к команде; пункт меню Dock в `DockMenuCoordinator`, если операция нужна там.
6. Тесты по `cs-tests`: правило дня через `WorkDayService`, доступность команды через ViewModel.

## Время

Приложение считает время, поэтому ошибка здесь видна пользователю как неверный итог дня.

- Текущее время берётся только из `TimeProvider`, переданного в конструктор: `timeProvider.GetUtcNow()`, `timeProvider.LocalTimeZone`. В расчётах и правилах нет `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow` и `TimeZoneInfo.Local`: тест их не подменит. Существующие места с ними (`App.ConnectOptionalServices`, `SqliteTimeEntryRepository.StartAsync`) не копируются.
- Моменты хранятся и передаются как `DateTimeOffset`. `DateTime` без смещения не превращается в `DateTimeOffset` неявно: .NET подставит смещение машины. Из-за `start.Date` на версии 1.6.0 4 теста падали в UTC (сессия 8).
- Граница дня: `WorkDayService.Boundary(date, timeZone)`, она обходит несуществующее местное время при переходе на летнее время. Окно дня: от `Boundary(date)` до `Boundary(date.AddDays(1))`, конец окна не входит.
- Дата дня (`DateOnly`) считается в часовом поясе дня (`WorkDays.TimeZoneId`), не в поясе машины.
- Длительность: `TimeSpan`; в базе целые секунды (`sqlite`).
- Часы компьютера могут уйти назад. Время раньше последней сохранённой отметки даёт `ClockConflictException` в `ProcessWorkDayAsync` (Р7); новая операция с записью проходит через эту проверку.
- События сна и пробуждения приходят от платформы с опозданием, повторно или не приходят (Р1, Р5). Обработчик выдерживает повтор и пропуск без двойного учёта.
- После правки расчёта проверяются: активный рабочий период, активный перерыв, пауза, завершённые периоды, итог дня (`AGENTS.md`), полночь, выход и повторный запуск.

## Асинхронность и поток интерфейса

- `async void` только в обработчиках событий Avalonia и в `AsyncCommand.Execute`. Остальной асинхронный код возвращает `Task`.
- `.Result`, `.Wait()` и `GetAwaiter().GetResult()` только в `Program.Main` до запуска Avalonia.
- Колбэк платформы (сон, Dock, канал второго экземпляра) переходит в поток интерфейса через `Dispatcher.UIThread.Post` или `InvokeAsync`, как в `App.WireInstance`.
- Состояние `MainWindowViewModel` меняется только под `stateLock`.
- Асинхронные методы Microsoft.Data.Sqlite выполняются синхронно: из 1000 обновлений таймера 1000 завершились синхронно в потоке интерфейса (сессия 9). `await` не освобождает окно на время запроса, поэтому запрос по таймеру читает только окно текущего дня.
- Подписки на события снимаются, таймеры останавливаются, `IDisposable` освобождается в `Dispose`, как `App.DisposeDesktopServices`.

## Ошибки

- Ловятся конкретные исключения: `IOException`, `SqliteException`, `InvalidDataException`, `ClockConflictException`. `catch (Exception)` допустим только на границе с пользователем (текст ошибки в окне) и на необязательных интеграциях с предупреждением, как в `App.ConnectOptionalServices`. Новых без этой причины нет (К4).
- Ошибка не глотается: она либо видна пользователю (`ErrorMessage`, `IntegrationWarning`), либо уходит наверх. Ошибка интерфейса не выдаётся за ошибку базы (Р2).
- Голый `Exception` не выбрасывается. Нарушение правила учёта: `InvalidOperationException` с текстом для пользователя по-русски, как в `StartPauseAsync`.
- Логика не ветвится по `exception.Message` и строкам ОС. Решение принимается по типу исключения, коду (`SqliteException.SqliteErrorCode`), enum или полю.

## Культура и тексты

- Значения для базы, файлов и журнала форматируются и разбираются с `CultureInfo.InvariantCulture` или явным форматом (`"O"`, `"yyyy-MM-dd"`). Анализатор CA1305 находит места без культуры (К2).
- Тексты интерфейса по-русски; имена в коде по-английски; комментарии по-английски (`AGENTS.md`).
- Пользователь видит время как `HH:mm` или `dd.MM.yyyy HH:mm:ss`, без долей секунды и без ISO-формата (Р4).

## Безопасность локальных файлов

- База, блокировка и сокет лежат в каталогах текущего пользователя; канал между экземплярами доступен только ему (`PipeOptions.CurrentUserOnly`, каталог с правами только владельца в `UnixInstanceSocket`). Новый файл, каталог или канал повторяет эту защиту.
- Файл от пользователя читается с ограничением размера и числа записей и с проверкой формата и версии документа, как `ActionLogJsonService` с `SizeLimitedReadStream`.
- Файл, который нельзя потерять наполовину, пишется через `AtomicFileService`.
- База пользователя не удаляется установкой, обновлением и удалением программы (README).

## Против лишнего кода

- Размер фикса по размеру проблемы. Правка в 2 строки, если она решает задачу, предлагается первой. Если фикс в 10 раз больше проблемы, остановись и спроси.
- Сначала поиск существующего: `Services/`, partial-файлы репозитория, тестовые помощники. Копия существующего кода с одной разницей: нарушение. Чтение строки в объект уже повторено 7 раз (А5), восьмого не добавляется.
- Функция не длиннее 100 строк, файл не длиннее 1200 строк. Функция, которая уже длиннее, правкой не удлиняется: новая логика уходит в private метод того же класса.
- Nullable-ссылки включены. Проверка на `null`, `??`, `?.` и `!` ставятся только для значения, которое по типам и инвариантам может отсутствовать; `!` только там, где инвариант виден в соседних строках.
- Нет мёртвого кода, неиспользуемых параметров, закомментированного кода, магических строк и чисел (константы с именем, как `SchemaVersion`, `MaximumImportBytes`).
- Поведение macOS, Windows, Avalonia и SQLite не угадывается: оно проверяется запуском или по документации с указанием источника.

## Перед сдачей

- `dotnet build WorkTimeRegister.slnx`: 0 предупреждений.
- Число предупреждений анализаторов не растёт: `dotnet build WorkTimeRegister.slnx --no-incremental -p:AnalysisLevel=latest-recommended` до и после правки, сравнение итоговой строки `Warning(s)`.
- Тесты по `cs-tests`.
- Отчёт по `AGENTS.md`: изменённые файлы, точные команды и их результаты.
