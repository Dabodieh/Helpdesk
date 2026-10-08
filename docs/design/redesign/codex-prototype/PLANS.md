# Graphite prototype execution plan

Scope: an independent, static visual concept for Administration, Department overview, Mailboxes and Agent workspace, plus a comparison landing page. Single agent. All work is contained in this directory. Do not read, copy or modify the prior prototype.

Inspected: project/global guidance, architecture and MVP, development status, production AppShell, DepartmentPage, DepartmentOverview, MailboxesSection, WorkspacePage, Composer and design tokens.

Reusable product patterns: queue-first work; grouped vertical Department navigation; setup states rather than decorative metrics; fixed ticket mailbox identity; explicit public replies/Internal Notes; local requester identity; scoped configuration. Reuse concepts, not production styles or the previous prototype.

Design: Graphite, a neutral mineral palette with vermilion action accent, locally hosted Manrope, compact application rail, contextual navigation, legible 14px operational text, shared semantic tokens for light/dark themes. Design variance 5, motion 2, density 7. Native HTML/CSS/JS as requested takes precedence over skill package defaults. No decorative imagery is needed in operational screens.

Implementation: shared shell, CSS tokens, static fixture data, accessible native dialogs styled as drawers, local interaction state. No framework or package installation. Tabler SVG assets and Manrope font carry their upstream licences.

Data/API/UI impact: only the isolated UI concept changes. No database/model, API, production route, permission, privacy, audit or security implementation changes. All examples are synthetic. Screen access models are explanatory, not authorization implementations.

Risks: visual concept is not production functionality; demo actors have explicit Department membership; no real Graph connection or delivery validation; unimplemented settings must identify themselves as previews. Tickets retain their stored sender. Uncertain mail must not offer blind retry.

Verification: JS syntax; direct file opening; four screens at 1440x900 and 1366x768 in both themes; screenshot inspection; selected ticket, reply/note switching, queue filters, theme persistence, add/edit mailbox validation and focus; overflow and browser errors. No full test suites or backend tests. Browser plugin startup is broken (missing referenced runtime); use already-installed Playwright locally.

Documentation: README includes design rationale, boundaries, asset sources, review checklist, screenshots and local changelog. Production roadmap/status remains unchanged.

Rollback: remove only this new directory if the concept is rejected. No production rollback is necessary.

Status: prototype complete; stopped for user visual approval. Five entry pages plus screenshot gallery, shared tokens/shell, local interactions and design notes delivered. All 16 requested screen/theme/viewport states captured and visually inspected; two drawer captures also inspected. Fourteen recorded interaction/review checks passed. Eight screen/theme accessibility states have zero axe WCAG 2/2.1 A/AA violations after the composer tab grouping fix. No observed page errors, external HTTP requests or horizontal content overflow. No integration, commit or deployment.

Final Git observation: the redesign directory is untracked (the prior prototype was already untracked at task start). At the final status snapshot, `docs/guides/GRAPH_TENANT_VALIDATION.md`, `scripts/ops/backup-database.sh` and `scripts/ops/backup-objects.sh` were staged by work outside this task; this task did not read, edit, stage or revert them. All writes by this task remained in `docs/design/redesign/codex-prototype/`.
