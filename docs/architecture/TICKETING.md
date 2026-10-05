# Ticketing domain (Phase 2)

Owner: Principal Engineer. Module `Helpdesk.Modules.Ticketing`, PostgreSQL schema `ticketing`, ADR-012. API contract: `docs/api/PHASE2_API.md`. Permissions: `PERMISSIONS.md`. Email threading policy: `EMAIL_ARCHITECTURE.md`.

## Aggregates and tables
| Table | Purpose |
|---|---|
| `ticketing.requesters` | Department-scoped person who raises tickets. `(department_id, email_normalised)` unique. Not a `User`. |
| `ticketing.department_counters` | One row per department: `next_number`. The only source of ticket numbers. |
| `ticketing.tickets` | The ticket aggregate root. |
| `ticketing.messages` | Append-only conversation entries (public messages and internal notes). |

History is **not** a separate table: ticket activity is the immutable `audit.audit_events` stream (category `ticketing`, `object_type = ticket`, `object_id = ticket id`) presented through `IAuditReader` (Audit.Contracts). One authoritative history; the UI renders it as a human timeline.

### Ticket
`id` (uuid v7, internal, immutable) · `department_id` · `number` (int, per department) · `reference` (text, `KEY-number`, unique, **immutable**) · `requester_id` · `subject` · `status` · `priority` · `team_id?` · `assignee_user_id?` · `created_by_user_id?` (null = system/ingestion) · `created_at` · `updated_at` · `resolved_at?` · `closed_at?` · `version` (xmin, opaque API `version`).
No speculative columns (no SLA, category, tags, due date, source/mailbox: Phase 3+/4+/5).

### Message
`id` · `ticket_id` · `department_id` (denormalised, composite FK with ticket) · `kind` (`Public`|`Internal`) · `author_type` (`Agent`|`Requester`|`System`) · `author_user_id?` · `author_requester_id?` · `body` (plain text) · `source` (`web`|`manual`; `email` arrives in Phase 3) · `delivery_state` (`None`|`NotDelivered`) · `created_at`. Never updated or deleted.
Mail transport data (Graph ids, internetMessageId, headers) belongs to the Phase 3 mail module and links to a message by id; it never lives on these tables.

## Invariants (service **and** database)
1. `reference` = department key + `-` + number; unique; never recalculated. The department key is immutable (DB trigger from Phase 1); renaming a department never changes references. The prefix **is** `departments.key` (`^[A-Z][A-Z0-9]{1,9}$`, validated server-side, unique case-insensitively): no second prefix concept. The key is not the department's identity (uuid is).
2. Numbering is allocated in the ticket-creation transaction by `INSERT ... ON CONFLICT (department_id) DO UPDATE SET next_number = next_number + 1 RETURNING` on `department_counters`. The row lock serialises concurrent creators per department; a rolled-back creation rolls the number back, so numbers are gap-free per department. Never `MAX(number)+1`. Unique `(department_id, number)` is the backstop.
3. Team consistency: composite FK `(team_id, department_id) -> organisation.teams(id, department_id)`; an IT ticket cannot name an HR team. Service also requires the team to be active when assigned.
4. Assignee consistency: composite FK `(assignee_user_id, department_id) -> organisation.department_memberships(user_id, department_id)`; the assignee must hold a membership in the ticket's department. The service additionally requires a role that can work tickets (holds `tickets.update`: Agent, TeamLead, DepartmentAdmin) and an active user. Removing a department membership nulls the assignee (`ON DELETE SET NULL (assignee_user_id)`); that silent unassignment is a known limitation (no ticket event is written because Organisation cannot call Ticketing).
5. Messages: `(ticket_id, department_id)` composite FK; `kind = 'Internal'` requires `author_type <> 'Requester'` **and** `delivery_state = 'None'` (CHECK). An internal note can therefore never be authored by, or addressed to, a requester, and can never carry a delivery state.
6. Status timestamps: entering `Resolved` sets `resolved_at`; entering `Closed` sets `closed_at` (and `resolved_at` if empty); returning to `Open`/`Pending` clears both. (CHECK: `closed_at` implies status `Closed`.)
7. Every state change and its audit event commit in one transaction (`IAuditWriter` on the Ticketing DbContext).
8. A ticket never changes department in Phase 2. If cross-department move is added later: the original reference stays on the ticket permanently as an alias, the ticket receives a new reference from the target department's counter, both are searchable, and the move is audited in source and target (`tickets.move` + target `tickets.create`). Not implemented.

## Status semantics
| Status | Meaning |
|---|---|
| Open | Agent action is required. |
| Pending | Waiting for the requester, a third party or another dependency. |
| Resolved | Agent believes it is fixed; not yet final. |
| Closed | Complete. |
Any agent with `tickets.update` may move a ticket between any two statuses (no workflow engine in v1). Auto-close timers belong to Phase 5.

## Late replies — D-15 (decided)
A requester **public** message added through `ITicketIngress.AddRequesterMessageAsync` (Ticketing.Contracts, used by Phase 3 mail ingestion, never exposed over HTTP):
- Closed -> reopen **the same ticket**: status `Open`, same department, reference, team and assignee (team/assignee are retained by construction; the FKs keep them valid), `closed_at`/`resolved_at` cleared, audit `ticket.status_changed` with `reason: requester_reply`, system actor.
- Resolved or Pending -> status `Open` the same way (the requester has answered; agent action is required).
- Open -> stays Open.
A new follow-up ticket is **not** created by default. A future per-department setting may change this.
Agents recording a requester message by hand is not a Phase 2 feature.
Agent public replies on a Closed ticket are refused (409 `invalid_state`: reopen first); internal notes are allowed on any status.

## Public reply truthfulness (Phase 2)
There is no mail transport. A public agent reply is stored as `kind=Public, author_type=Agent, delivery_state=NotDelivered`, which the UI must label "Recorded, not emailed (email delivery is not enabled)". No state implies sending. Phase 3 adds `Pending`/`Sent`/`Failed` and a transport abstraction; Graph delivery is Phase 3.

## Permissions (all *content* kind: membership only, never the platform-admin flag)
`tickets.read`, `tickets.create`, `tickets.reply`, `tickets.note`, `tickets.notes.read`, `tickets.update`, `tickets.assign.self`, `tickets.assign.any`.
| Role | Permissions |
|---|---|
| Viewer | read, notes.read |
| Agent | + create, reply, note, update, assign.self |
| TeamLead | + assign.any |
| DepartmentAdmin | all of the above |
`tickets.notes.read` is separate from `tickets.read` so internal notes have their own explicit gate: the messages query excludes `Internal` rows for callers without it, in the query itself.

## Department scope (defence in depth, PERMISSIONS rule 4)
`TicketingDbContext` applies a global query filter to tickets, messages and requesters: `department_id IN (scope)` where the scope is the caller's accessible departments for `tickets.read`, loaded from `IAuthorizer` once per request into a scoped `IDepartmentScope`. **Fail closed:** if the scope was never loaded the filter matches nothing. Bypass is explicit and named (`IgnoreDepartmentScope()`), used only by number allocation, ingestion (system principal with department from the mailbox) and tests, and covered by an architecture test that lists its call sites. Ticket-id routes resolve the ticket through the filtered set, so "does not exist" and "no access" are the same 404; the specific action permission is then checked with `IAuthorizer` against the ticket's persisted department (403 for members lacking it). PostgreSQL row-level security (D-04) stays deferred: the filter + composite FKs + isolation matrix are the Phase 2 layers.

## Queues
Queues are filtered views over `tickets`, never separate stores. `my` = assignee is the caller; `team` = ticket team is one of the caller's teams (team memberships, in departments where they hold `tickets.read`); `unassigned` = no assignee; `all` = every ticket in accessible departments (optionally one department). Lists use keyset pagination on `(sort key, id)`; no totals except `queue-counts` (Open tickets, accessible departments only). Batch lookups resolve team and assignee names (two queries per page, no N+1).

## Indexing intent (document final choices in the migration)
- `tickets (department_id, status, updated_at DESC, id DESC)` general queue and department filter.
- Partial `tickets (assignee_user_id, updated_at DESC, id DESC) WHERE assignee_user_id IS NOT NULL` for My Queue.
- `tickets (team_id, status, updated_at DESC, id DESC) WHERE team_id IS NOT NULL` for Team Queue.
- Partial `tickets (department_id, updated_at DESC, id DESC) WHERE assignee_user_id IS NULL` for Unassigned.
- `tickets (reference)` unique; `tickets (requester_id, created_at DESC)`.
- `messages (ticket_id, created_at DESC, id DESC)`.
- `audit.audit_events (object_id, occurred_at DESC, id DESC)` for ticket activity.
- Text search in Phase 2 is `reference` prefix or subject substring on the already-scoped set; real full-text search is Phase 4.

## Realtime
SignalR is deferred to Phase 4 (plan unchanged). Phase 2 workspaces poll with TanStack Query (focus refetch + short interval for the open ticket and queue counts) and rely on `version` conflicts to avoid silent overwrites. The mutation response always returns the new ticket state so a future push channel can reuse the same payload.

## Out of scope
Attachments, email transport, SLA, automation, tags/categories, merge/link, bulk actions, saved views, rich text, requester portal, cross-department move.
