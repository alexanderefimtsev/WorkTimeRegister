---
name: sqlite
description: "Правила SQL и SQLite в WorkTimeRegister: подключение и помощники, типы колонок и хранение времени, параметры, выборка по окну, транзакции, инварианты в индексах, миграции через PRAGMA user_version с резервной копией, план запроса, отладка на копии базы. Вызывается пользователем: $sqlite."
---

# SQL и SQLite в WorkTimeRegister

Канон проекта: `AGENTS.md`. База лёгкая, файловая, без отдельного сервера; другая база только по согласованию.

## Как устроено

- Доступ к базе: Microsoft.Data.Sqlite напрямую, без ORM. Весь SQL живёт в `SqliteTimeEntryRepository` и его partial-файлах, вне `Data/` SQL нет (тесты не в счёт).
- Подключение открывается на операцию: `await using var connection = new SqliteConnection(ConnectionString)`. Строка подключения включает `ForeignKeys = true`; новое подключение строится только через `ConnectionString`.
- Помощники в `SqliteTimeEntryRepository.WorkDays.cs`: `CreateSql`, `ExecuteSqlAsync`, `ScalarAsync`. Параметры передаются кортежами `("$name", value)`, `null` превращается в `DBNull.Value`. Новый код внутри транзакции пользуется ими и не повторяет `CreateCommand` и `Parameters.AddWithValue` вручную.
- Транзакция открывается `connection.BeginTransaction()`, как в `ProcessWorkDayAsync`: метод сразу даёт `SqliteTransaction`, приведение типа не нужно.
- Команды и читатели освобождаются через `using` или `await using`.

## Типы колонок

| Значение в C# | Колонка | Запись и чтение |
| --- | --- | --- |
| `DateTimeOffset` | `TEXT` | `Format`: UTC, формат `"O"`, `InvariantCulture`; чтение `Parse` с `AssumeUniversal` |
| `DateOnly` | `TEXT` | `"yyyy-MM-dd"`, чтение `DateOnly.ParseExact` |
| `TimeSpan` | `INTEGER`, секунды | `ToSeconds` с `checked`; чтение `TimeSpan.FromSeconds` |
| `bool` | `INTEGER` 0 или 1 | `value ? 1 : 0`; чтение `!= 0` |
| enum | `INTEGER` | `(int)value`; чтение приведением к enum |
| часовой пояс | `TEXT` | `TimeZoneInfo.Id`, колонка `WorkDays.TimeZoneId` |

SQL сравнивает моменты как строки (`StartedAt < $to`). Сравнение верно, потому что все моменты записаны одинаково: UTC, формат `"O"`, 7 знаков после секунды, суффикс `Z`. Момент, записанный в другом формате или со смещением, ломает выборки по окну. Поэтому время пишется в базу только через `Format`.

## Запросы

- Каждое значение передаётся параметром `$name`. Интерполяция в текст SQL допустима только для констант из кода, как фильтр в `ReadWorkDayAsync`; значения от пользователя, из файла или из базы в текст SQL не попадают.
- Фильтр стоит в `WHERE`. Чтение всей таблицы с отбором в C# не допускается. Таймер обновляет окно раз в секунду, поэтому запрос по таймеру читает только окно текущего дня, и объём чтения не растёт с историей.
- Пересечение периода с окном: `StartedAt < $to AND (EndedAt IS NULL OR EndedAt > $from)`. Событие в окне: `RegisteredAt >= $from AND RegisteredAt < $to`. Конец окна не входит.
- Записи, которые должны сохраниться все или ни одна, пишутся в одной транзакции с `CommitAsync` в конце (`StopActiveAsync`, `AddTransferAsync`, `ImportActionLogAsync`). Данные для решения читаются внутри той же транзакции, что и запись.
- Правило «не больше одной активной записи» держит частичный уникальный индекс: `IX_TimeEntries_Active`, `IX_TimePauses_Active`, `IX_WorkDays_Open` на `((1)) WHERE ...`. Правило «одна строка в таблице» держит `CHECK(Id=1)` (`WorkDaySettings`, `ApplicationState`). Новое правило такого вида тоже уходит в индекс или `CHECK`, не в проверку в C#.
- Вставка, которая не должна сработать при нарушенном условии: `INSERT ... SELECT ... WHERE EXISTS (...) AND NOT EXISTS (...)` и проверка числа изменённых строк, как в `StartPauseAsync`.
- Импорт без повторов: `WHERE NOT EXISTS` по полям записи, как в `ImportActionLogAsync`.
- Колонка, по которой новый запрос фильтрует или сортирует, получает индекс, если запрос идёт по таймеру или по всей истории. Проверка: `EXPLAIN QUERY PLAN` на копии базы показывает `SEARCH ... USING INDEX`, а не `SCAN`.
- Асинхронные методы Microsoft.Data.Sqlite выполняются синхронно (сессия 9): долгий запрос держит окно. При занятом файле базы окно ждёт до 30 с, таймаута команды по умолчанию. Обработка ожидания базы отложена до измерения (сессия 11); таймауты без согласования не меняются.

## Схема и миграции

- Начальная схема: `CREATE TABLE IF NOT EXISTS` в `InitializeAsync`. Версия схемы: `PRAGMA user_version` и константа `SchemaVersion` в `SqliteTimeEntryRepository.Runtime.cs`.
- База новее программы (`user_version > SchemaVersion`) не открывается: `ValidateSchemaVersionAsync` бросает `InvalidDataException`. Это правило не ослабляется.
- Новая версия схемы, по шагам:
  1. `SchemaVersion` растёт на 1.
  2. Прежний последний блок в `MigrateReliabilityAsync` получает своё условие `if (version < <его номер>)`, сейчас он выполняется без условия.
  3. Новый блок `if (version < N)`: `ALTER TABLE ... ADD COLUMN ... NOT NULL DEFAULT ...` или новая таблица, перенос данных SQL-запросом, `PRAGMA user_version=N` в том же блоке.
  4. Вся миграция идёт в одной транзакции.
  5. Перед миграцией базы с данными: резервная копия рядом с базой через `connection.BackupDatabase`, имя `worktime.db.before-<тема>-<UTC время>-<guid>.bak`, как в `MigrateReliabilityAsync`. Маска имени добавляется в `.gitignore` рядом с `*.db.before-reliability-*`.
  6. Перенос старых строк не берёт текущее время машины; если оно нужно, миграция получает `TimeProvider`, как `MigrateWorkDaysAsync`.
  7. Тест миграции по `cs-tests`: база старой версии, созданная в тесте, затем `InitializeAsync`, затем данные, `user_version` и одна резервная копия.
- Проверка колонки через `PRAGMA table_info` (`EnsureCurrentPeriodAdjustmentColumnAsync`, `EnsureActionLogIsPausedColumnAsync`) осталась с версий без `user_version`. Новые изменения схемы идут через `user_version`, третья такая проверка не появляется.

## Отладка на копии базы

- База пользователя: `~/Library/Application Support/WorkTimeRegister/worktime.db` (macOS), `%LOCALAPPDATA%\WorkTimeRegister\worktime.db` (Windows), README. Рядом лежат резервные копии `*.before-workdays-*.bak` и `*.before-reliability-*.bak`.
- В базу пользователя руками ничего не пишется. Программа закрывается, файл копируется во временную папку вне репозитория, копия открывается `sqlite3 -readonly <копия>`.
- Запросы для разбора одного дня:

```sql
PRAGMA user_version;
PRAGMA integrity_check;
SELECT * FROM WorkDaySettings;
SELECT * FROM ApplicationState;
SELECT Id, WorkDate, DayStart, DayEnd, TimeZoneId, StartedAt, EndedAt, LastMode, WasPaused, CloseReason
FROM WorkDays WHERE WorkDate = '2026-10-28';
SELECT * FROM TimeEntries WHERE WorkDayId = :dayId ORDER BY StartedAt;
SELECT * FROM TimePauses WHERE TimeEntryId IN (SELECT Id FROM TimeEntries WHERE WorkDayId = :dayId) ORDER BY StartedAt;
SELECT * FROM TimeTransfers WHERE WorkDayId = :dayId ORDER BY RegisteredAt;
SELECT * FROM ActionLog WHERE RegisteredAt >= :dayStart AND RegisteredAt < :dayEnd ORDER BY RegisteredAt, Id;
```

`:dayId`, `:dayStart` и `:dayEnd` берутся из строки `WorkDays` (`Id`, `DayStart`, `DayEnd`). Числа в колонках с enum расшифровываются по `Models/`: `Kind` и `LastMode` по `TimeEntryKind`, `CloseReason` по `WorkDayCloseReason`, `ApplicationState.RecoveryKind` по `RecoveryOrigin`.
