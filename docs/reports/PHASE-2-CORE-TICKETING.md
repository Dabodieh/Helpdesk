# Phase 2 report: core ticketing

Status: **complete with noted limitations.** Phase 3 has not been started.

## What was built
**Agent workspace (the primary product surface).** Ordinary agents land on *My Queue*. Navigation: My Queue, Team Queue, Unassigned, All Tickets (with department/team/assignee/status/priority filters and sort), Search, New ticket; Administration is a separate area under `/admin`, shown only to users who can administer something. Desktop is a split workspace (queue list left, ticket right) with direct URLs (`/tickets/IT-1042`, `/queue/my/IT-1042`) that survive a hard reload; narrow screens split into list and ticket views. The queue is a dense, server-paginated table (reference, subject, requester, team, assignee, status, priority, age). The ticket pane has an editable subject, status/priority/team/assignee controls, a requester panel (name, email, open/total counts in the same department), a chronological conversation, a History tab written as sentences, and a composer with an explicit Public reply / Internal note mode.

**Ticketing backend** (`Helpdesk.Modules.Ticketing`, schema `ticketing`): department-scoped requesters; tickets with immutable human references (`IT-1042`) from a transactional per-department counter; messages (public vs internal); assignment to team and agent; status Open/Pending/Resolved/Closed; priority Low/Normal/High/Urgent; queues (my, team, unassigned, all) with keyset pagination, filters, sorts, queue counts and text search; activity timeline from the audit stream; `ITicketIngress` for Phase 3 late replies. Design: `docs/architecture/TICKETING.md`, `docs/api/PHASE2_API.md`, ADR-012.

**Cross-cutting:** structured problem `code` (version_conflict, duplicate, last_admin, inactive, invalid_state, csrf_invalid) replaces the frontend's message-text matching; API security headers; SeaweedFS dev object store (opt-in compose profile).

## Key UX decisions
Queue first, administration demoted. Linear-style speed and keyboard (j/k, Enter, r, n, /, c, ?, Esc; never while typing; no destructive shortcuts) with Freshdesk/Halo-style density; an original visual language with tokens, light and dark by system preference, visible focus, reduced motion respected. Internal notes differ from public replies by lock icon, an "INTERNAL NOTE / Not visible to the requester" label, a dashed border and a hatched background (never colour alone); the composer mode is a labelled toggle with a banner. Agent public replies are labelled "Recorded, not emailed (email delivery is not enabled yet)": nothing claims a message was sent. A closed ticket disables Public reply but still allows notes. A version conflict shows a "someone else changed this ticket" banner with Reload latest and keeps the draft. History is a tab rather than interleaved. Screenshots: `docs/design/agent-workspace/screenshots/` (workspace dark and light, history, administration).

## Schema and domain changes
New schema `ticketing`: `requesters` (unique per department by normalised email), `department_counters`, `tickets`, `messages`. Integrity in the database: composite FKs keep the team and the assignee inside the ticket's department (`ON DELETE SET NULL (assignee_user_id)`), messages carry the ticket's department, CHECKs enforce status timestamps, reference format, and "an internal note can never be authored by a requester or carry a delivery state". Priority is stored as smallint for ordering. Audit gained the `ticketing` category and an `(object_id, occurred_at, id)` index. Seven new content permissions (`tickets.create/reply/note/notes.read/update/assign.self/assign.any`); the Phase 1 probe endpoint was removed. Indexing choices are in the migration and TICKETING.md.

**References:** the department key is the prefix (existing, immutable, validated, unique). Numbering uses `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` inside the creation transaction (verified with 30 parallel creates giving gap-free 1..30, and independent departments). References never change when a department is renamed; cross-department move is not implemented and its alias rule is defined in TICKETING.md.

**D-15 resolved:** a requester reply on a Closed ticket reopens the same ticket (also Resolved and Pending), audited with a system actor. **D-17 resolved:** SeaweedFS for local S3. D-04 (row-level security) not adopted.

## Security guarantees tested
`docs/security/PHASE2_THREAT_MODEL.md` and SECURITY.md list them with the proving tests. Summary: every ticket endpoint against nine personas returns the contract status and changes nothing when denied; all 404s are byte-identical to a random-id 404 (no existence oracle by body, header, count, search, cursor or requester lookup); internal notes only reach callers with `tickets.notes.read` and are absent from lists, search, activity, audit rows and logs; invalid team/assignee are refused (service and database); queues, filters, search, cursors and counts never include foreign tickets; markup is stored verbatim; mass assignment, department-id tampering and missing CSRF are neutralised; revocation is immediate; concurrent writers give exactly one success. QA found two low-to-medium defects (NUL characters caused a 500; API had no security headers); both are fixed and covered by tests. No cross-department leak was found.

## Test results
Run in a .NET 10 SDK container against PostgreSQL 18, one broad pass at the phase boundary:

| Suite | Result |
|---|---|
| `Helpdesk.Host.Tests` | 597/597 (includes 51 ticketing behaviour tests and 225 ticket isolation cases) |
| `Helpdesk.ArchitectureTests` | 39/39 (module DAG, boundaries, endpoint authorization metadata, scope-bypass call sites) |
| `Helpdesk.GraphSpike.Tests` | 43/43 |
| Web vitest / lint / build | 53/53, clean, builds |
| Release solution build | 0 warnings, 0 errors |
| Docker host image build | succeeds; API security headers confirmed on the built image |
| Migrations on PostgreSQL 18 | all four modules down to 0 and up again on a scratch database; 15 tables afterwards |

Not asserted by a test: absence of N+1 queries (by design one batch lookup per kind per page).

## Browser validation
Playwright (`src/web/e2e`) passes the critical flow against the real stack (host image + PostgreSQL 18 + Vite): dev sign-in, lands on My Queue, opens a ticket, reads the conversation, changes priority and status, changes team and assignee (the ticket leaves My Queue), adds an internal note, adds a public reply (shows the not-emailed label), opens History, then moves to the other ticket with the keyboard. It ran with `E2E_BROWSER_CHANNEL=chrome` (installed Chrome) because the Playwright Chromium download timed out on this network. I also drove the UI manually against a seeded dataset (`scripts/dev-seed.mjs`) in dark and light themes. No automated accessibility audit (axe) was run, and there is only one e2e flow.

## Performance check
With 300,000 tickets inserted into the dev database (since removed), every queue, the queue-counts call, sorted and paged lists and text search answered within roughly 10 to 60 ms above this Docker setup's ~0.2 s round-trip baseline (identical for `/health/live`). Search is a substring match over the already-scoped set; real full-text search is Phase 4.

## Known limitations
- Messages are append-only by convention, not by database grants; removing a department membership silently unassigns that member's tickets with no ticket event.
- (Hardened after Phase 2) `assign.self` claims unassigned tickets only; taking another agent's ticket needs `assign.any`. Posting a message bumps the ticket version, so a stale editor gets a 409.
- No realtime: workspaces poll (30 s) and rely on version conflicts; SignalR is Phase 4. No optimistic UI, resizable split, saved views, multi-select priority filter or bulk actions.
- Requesters are minimal (no editing, merging or portal identity). Plain-text bodies only; no attachments.
- Phase 3 must sanitise CR/LF in subjects and angle brackets in requester emails before using them in mail headers.
- One intermediate commit contains an unfinished QA test helper that the next commit completed.
- Real Entra tenant sign-in and the Graph validation checklist remain untested (unchanged from Phase 1).

## Outstanding decisions
D-16 (close the ADR-011 real-tenant validation gate; blocks Phase 3 production email, not Phase 3 development against mocks), D-08, D-09, D-11, D-12, D-13, D-14; D-04 re-evaluated before production hardening.

## Readiness for Phase 3
Ready to start development. `ITicketIngress` is the requester-message and reopen entry point; messages already have `source` and `delivery_state` that Phase 3 extends (`Pending`, `Sent`, `Failed`); requesters are department-scoped and keyed by normalised email; the audit/history stream, scope filter and isolation harness are in place. Blockers for **production** email: executing `docs/guides/GRAPH_TENANT_VALIDATION.md` against an approved test mailbox (D-16), persisting the Data Protection key ring (D-13), and the attachment scanning choice (D-08). Phase 3 must keep the rule that a ticket reference in a subject is a hint only.
