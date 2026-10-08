# Graphite implementation

Primary reference: `docs/design/redesign/codex-prototype/` (read-only).

## Completed sections

- Application shell and navigation: Graphite global rail, contextual queue navigation, top bar, real search, notifications, creation and account actions. Manrope is bundled with its licence. Explicit light/dark preference uses guarded existing preference storage; dark is the default.
- Shell verification: App tests 4/4, theme preference tests 2/2. Local real-API browser screenshots checked in dark and light at 1366×768, without horizontal overflow.

No backend, API, database, permission or email changes. Browser plugin bootstrap is broken (missing service module); local installed Playwright/Edge is used for visual evidence instead. Screenshots are local ignored artifacts under `src/web/TestResults/`.
