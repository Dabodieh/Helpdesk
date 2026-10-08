# Helpdesk — Graphite design prototype

An independent, static, browser-viewable enterprise ITSM visual exploration built on Codex's initial **Graphite** stylesheet and execution plan.

## Open it

Double-click `index.html` (or start at `workspace.html`). The landing page shows live miniature previews of four HTML screens and links to each full-size page.

- `admin.html` — organisation administration, Department search/filter and a demonstration create drawer.
- `department-overview.html` — configuration readiness, ownership, mail and SLA settings in a scoped Department view.
- `department-mailboxes.html` — Microsoft 365 mailbox concepts, example attention states, a working local add/edit drawer and input validation.
- `workspace.html` — queue navigation, synthetic ticket selection, conversation, clearly distinct Internal Note/public reply modes and properties.

A light/dark theme control appears in the global header and rail and persists where the browser allows localStorage. Keyboard Ctrl+K opens only a *concept* notification, not a functional search system.

## Design direction

- Deep mineral dark navigation; warm off-white operational surfaces; restrained vermilion for primary actions.
- Dense, legible configuration and conversation layouts without fake charts or metrics.
- Compact global icon rail plus context-specific departmental or workspace navigation.
- Consistent native controls, semantic status colours, typography and accessible labelled interactions.
- Strong separation of requester-visible messages and internal notes; mail sender identity is shown as fixed per ticket.

## Safety and isolation

All names, ticket examples, teams and operational states are **illustrative**. No API connections, Microsoft Graph requests, backend code, account logins, persistence of business data, mail sending or production changes are included. The only browser persistence is the user's visual theme choice.

The mailbox drawer simulates new/edit changes only in the open page. No Graph access/scope checks are performed and no actual mailbox is registered. Platform Admin and Department-access restrictions are only described, not implemented as authorization. The preview does not change approved product contracts.

Some controls show “Visual concept only” because the other product modules are intentionally out of prototype scope. The global search is a concept.

## Source and limitations

`styles.css` began with the unfinished Graphite foundation Codex wrote. This prototype adds a completion layer and lightweight HTML/JS. `PLANS.md` preserves the original plan. The implementation uses platform system fonts and inline hand-coded SVG icons: **no font or third-party icon files are packaged**.

Render and interaction checks should be performed in your own browser at 100% zoom, particularly at 1440×900 and 1366×768, in light and dark themes. The execution environment's Chromium browser blocks all URLs by administrator policy, so automated browser screenshots are not included and are not claimed.

## Review criteria

Assess whether the workspace keeps ticket conversation dominant, whether section navigation is useful, whether mail and ownership state read immediately, and whether the overall aesthetic looks like a product rather than a generic dashboard. This is **Phase 1 only**; no production UI changes or commit were made.
