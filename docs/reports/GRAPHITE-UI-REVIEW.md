# Graphite UI review — 2026-10-13

> Implementation update at the end (section 9). Sections 1–8 are the audit as found.

Scope: the running Graphite interface on the isolated E2E stack (`http://127.0.0.1:5174`, backend 5081, fake mail). About 65 captures at 1440x900 and 390x844 (dark theme), each with automatic checks for horizontal overflow, WCAG 2 A/AA serious or critical axe violations and console errors. Screenshots are temporary (`scratchpad\ui`), not in the repository. Nothing was changed.

## 1. Overall assessment

The desktop interface is coherent, dense and professional: consistent dark Graphite shell, compact queue table, restrained ticket workspace with the conversation dominant, Department administration with a grouped vertical nav and a real overview, and a clean requester portal. Axe found **no serious or critical violations on any of the ~60 captured states**, and no desktop overflow. The significant problem is the **agent application at phone width, which is unusable**. Several smaller duplications and discoverability gaps keep it from the Freshdesk/Halo level of polish.

## 2. Confirmed defects

| # | Screen | Evidence | Recommended fix | Pri |
|---|---|---|---|---|
| D1 | Agent app at 390 px (queue, ticket, department admin, analytics) | `03-queue-all-m`, `07-ticket-m`, `20-dept-overview-m`: content squeezed to ~50 px beside the 60 px rail and the secondary nav; ticket title wraps one letter per line; the rest of the viewport is empty black. My overflow metric reported 0 because the shell clips instead of scrolling, so automatic checks pass while the page is unusable. | Decide a supported minimum width. If phones matter: collapse the secondary nav and rail into the hamburger drawer below ~768 px and let content take the full width. If not: show a single "use a larger screen" notice below the minimum. | P1 (P3 if mobile is out of scope) |
| D2 | Realtime connection, every first load | Backend log: 60 of 290 `POST /hubs/tickets/negotiate` returned 400 (21%); browser console shows "Failed to complete negotiation" on `/dashboard/<id>` in both runs. The client reconnects (230 succeeded), so live updates work, but it logs errors and wastes requests. Cause not isolated (candidates: two connection starts racing, or a CSRF token fetched before the session settled; `lib/realtime.ts`, `api/http.ts`). | Reproduce with one network capture, then make `useRealtime` start a single connection and fetch the CSRF token after sign-in settles. | P2 |
| D3 | Shortcuts dialog | `[` and `]` (previous/next ticket on the ticket page) work and are used by E2E, but `SHORTCUT_HELP` in `lib/shortcuts.ts` lists only list-level keys. | Add both keys to the help. | P3 |
| D4 | Dead code | `SlaStrip` in `components/Sla.tsx` is exported but not referenced anywhere (replaced by `SlaPanel` in the properties rail). | Remove it. | P3 |

## 3. Known review points: result

- **j / Enter:** intentional. The dedicated ticket page (`95550ad`, 2026-10-09) replaced the split view; `j`/`k`/`Enter` still operate on the queue list (listed in help, unit-tested in `queue.test.tsx`, not exercised in a browser this session). On the ticket page `[` and `]` move through the queue list. The old E2E step pressed `j` on a page where it no longer applies. Not a defect (see D3 for the help gap).
- **2 px portal overflow:** not reproduced. All portal pages at 390 px measured 0 px overflow with a mailbox configured. The earlier failure was in a Department without a mailbox; that state was not captured (needs a requester with no available forms), so it remains unverified.
- **SlaStrip:** remove (D4).
- **Permission-dependent settings:** mostly clear. The mailbox sender row is labelled "Mailbox administration" and appears only for administrators; a plain agent sees nothing and no explanation, which is correct for non-disclosure. On the Microsoft 365 page a platform administrator without a Department membership sees only "Knowledge" in the main nav (no Dashboard, Tickets, Analytics), which is consistent with "platform admin has no automatic ticket access" but may look broken without a hint (U6).
- **Graphite complexity:** the desktop workflows are not more complicated than before; the main cost is duplication (section 4).

## 4. Usability problems

| # | Screen | Evidence | Recommended fix | Pri |
|---|---|---|---|---|
| U1 | Ticket queue desktop | Two stacked navigations: primary rail plus a second "Tickets" panel that repeats "Dashboard" and has its own "Collapse" next to the primary "Collapse". | Drop "Dashboard" from the Tickets panel; keep one collapse control, or make the panel collapse follow the main one. | P2 |
| U2 | Ticket page left rail (`07-ticket-d`) | Icon-only column (counts 2, 0, 2, 0, search, doc) with no labels or tooltips visible; purpose unclear. | Add titles/tooltips and `aria-label`s, or replace with a "Back to queue" breadcrumb. | P2 |
| U3 | Ticket page | The requester name and email appear in the header and again in the Properties rail ("Requester"); the no-mailbox warning appears in the composer ("Email sending is unavailable…") and in the rail ("No mailbox: replies cannot be emailed"). | Keep one of each; keep the composer warning (it is where the action happens), shorten the rail text to a link. | P2 |
| U4 | Queue table at 1440 | The "Updated" column header is cut at the right edge; the table is slightly wider than the content area. | Let columns share width (narrower Subject/Assignee) or let the table scroll inside its container with a visible edge. | P2 |
| U5 | Department overview | Each card carries a description, a status chip, a corner arrow and "Manage →" (two affordances). Sections add a helper sentence. The left nav duplicates every card. | Drop the corner arrow; shorten or remove the section sentences; keep the cards as a setup/status summary. Overview is a locked decision, so keep it. | P3 |
| U6 | Microsoft 365 page | Intro paragraph repeats itself ("Staff sign-in, mail processing and shared mailboxes. Sign-in, mail processing and mailbox creation use separate identities."); "Not configured/Not connected" appears three times (alert, pill, card). | One-line intro; one status per area; keep the development sign-in warning (essential). | P3 |
| U7 | Portal home | "Recently added" shows several near-identical test articles; "Submit a request" duplicates the nav tab. | Test-data artefact; nothing to change besides reviewing the duplicate CTA. | P3 |
| U8 | Dashboard | Header says "Dashboard" in the top bar and again as the page title; the Workload panel is mostly empty space next to the large activity chart. | Drop the top-bar text on pages whose title already says it; reduce chart height. | P3 |

## 5. Visual inconsistencies

- Status chips use three treatments (outlined "✓ Open" toggles in the queue, filled pills on dashboard cards, plain dot + text in table rows): converge on one chip style.
- Primary actions vary: filled blue "New ticket", outlined "Edit department", text-link "Assign a mailbox", ghost "SLA details". Acceptable hierarchy, but link-style buttons in the properties rail look like navigation.
- Sentence-case headings dominate; the department nav uses small-caps group labels while the Tickets panel uses upper-case labels with different tracking.

## 6. Accessibility and responsive

- Axe: 0 serious/critical across ~60 states (desktop and phone), including dialogs; keyboard operation of dialogs was covered by existing E2E, not re-tested.
- Row edit buttons repeat the item name for screen readers (good), which breaks naive text locators only.
- Phone: portal pages fine (no overflow, 44 px-class targets); agent application broken (D1).
- Focus visibility and contrast in the light theme were not captured.

## 7. Incomplete or unverified

- Agent application at phone width (D1).
- Not visually verified: light theme, the new-ticket dialog contents at length, Analytics report pages, requester history drawer content, admin Knowledge/Macros editors, empty states of most administration tabs, error and loading states, notifications panel, People invitations flow, Entra connect dialog.
- Portal in the no-mailbox state (see section 3).
- j/k/Enter in a real browser.

## 8. Prioritised improvement plan

| Pri | Item |
|---|---|
| P0 | none |
| P1 | D1: decide supported minimum width; fix or gate the agent shell below it. |
| P2 | D2 hub negotiation 400s; U1 duplicate navigation/collapse; U2 label the ticket-page icon rail; U3 duplicate requester and mailbox messages; U4 queue table width. |
| P3 | D3 help entries; D4 remove `SlaStrip`; U5-U8 copy and duplication trimming; chip style consolidation; capture light theme and remaining states. |

Removed features (Tags, Forward, Time Tracking, Companies/Contracts) did not appear anywhere in the captured screens.

## 9. Implementation update (design decision: agent workspace and administration are desktop-first; the portal stays mobile-friendly)

### Resolved

| Finding | Resolution |
|---|---|
| D2 realtime 400s | **Root cause confirmed, application defect.** `getCsrf()` had no in-flight sharing: two callers at start-up (the SignalR negotiate and another request) each fetched `/api/csrf`; the second response replaced the antiforgery cookie, so the first token failed with `400 csrf_invalid` (`POST /hubs/tickets/negotiate`, 60 of 290 in the E2E log; 3 of 3 first loads reproduced it). Not specific to E2E or to dev mode (the dev double-mount only made it visible; a production start with any concurrent unsafe call can hit it, and a failed negotiate waits 15 s before retrying). Fix: concurrent callers share one pending fetch (`api/http.ts`). Reconnect logic untouched. After the fix: 0 negotiate 400s and half as many `/api/csrf` calls over the same three-load probe; new unit test covers it. |
| D1 phone shell | Agent/admin shell now shows a short "Use a larger screen" notice below 768 px with a link to the requester portal, instead of the crushed layout. No mobile redesign. |
| D3 `[`/`]` help | Added to the Shortcuts help. |
| D4 `SlaStrip` | Verified unreferenced (only its own definition and one CSS rule); component and `.sla-strip` CSS removed. |
| U1 duplicate navigation | "Dashboard" removed from the Tickets panel (the application rail has it). One collapse control remains: the rail's button now narrows both the rail and the queue panel (the panel's own Collapse button is gone). The workspace test was rewritten for that intended change. |
| U2 icon rail | The collapsed queue panel's links keep their `title`; behaviour unchanged. Not further modified. |
| U3 duplicates on the ticket page | Requester email removed from the header (name still opens the requester drawer; the properties rail and composer keep email); rail mailbox row says "None assigned" while the composer carries the full warning and the "Assign a mailbox" action stays. |
| U4 clipped "Updated" | Fixed table column widths trimmed (requester 150, status 140, SLA 96, assignee 130, subject min 240). Measured at 1440: table 1074 px in a 1086 px container, last header edge at 1434 of 1440. |
| U5 / U6 copy | Department overview: category helper sentences and the corner arrow removed (cards keep description, status and "Manage →"). Microsoft 365: intro shortened. |
| Platform-admin nav | When the user has no ticket access, the rail shows "Tickets appear here once you have a Department membership." |
| New: agent viewing the portal on a phone | `.portal-tools` overflowed by ~129 px for staff (extra "Back to Helpdesk" link); now wraps (0 px). Requester (employee) portal pages measured 0 px before and after. |

### Remaining

- Microsoft 365: "Not configured/Not connected" still appears in the alert list, the section pill and the summary card (not consolidated; needs a decision on which status owns the page).
- U2: no new visual treatment for the ticket-page icon rail beyond its existing titles.
- U7/U8, chip-style consolidation, light-theme capture and the unverified states of section 7 are untouched.
- The 2 px portal overflow was never reproduced.

### Tests executed (isolated stack)

- Vitest (frontend only, full): 34 files, 199 tests passed (198 + 1 new CSRF-sharing test); `tsc -b` clean; oxlint 0 errors (existing warnings only).
- Playwright, full suite once: 13 passed, 0 failed, 9 skipped (opt-in captures).
- Targeted browser checks (temporary specs, removed): 1440 queue column fit, no Dashboard link in the Tickets panel, one collapse button, no header email; 390 agent notice, no overflow; portal pages at 390 as employee 0 px and as agent 0 px (after the fix); platform admin nav hint present with only "Knowledge".
- Backend not touched, backend suite not rerun.

### Unverified

Light theme; the fallback notice on a real phone (checked at 390 px viewport only); the narrow-rail appearance of the new nav note.

### Finalisation pass

- **Microsoft 365 repeated status (resolved):** "not configured" appeared in the findings list, the sign-in section badge and the Overview sign-in card. It is now stated once, by the "Staff sign-in" section with its connect action: the `sign_in_not_configured` finding is hidden on this page and the card shows its badge only when configured (the card keeps tenant, client id and secret presence, which a test asserts). Development sign-in, tenant-difference and mailbox findings are unchanged.
- **Ticket icon rail (defect found and fixed):** the collapsed queue links had `title` but their accessible name came from the count badge ("2 open"), so a screen reader heard no queue name. Each link now has an explicit label ("My Queue, 2 open"; "SLA breached, N breached"); the visual design is unchanged.
- **Light theme:** dashboard, queue, ticket page, administration overview and mailboxes at 1440 px: 0 axe contrast violations and no other serious findings; screenshots of the ticket page and overview read cleanly. No theme changes.
- **CSRF fix confirmed:** concurrent callers share one pending fetch; the pending state is cleared in `finally` after success and after failure (a failed fetch rejects all waiters and the next call fetches again). Tests: concurrent sharing, failed fetch then recovery (new), and the existing 400 `csrf_invalid` refresh-and-retry.
- **Tests:** frontend unit 34 files, 200 tests passed (this pass: 2 new CSRF tests overall); `tsc -b` clean; oxlint 0 errors. Playwright `agent-workflow`, `productivity`, `service`: 7 passed (full suite of 13 passed earlier in this pass before the final rail and Microsoft 365 edits).
- **Still open:** ticket page icon rail has no visible labels by design (compact); chip-style consolidation; unverified states listed in section 7 other than light theme.
