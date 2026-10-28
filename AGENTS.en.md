# Project overview

- This is a desktop application developed in C# in IntelliJ IDEA.
- SQLite is used as the local database. The database must remain lightweight and file-based and must not require installing a separate server.
- The application records working time throughout the day:
  - start and end of work;
  - start and end of breaks;
  - total working time and total break time for the current day;
  - elapsed working or break time for the currently active period.
- Daily totals and the duration of the currently active period must remain visible and update promptly.

# Working rules

- Before changing code, inspect the related files and briefly explain the implementation plan to the user.
- Do not perform broad refactoring without separate permission.
- Do not rename public APIs, classes, methods, DTOs, or endpoints without an explicit request.
- Keep changes within the scope of the task and preserve existing behavior outside that scope.

# C# style

- Use a modern, concise C# style consistent with the existing codebase.
- Prefer `var` for local variables when the type is obvious from the right-hand expression.
- Write C# code comments in English.
- Preserve the original language of file, class, method, endpoint, and configuration key names; do not translate them.

# User interface style

- Treat the user interface as a desktop interface unless the project explicitly selects a different client type.
- Follow the patterns and naming conventions already established in the selected UI framework.
- Keep total working time and total break time for the current day, as well as the duration of the currently active period, continuously visible and up to date.
- Do not add or replace the UI framework without explicit permission.

# Configuration rules

- Do not modify `docker-compose.yml`, `appsettings.json`, or frontend configuration without separate permission.
- Do not rename configuration keys without an explicit request.
- Store the database locally and use SQLite unless the user explicitly agrees to a different database.
- Do not introduce a dependency on a separately installed database server.

# Verification

- After changes, run appropriate tests and/or a build if the local environment allows it.
- Report the exact commands executed and their results.
- If verification is impossible, clearly explain why.
- When changing the corresponding logic, verify time calculations for active working periods, active breaks, completed periods, and daily totals.

# Response format

- Communicate with the user in Russian.
- Write all text in `AGENTS.md`, including headings and new rules, in Russian.
- After changes, list every modified file.
- Report the exact test and build commands together with their results.
- If tests or a build were not run, explain why.
- Do not translate file, class, method, DTO, endpoint, or configuration key names.

# Commits and publishing changes

- Project repository: https://github.com/alexanderefimtsev/WorkTimeRegister.
- After making changes, successfully testing them, and producing a new build, show the user a brief description of the changes, verification results, the build path, and the proposed commit message.
- Wait for the user's explicit approval of the proposed message before creating the commit and pushing changes to the repository.
- Approval covers only the displayed changes and commit message; show a revised proposal if substantial changes are made.
- If testing or building is impossible or fails, report that before requesting commit approval.
- After pushing changes, report the commit link and the publication result.

# Analysis and recommendations

- Keep discussions of project analysis, audits, and recommendations in `ANALYSIS.md` at the repository root.
- Before a new review, read the current `ANALYSIS.md` from the repository and take previous sessions into account.
- Add new reviews, responses, agreed decisions, implementation results, and verification results as separate sessions identifying the author, date, and reviewed version or commit.
- Preserve previous sessions and their attribution; do not retrospectively rewrite another author's conclusions. Record clarifications and objections in a new session.
- Distinguish proposals from agreed requirements, and planned changes from changes that have been implemented and verified.
- Briefly report the outcome to the user in chat and link to the relevant entry in `ANALYSIS.md`.
- Publish changes to `ANALYSIS.md` under the general commit approval rule; discussion in the file does not waive confirmation before pushing.

# English version of the rules

- Keep a complete English translation of these rules in `AGENTS.en.md` without losing meaning.
- Whenever the rules change, update both versions together. Keep the primary `AGENTS.md` file in Russian; do not translate file, class, method, DTO, endpoint, or configuration key names.
