# Phase 4 report: agent productivity, realtime and UX polish

Status: **built and tested; closed for Phase 4 scope.** Production readiness is still gated by the items in "Remaining production gates" (unchanged from Phase 3). Phase 5 has not been started.

## UX changes
- Base type raised to 15 px, stronger hierarchy (page headers, section heads), consistent selected / hover / focus states, reduced-motion respected.
- **Queue:** checkbox column, sortable Priority and Updated headers (`aria-sort`), compact/comfortable density (remembered), clear selected row, columns collapse by container width, safe counts in the navigation, search matches shown under the subject with a highlighted snippet and the field they matched (message / note).
- **Resizable split:** queue and ticket separated by a keyboard-accessible separator (`role=separator`, arrows, Shift = larger step, Home/End/Enter); width clamped (queue at least 360 px, ticket at least 460 px) and remembered locally. Below 900 px the panes remain separate views.
- **Ticket header:** reference and department, large subject, status / priority / team / assignee controls in one row, requester and dates as secondary text.
- **Conversation:** requester, public reply and internal note are different by label, icon, border style (solid / accent stripe / dashed), indent and background, never by colour alone. Attachments are cards with name, size and scan/availability state.
- **Composer:** bordered box whose style, banner text and button label change with the explicit mode (Public reply / Internal note); "Insert response" button; Ctrl+Enter hint; Enter alone never sends; draft survives mode changes and conflicts; delivery state unchanged.
- **Administration:** department page with tabs (Overview, Teams, Members, Mailboxes, Responses; tabs the caller cannot use are hidden), page headers, and "New department / New team / Add member / Add mailbox / New response" buttons that open dialogs instead of permanently visible forms. Personal responses under the account menu (`/responses`).
- **Live indicator** in the navigation: connecting / live / reconnecting / off with the polling interval.
- Keyboard shortcuts never fire while typing in a field, dialog or composer (existing guard, covered by tests); `?` lists them.
Screenshots (light and dark) for the main screens: `docs/design/agent-workspace/screenshots/` (`workspace`, `bulk-selection`, `views-dialog`, `canned-picker`, `department-admin`). Reviewed in both themes; a filter-layout and column-width problem found in the first capture was fixed before closing.

## Saved view model
Personal, owner-only (404 for anyone else). A view stores a validated structured filter (queue, department, team, assignee/unassigned, status, priority, text, sort), not SQL and not ids of tickets. Running a view is the ordinary department-scoped list (`?view=` or the stored filter in the URL), so a view can never return more than the caller could list anyway. Views can be pinned: pinned views appear under the core navigation. Limit 50 per user, unique names, optimistic concurrency. The UI keeps the view id in the URL so reload and sharing the link within the same account work.

## Bulk behaviour
Up to 100 tickets per request, selected from the queue (page selection or "select all in list"). Status, priority, team and assignee. Every ticket is authorised and validated on its own, in its own transaction, with the single-ticket rules; one failure never rolls back the others. The response reports updated / unchanged / failed with a per-ticket outcome (`not_found`, `forbidden`, `invalid`); the bar summarises it in words (counts and reasons). Audit: one event per changed ticket with `bulk: true`; one realtime event per ticket. The bar offers only controls the selection allows (UX only; the server decides).

## Canned response design
Two scopes: Personal (owner only) and Department (shared; managed with the new `department.canned.manage` permission held by DepartmentAdmin, readable by members who can reply or note). Plain text, title up to 120 and body up to 5000 characters, deactivate instead of delete, version-checked edits, limits of 200 per owner/department. Insert-only: the picker (filter box, Enter inserts the first match) puts the text at the cursor of the draft and focuses the box; nothing is posted, sent or applied automatically. No variable expansion in this phase (see BACKLOG).

## Search changes
`q` now covers reference (prefix), subject, requester name and email, and message text, using pg_trgm GIN indexes with escaped `ILIKE`; scoped by the same department filter as the list; keyset-paginated; internal notes searched only for callers with `tickets.notes.read`. Each hit reports `match { field, snippet }` where the snippet is plain text built only from messages the caller may read. The previous test asserting "bodies never appear in search results" was replaced by the narrower rule: bodies appear in search snippets only for callers entitled to read them, and never for anyone else (non-members, other departments, platform admin without membership).

## SignalR architecture
Hub `/hubs/tickets` in the Ticketing module (no new ADR: the design fits ADR-007 and the module rules; recorded in `ARCHITECTURE.md`). Mutations publish a content-free `ticketChanged { ticketId, departmentId, reference, kind, actorUserId, at }` **after commit**, best-effort (a publish failure is logged without content and never fails the request). Mail ingestion, attachment storage and delivery-state changes publish through the existing Mail.Contracts ticket interfaces (`NotifyTicketChangedAsync` and the delivery-state sink) implemented by Ticketing, so Mail still never references Ticketing.
Client (`src/web/src/lib/realtime.ts`): one shared connection, automatic reconnect with back-off, status exposed to the shell. A received event invalidates the affected TanStack queries (queue lists, counts, the open ticket, its messages) so data is always re-read through the authorised REST API. Polling is the fallback and the safety net: 120 s while live, 30 s otherwise. An open ticket never loses the composer draft; a remote change refreshes the data underneath and version conflicts still go through the existing reload banner.

## Realtime security model
- Group names are chosen only by the server. Clients cannot join or leave groups, pass ids, or invoke state-changing hub methods.
- `d:{department}` for departments where the user holds `tickets.read`; `n:{department}` additionally for `tickets.notes.read`; internal-note events are sent only to `n:`.
- Group membership is computed on connect, recomputed immediately when memberships/roles change (`IAccessChangeListener` from Organisation) and every 60 s; a removed member stops receiving events on the open connection.
- Payload carries no subject, body, requester or addresses. Even a mistaken delivery would leak only that a ticket id changed.
- Hub requires authentication (architecture test: every endpoint declares authorization, hub included); CSRF header on negotiate.
- Tests (`RealtimeTests`, 7): anonymous connect refused; members receive content-free events for their department only; members of other departments and users without access receive nothing; a removed member stops receiving immediately; a newly added member starts receiving without reconnecting; clients cannot join groups or call anything on the hub; mail and delivery changes are published to the department group. Note events are routed to `n:` groups by design, but every current role holds `tickets.notes.read`, so there is no test persona that is excluded from notes yet (revisit when a role without it exists).

## Accessibility results
`@axe-core/playwright` (WCAG 2.0/2.1 A and AA) run against the real stack on: ticket workspace with queue, navigation and composer; the Views dialog; department administration (overview and Teams tabs); the New department dialog. Result: **no serious or critical violations** at closing (`e2e/productivity.spec.ts`). Manual keyboard checks covered the split separator, tabs (arrow/Home/End), dialogs (focus in, Escape out, focus returned), bulk bar, picker (Enter inserts first match). Not covered: screen-reader testing with real assistive technology, high-contrast mode, 200% zoom reflow beyond the existing narrow-width layout (see limitations).

## Test results (one broad pass at the phase boundary)
| Suite | Result |
|---|---|
| `Helpdesk.Host.Tests` | 734/734 (21 new tests: views and bulk 8, canned and search 6, realtime 7). Two earlier tests were updated for the intentional Phase 4 contract change (`match` field on list items; search snippets for entitled callers) |
| `Helpdesk.ArchitectureTests` | 61/61 (hub and new endpoints are covered by the authorization-metadata test) |
| `Helpdesk.GraphSpike.Tests` | 43/43 |
| Web lint / build / vitest | 0 errors (4 warnings, see limitations) / builds / 66/66 |
| Playwright (real stack, simulator) | 7/7: agent workflow, email loop, canned insertion + modes + update, saved view pinned and persisted across reload, bulk action, realtime update from a second session, axe checks |
| Migrations (PostgreSQL 18) | all six modules down to 0 and up again on a scratch database; 4 trigram indexes created |
| Docker host image | rebuilt and running; negotiate and WebSocket verified through the Vite proxy |

## Browser validation
Real Chrome via Playwright against the rebuilt host image and Vite dev server, light and dark screenshots reviewed. The dev server proxy now forwards `/hubs` with WebSocket support.

## Performance findings
Query behaviour changed for search only (new trigram `ILIKE` over message bodies). Focused check on a scratch database with 300,000 message bodies and the same GIN trigram index: selective terms (`%printer%floor 5%`, an unknown token) use the index in about 60 ms and 0.02 ms; a very common term with a department filter and `LIMIT 51` stops early after about 10,000 rows in 9 ms. The check used a synthetic table in a throw-away database, **not** the real query through the application and not production-like concurrency; no performance claim beyond that. Indexes: trigram GIN on `tickets.subject`, `requesters.display_name`, `requesters.email_normalised`, `messages.body`; btree indexes for saved views (owner, created) and canned responses (owner, title; department, title). Cost: the message-body index grows with message volume and slows inserts slightly; revisit with real data. Queue counts are unchanged in query shape.

## Known limitations
- Search is substring matching, not ranked full-text: no stemming, no relevance order (results stay in the selected sort).
- Bulk actions cap at 100 tickets and do not undo; no bulk reply or merge.
- Canned responses have no variables/placeholders, no shared folders, no per-team scope.
- Realtime is a notification channel: a single host instance only. Multi-instance deployment needs a SignalR backplane (not built, not needed for the current hosting target).
- Split width and density are stored per browser, not per account.
- oxlint reports no warnings (the four earlier ones were fixed after Phase 4 acceptance).
- Large JS bundle (about 540 kB, 157 kB gzip): no code splitting yet.
- Accessibility is machine-checked plus keyboard review; no assistive-technology user testing.

## Remaining production gates (unchanged, not closed by Phase 4)
D-16 / ADR-011 real Microsoft 365 validation; D-08 malware scanner engine (`RequireCleanScan` stays fail-closed); D-13 Data Protection key ring persistence; D-09 retention; D-11, D-12, D-14. No new mail providers were added.

## Readiness for Phase 5
Ready. The ticket model, events and search are in place for SLA/automation work to build on (ticket events already carry `kind`). Phase 5 must not start until the user asks.
