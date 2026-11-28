---
name: cs-review
description: "Review a WorkTimeRegister diff, branch or commit: AGENTS.md, C# (.NET 10, C# 14), Avalonia, Microsoft.Data.Sqlite, xUnit v3; numbered table report, fixes only on request. Invoke with $cs-review."
---

# cs-review

## Method

- Scope: what the user named. Default: uncommitted changes (`git diff -U3 HEAD` plus untracked files). Branch: `git diff -U3 main...HEAD`.
- Token budget: read the diff and only the methods it touches or calls. No repo scan, no subagents. Trim command output.
- Run the commands first, then read.
- Only added or changed lines count. Report violations only, nothing that is fine.
- Each rule is violated or not; no severity. PASS means 0 violations.
- Change nothing until the user names items ("fix 2 and 4"); then fix, rerun the commands, re-report with the same numbers.
- Writing to `ANALYSIS.md`: ask first. Never commit or push.

## Commands

```bash
# 1 comments: the grep from "Rules from Yuri" in AGENTS.md
# 2 analyzers on changed files
{ git diff --name-only HEAD -- '*.cs'; git ls-files -o --exclude-standard -- '*.cs'; } > /tmp/wtr-changed.txt
dotnet build WorkTimeRegister.slnx --no-incremental -p:AnalysisLevel=latest-recommended 2>&1 | grep -E 'warning (CA|CS|IDE|SYSLIB)' | grep -v CA1707 | grep -F -f /tmp/wtr-changed.txt | sed -E 's/ \[[^]]*\]$//; s#^/[^(]*/(src|tests|packaging)/#\1/#' | sort -u | cut -c1-160
# 3 build and tests
dotnet build WorkTimeRegister.slnx 2>&1 | tail -n 3
dotnet test WorkTimeRegister.slnx -m:1 -nodeReuse:false 2>&1 | grep -E 'Passed!|Failed!'
```

For a branch, replace `HEAD` with `main...HEAD` in step 2.

## Rules

Project (`AGENTS.md`):
- [ ] No comment in added or changed lines (Rules from Yuri).
- [ ] Only what the task needs: no broad refactoring, no renamed public APIs, classes, methods, DTOs, endpoints or config keys; behaviour outside the task unchanged.
- [ ] UI framework unchanged; day totals and current period stay visible and updating.
- [ ] Time math correct for active work, active break, completed periods, day totals.
- [ ] SQLite stays local, no database server.
- [ ] `AGENTS.md` changed: `AGENTS.en.md` matches.

C# (.NET 10, C# 14):
- [ ] 0 new analyzer warnings (command 2) and 0 new compiler warnings, nullable CS86xx included.
- [ ] `!` only with a visible invariant; no `?.` or `??` on values that cannot be null.
- [ ] `async void` only in event handlers and `AsyncCommand.Execute`; no `.Result`, `.Wait()`, `GetAwaiter().GetResult()` outside `Program.Main`.
- [ ] Every created `IDisposable` is disposed with `using` or owned by a disposable type (CA2000, CA1001, CA2213).
- [ ] No `catch (Exception)` except UI or optional-integration boundaries that show a message; no empty catch; no catch that only logs and rethrows (CA1031).
- [ ] No logic on exception messages or other free text; decide on types, codes, enums.
- [ ] Specific exception types; argument exceptions built correctly (CA2208).
- [ ] Persisted and file data formatted and parsed with `CultureInfo.InvariantCulture` or an explicit format (CA1305); string comparisons pass `StringComparison` (CA1310).
- [ ] Now from the injected `TimeProvider`; instants as `DateTimeOffset`; no implicit `DateTime` to `DateTimeOffset`; day windows via `WorkDayService.Boundary`, end exclusive.
- [ ] New P/Invoke uses `[LibraryImport]` (SYSLIB1054).
- [ ] No copy of existing logic: SQL helpers, `Format`, `Parse`, `ToSeconds`, calculators, `WorkDayService.Boundary`, test clocks.
- [ ] Function at most 100 lines, file at most 1200; a longer function does not grow.
- [ ] No dead code, unused parameters, magic strings or numbers.

Avalonia 11.3:
- [ ] View-model state changes on the UI thread (`Dispatcher.UIThread`) and under `stateLock`.
- [ ] New windows in XAML with `x:DataType`; no tracking logic in code-behind.
- [ ] Commands raise `CanExecuteChanged` after state changes.

Microsoft.Data.Sqlite:
- [ ] Values only as parameters.
- [ ] Multi-row writes: one transaction and one reused parameterized command (Microsoft Learn, bulk insert).
- [ ] Reads filter in `WHERE` and are bounded by a time window; no full-table read filtered in C#.
- [ ] Instants via `Format` (UTC, `"O"`), durations via `ToSeconds`.
- [ ] Connections, commands and readers disposed.
- [ ] Schema change: new `user_version` block, backup before migration, migration test.
- [ ] No heavy SQLite work on the UI thread: async methods run synchronously (Microsoft Learn, async limitations).

Tests (xUnit v3):
- [ ] Changed behaviour has a test; a fixed bug has a test that failed before the fix.
- [ ] Real SQLite in a temp folder, `TimeProvider` subclass, explicit offsets, no mocks of project classes.
- [ ] No `Thread.Sleep`; no `Task.Delay` waits except the existing `WaitForAsync` after `ICommand.Execute`.
- [ ] Expected values are literals, not recomputed by the code under test.

## Report

```
# Code Review: <scope>

## Violations
| # | Rule | File | Line(s) | Why it violates the rule | Fix |

## Behaviour changes (confirm intent)
| # | File | Line(s) | Before -> after | Intended? |

## Checks run
<command>: <result line>

## Verdict
- Violations: N (#...)
- Status: PASS (0) | CHANGES REQUIRED (N)
```

One numbering across all sections. Numbers never change; new findings continue the sequence. After requested fixes, add a `Fixed` column and write "Violations: N found, N fixed".
