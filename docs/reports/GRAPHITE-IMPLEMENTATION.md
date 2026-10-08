# Graphite implementation

Primary reference: `docs/design/redesign/codex-prototype/` (read-only).

## Completed sections

- Application shell and navigation: Graphite global rail, contextual queue navigation, top bar, real search, notifications, creation and account actions. Manrope is bundled with its licence. Explicit light/dark preference uses guarded existing preference storage; dark is the default.
- Shell verification: App tests 4/4, theme preference tests 2/2. Local real-API browser screenshots checked in dark and light at 1366×768, without horizontal overflow.

No backend, API, database, permission or email changes. Browser plugin bootstrap is broken (missing service module); local installed Playwright/Edge is used for visual evidence instead. Screenshots are local ignored artifacts under `src/web/TestResults/`.
- Administration and Department Overview: full-height grouped navigation, searchable/status-filtered department table, grouped setup rows and contextual details. Existing permission-gated queries and edit flows retained. Targeted administration tests: 14/14 passed (updated selectors for grouped setup lists and field roles). Real local API views checked in both themes at 1366×768 and 1440×900; no viewport overflow.
- Mailbox Management: compact operational summary, combined sender/health/sync table, subscription detail disclosure, create/edit drawers, and prioritised mail attention. Admin sender controls, warnings, retries and acknowledgement settings remain connected to existing APIs. Targeted mailbox tests: 5/5 passed, including permissions, creation and uncertain-send handling. Both themes checked at both target sizes, including the edit drawer.
