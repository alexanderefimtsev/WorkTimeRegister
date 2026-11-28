---
name: investigate
description: "Evidence-based WorkTimeRegister bug investigation: every claim cited, reproduced by a test, user DB read only as a copy, cause and proposed fix without implementing it. Invoke with $investigate."
---

# investigate

## Rules

1. Every claim cites evidence: `file:line` at a named commit, command output, DB or log row. Label guesses as guesses.
2. Confirm code reading by a run: a reproducing test or a run on a DB copy. List what was not run.
3. Investigate the actual case: that DB, day, app version, OS and time zone.
4. A current row is not the value at event time: `WorkDays` and `ApplicationState` are overwritten; `ActionLog` keeps per-action snapshots; `*.bak` files keep pre-migration state.
5. A missing row does not prove the event did not happen; check every write path.
6. "Fixed in version X?": `git merge-base --is-ancestor <fix> <version>` plus the code, not dates.
7. Conflicting sources: stop, name both, check a third.
8. Report failures and partial results explicitly.

## Data

- User DB paths: `cs-guide`. Never write to it: close the app, copy the file outside the repo, open the copy with `sqlite3 -readonly`. Day queries: `sqlite`.
- The JSON log export is not a DB backup (README).

## Steps

1. Symptom: what the screen shows, which day, expected and shown numbers.
2. App version, OS, machine time zone, `WorkDaySettings`.
3. Rows of that day from the copy.
4. Reproducing test, written in a repo copy in a temp folder; it enters the repo only with an agreed fix.
5. Cause: `file:line` and how it produces the shown number.
6. Proposed fix: file and method, why it removes the cause, what else it touches, which test turns green. Smallest option first. Code changes wait for the user's approval (AGENTS.md).

## Output

Two sentences in chat: what happened, what to do. Writing a session to `ANALYSIS.md`: ask first.
