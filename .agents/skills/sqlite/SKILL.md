---
name: sqlite
description: "WorkTimeRegister SQLite patterns: connection and helpers, column types and time storage, window queries, transactions, index-backed invariants, user_version migrations with backup, debug queries. Invoke with $sqlite."
---

# sqlite

Microsoft.Data.Sqlite with raw SQL, no ORM (EF Core considered and declined in ANALYSIS.md session 9). All SQL is in the `SqliteTimeEntryRepository` partials.

## Access

- One connection per operation via `ConnectionString` (`ForeignKeys = true`).
- Helpers in `.WorkDays.cs`: `CreateSql`, `ExecuteSqlAsync`, `ScalarAsync`; parameters as `("$name", value)`, `null` becomes `DBNull.Value`.
- Transactions: `connection.BeginTransaction()` (`ProcessWorkDayAsync`).
- All values are parameters; the only interpolation is a constant filter in `ReadWorkDayAsync`.

## Types

| C# | Column | Via |
| --- | --- | --- |
| `DateTimeOffset` | TEXT | `Format` (UTC, `"O"`, invariant culture), `Parse` (`AssumeUniversal`) |
| `DateOnly` | TEXT | `"yyyy-MM-dd"` |
| `TimeSpan` | INTEGER seconds | `ToSeconds` (`checked`), `TimeSpan.FromSeconds` |
| bool | INTEGER 0 or 1 | |
| enum | INTEGER | `TimeEntryKind`, `WorkDayCloseReason`, `RecoveryOrigin` (`ApplicationState.RecoveryKind`) |
| time zone | TEXT | `TimeZoneInfo.Id` (`WorkDays.TimeZoneId`) |

Window queries compare timestamps as strings. This works because every stored instant goes through `Format`.

## Queries

- Overlap: `StartedAt < $to AND (EndedAt IS NULL OR EndedAt > $from)`. Event in window: `RegisteredAt >= $from AND RegisteredAt < $to`.
- Multi-row writes share one transaction (`StopActiveAsync`, `AddTransferAsync`, `ImportActionLogAsync`).
- One-active-row invariants are partial unique indexes on `((1)) WHERE ...` (`IX_TimeEntries_Active`, `IX_TimePauses_Active`, `IX_WorkDays_Open`); single-row tables use `CHECK(Id=1)`.
- Guarded insert with affected-row check: `StartPauseAsync`. Duplicate-free import: `WHERE NOT EXISTS` (`ImportActionLogAsync`).
- Default command timeout is 30 s; a locked DB file blocks the UI that long (ANALYSIS.md sessions 9, 11).

## Migrations

- `CREATE TABLE IF NOT EXISTS` in `InitializeAsync`; version in `PRAGMA user_version`, constant `SchemaVersion` in `.Runtime.cs`.
- A DB newer than the app makes `ValidateSchemaVersionAsync` throw `InvalidDataException`.
- `MigrateReliabilityAsync` pattern: backup via `connection.BackupDatabase` to `worktime.db.before-<topic>-<utc>-<guid>.bak` when data exists; one transaction; `if (version < N)` blocks ending with `PRAGMA user_version=N`. The last block has no `if` yet; a new version needs one there. `.gitignore` lists the backup masks.
- `PRAGMA table_info` column checks predate `user_version`.

## Debug queries (on a copy, `sqlite3 -readonly`)

```sql
PRAGMA user_version;
PRAGMA integrity_check;
SELECT * FROM WorkDaySettings;
SELECT * FROM ApplicationState;
SELECT * FROM WorkDays WHERE WorkDate = '2026-10-28';
SELECT * FROM TimeEntries WHERE WorkDayId = :dayId ORDER BY StartedAt;
SELECT * FROM TimePauses WHERE TimeEntryId IN (SELECT Id FROM TimeEntries WHERE WorkDayId = :dayId) ORDER BY StartedAt;
SELECT * FROM TimeTransfers WHERE WorkDayId = :dayId ORDER BY RegisteredAt;
SELECT * FROM ActionLog WHERE RegisteredAt >= :dayStart AND RegisteredAt < :dayEnd ORDER BY RegisteredAt, Id;
```

`:dayId`, `:dayStart`, `:dayEnd` come from the `WorkDays` row (`Id`, `DayStart`, `DayEnd`).
