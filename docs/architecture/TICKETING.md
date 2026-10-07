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

## Public reply truthfulness (Phase 2, extended in Phase 3)
An agent public reply is `kind=Public, author_type=Agent` with a delivery state: `NotDelivered` when the department has no active mailbox (recorded only), `Pending` once queued in the mail outbox (same transaction as the message), `Sent` only after the provider accepted the send, `Failed` (short error code, visible and retryable) otherwise. Notes and requester messages are always `None` (database CHECK). The UI labels each state; nothing claims a send that did not happen. Phase 3 additions to the schema: `messages.source` may be `email`, `messages.source_ref` (unique per department+source: producer idempotency), `messages.body_html` (sanitised email HTML), `messages.delivery_error`; `tickets.mailbox_id` (composite FK with the department); `ticket_participants` (CC from email, copied on replies). Email enters only through the mail sink (`IInboundTicketSink`), the same D-15 rules apply (a requester reply reopens the same ticket).

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

## Indexing (final choices, as in the `Initial` migration)
- `ux_tickets_reference` (unique) and `ux_tickets_department_number` (unique): reference lookup (`{ticket}` accepts `IT-1042`) and the numbering backstop.
- `ix_tickets_department_status_updated (department_id, status, updated_at DESC, id DESC)`: general queue, department filter, queue-counts.
- Partial `ix_tickets_assignee_updated (assignee_user_id, updated_at DESC, id DESC) WHERE assignee_user_id IS NOT NULL`: My Queue; also serves the `SET NULL` of the assignee FK.
- Partial `ix_tickets_team_status_updated (team_id, status, updated_at DESC, id DESC) WHERE team_id IS NOT NULL`: Team Queue.
- Partial `ix_tickets_unassigned_updated (department_id, updated_at DESC, id DESC) WHERE assignee_user_id IS NULL`: Unassigned.
- `ix_tickets_requester_created (requester_id, created_at DESC)`: requester counts and history. `ux_requesters_department_email`: requester upsert by normalised email.
- `ix_messages_ticket_created (ticket_id, created_at DESC, id DESC)`: message list newest first.
- `audit.ix_audit_events_object_time (object_id, occurred_at DESC, id DESC)` (Audit migration `TicketingSupport`): ticket activity.
- EF also generates two FK indexes (`IX_messages_ticket_id_department_id`, `IX_tickets_requester_id_department_id`); kept as generated.
- `priority_desc` and `created_*` sorts run over the scoped set without a dedicated index (not hot; revisit with real data). Priority is stored as `smallint` (1 Low .. 4 Urgent) so the sort is a plain column order.
- Search (Phase 4): `q` matches reference prefix, subject, requester name/email and message text with escaped `ILIKE` on the already-scoped set, backed by pg_trgm GIN indexes (`ix_tickets_subject_trgm`, `ix_requesters_name_trgm`, `ix_requesters_email_trgm`, `ix_messages_body_trgm`). Internal notes are searched only with `tickets.notes.read`. Hits carry `match { field, snippet }`, a plain-text snippet from messages the caller may read. Not ranked full-text search; see the Phase 4 report for the scale check.
- Saved views (`ix_saved_views_owner`) and canned responses (`ix_canned_owner`, `ix_canned_department`) are small per-owner tables.

## Realtime
Phase 4 added a SignalR hub (`/hubs/tickets`) that announces *that* a ticket changed (ids and a kind, no content); clients re-read through REST. Polling with TanStack Query remains the fallback (30 s, or 120 s while the hub is live) and `version` conflicts still prevent silent overwrites. See ARCHITECTURE.md (Phase 4 structure) and the realtime security model in the Phase 4 report.

## Phase 4 additions
- **Saved views:** personal, owner-only, structured validated filter, executed as the normal scoped list (`?view=`).
- **Bulk change:** up to 100 tickets; per-ticket transaction, authorization, validation, audit (`bulk: true`) and event; partial failure reported per ticket, never as an HTTP error.
- **Canned responses:** personal and department scope, plain text, insert-only; department scope needs `department.canned.manage`.

## Out of scope
SLA, automation, tags/categories, merge/link, rich text, requester portal, cross-department move, bulk reply, canned-response variables.

## Phase 5 additions
- **Tables:** `business_hours`, `sla_policies`, `ticket_sla` (frozen snapshot, both clocks, `next_due_at`, `next_warn_at`, `next_event_at`), `sla_events`, `automation_rules`, `automation_executions` (unique rule + ticket + trigger key). Messages gain source `automation`.
- **Clock rules:** first response = first public agent reply; resolution = Resolved or Closed; Pending pauses resolution only when the policy says so; reopening starts a new resolution cycle. Implemented only in `SlaEngine`.
- **Indexing:** partial indexes on the three summary columns; see ADR-014 and the Phase 5 report for the scale check.
- **Queue:** `sla` filter, `sla_due_asc` sort and SLA counts read `ticket_sla` (department scoped like tickets).
- **Activity:** audit entries of internal notes are hidden from callers without `tickets.notes.read`.
- **`updatedAt` semantics:** the time of the last *visible* activity (public message, status, priority, team, assignee, subject). Adding an internal note does not touch the ticket row, so it cannot be inferred from `updatedAt`, `version`, ordering or counts.

## Ticket types and ticket fields (refinement 2)
- **Ticket type** (`ticket_types`): a department classification (id, name, description, active, default, order). Never deleted; a deactivated type stays on the tickets that already have it and is readable. At most one **default** type per department (partial unique index); a type that is deactivated stops being the default. `tickets.ticket_type_id` is nullable and, like every department-scoped link, uses a composite foreign key `(ticket_type_id, department_id)`, so a ticket can never point at another department's type; the same applies to forms, type-field links and form-field links.
- **Which type a new ticket gets:** an explicit choice (agent create, must be active and of the department; foreign, missing and inactive all answer 400 with one message), else the form's type if still active (portal), else the department default, else none. Email cannot choose and never fails: it gets the default or stays untyped.
- **Ticket field** (`ticket_fields`): the one field definition per department, evolved from the Phase 6 per-form field. Stable immutable `key` and `field_type` (short text, long text, single/multi select, checkbox, date, number), `required` default, options with stable values (relabel or deactivate, never remove), `applies_to_all_types` (common field), `requester_visible`, `requester_editable` (implies visible), `agent_visible`, `is_active`. Deactivated, never deleted; its stored values remain readable.
- **Where a field is asked:** common fields for every ticket of the department, then the fields linked to the ticket's type (`ticket_type_fields`, ordered, optional requirement override). A request form asks the fields linked to the form (`request_form_fields`, ordered, optional override) and creates the form's type. Effective "required" = link override if set, else the field default.
- **Values** (`ticket_field_values`): one typed, validated row per ticket and key, with a snapshot of label, type, visibility and rendered display text, rewritten when the value is edited. Renaming a field or option or deactivating a field never changes what an existing ticket shows. Validation is server side only (`FieldValueValidator`): type, option values, length, no scripts, HTML or formulas.
- **Required semantics:** required is enforced when a ticket is created through a channel that submits fields (portal form; agent create when it submits `fields`) and when someone edits a field (a required field cannot be cleared). Making a field required, or changing a ticket's type, never invalidates existing tickets; missing required values are only reported (`missingRequired`). Email and an agent create that submits no `fields` do not enforce required.
- **Agent editing** (`GET/PATCH /api/tickets/{ref}/properties`, `tickets.read` / `tickets.update`): changes only the type and field values (optimistic `version`). A held value whose field is no longer applicable stays readable and, while the field is active, editable. Changes are audited: `ticket.type_changed`, `ticket.field_changed` (field, previous and new display; values of fields agents cannot see are never written).
- **Requesters:** portal forms, the portal ticket and requester-editable updates (`PUT /api/portal/requests/{ref}/fields`) only ever involve active, requester-visible fields; a value is shown while both its snapshot and the live definition are requester visible, so hiding a field later hides old values too. Requester field edits emit no requester realtime payload.
- **Configuration** is under `department.forms.manage` (DepartmentAdmin): Department, Ticketing (Ticket types, Ticket fields, Request forms).
- **Conditions:** `ticketType` (in / not in, a type of the same department) is available to automation rules and SLA policy matching; the queue accepts `typeId`.
- **Phase 6 data:** the migration copies each per-form field into `ticket_fields` (ids kept; a key that collides within a department gets a numeric suffix on the later field), links it to its original form (requirement kept on the field), keeps all values, and `down` restores the per-form shape (a field linked to several forms stays with the first).

## Ticket merge and ticket links (refinement 3)
**Merge** (same department only): `POST /api/tickets/{canonical}/merge`. The route ticket is the explicit canonical ticket; every source is merged into it, in one transaction with row locks in id order and the xmin `version` of every involved ticket. Nothing is rewritten or copied.
- A merged ticket is not a status. It carries `merged_into_ticket_id` (composite FK `(id, department_id)`, so a cross-department merge is impossible in the database too), `merged_at`, `merged_by_user_id` (check `ck_tickets_merge_shape`). Status, subject, requester and reference stay as they were. The canonical ticket never becomes a chain: merging a canonical ticket repoints its sources.
- Read-only: every mutation path (`LockAndLoadAsync`, properties, bulk, retry delivery, requester ingress, automation actions and rule matching) refuses with `409 ticket_merged`. Rules and creation routing are not re-run because of a merge, and a merge raises no status/Closed trigger (no change events are dispatched).
- Conversation: messages keep their own `ticket_id` (provenance). The canonical conversation is read as the union of the canonical ticket and its merged sources, with the same notes filter; each imported entry carries `importedFrom` (agent-only; the portal never reads another ticket's messages). Attachments stay on their original ticket and the canonical download route tries the canonical ticket then its sources (same authorisation and scan rules, no copies).
- Participants are not copied and the canonical requester never changes; no portal access follows a merge.
- Email: `AddMessageAsync` follows a merged ticket to its canonical ticket after Mail's existing correlation (headers/token) matched the old thread; the audit entry records the merged ticket id. A race with a concurrent merge fails and the mail job retries.
- SLA: the source's clocks stop (`Stopped` SLA event, no due dates, nothing marked met); the canonical SLA is untouched and never combined.
- Queues, queue counts and views exclude merged tickets. Search (`q`) includes them, marked `mergedInto`. Requester history counts them in the total but not in the open count, and lists them under "previous" as `Merged into IT-xxxx`.
- Audit: `ticket.merged_in` on the canonical ticket, `ticket.merged_into` on each source (references only, no content).
- Portal: a merged request leaves the list; opening it shows `mergedInto` only when the same requester also owns the canonical ticket, otherwise 404. Replies to it are refused.
- Concurrency/idempotency: stale versions are `version_conflict`; two merges of one source race on the row lock (one wins); repeating a finished merge is a harmless 200.

**Links**: `ticket_links` (department-scoped, composite FKs to both tickets, unique `(source, target)`, no self link). Types: `Related` (symmetric, smaller id first), `DuplicateOf` (source is the duplicate), `ParentOf` (source is the parent). The API speaks relative to the open ticket: Related, DuplicateOf, Parent, Child (read back also as DuplicatedBy). One link per pair of tickets; cycles of the directional types are refused (`invalid_link`); merged tickets cannot be linked; cross-department is `409 cross_department` (404 when the other ticket is invisible). Links are informational: they change neither ticket.

## Ticket followers (refinement 4)
- **Follower is not the assignee, is not a CC participant and grants no access.** A follower is an internal "keep me informed" marker (`ticket_followers`, primary key `(ticket_id, user_id)`, composite FK to the ticket's department, indexes by ticket via the key and by user via `ix_ticket_followers_user`). Requester CC participants (`ticket_participants`) are a separate, external concept; nothing here sends or copies email.
- Access always comes from the user's own department membership and permissions. The follower list shows only active users who can currently read the department's tickets; the Following queue is the normal list restricted to the caller's followed tickets inside the caller's department scope, so it can never show more than the caller can read. Following does not change note visibility (`tickets.notes.read` is still checked per call) and does not write the ticket row, so `updatedAt` and the version are untouched.
- Same department by construction: `fk_ticket_followers_membership` ties `(user_id, department_id)` to `organisation.department_memberships` with `ON DELETE CASCADE`, so removing a membership removes the follow. The service also validates the target (active, can read the department) and answers 400 otherwise. User deactivation has no hook: the follower list ignores inactive users, and stale rows are cleaned up with the membership (backlog).
- Rules: following or unfollowing yourself needs `tickets.read`; adding or removing someone else, and the candidate search, need `tickets.update`. No new permission. Writes are idempotent (`ON CONFLICT DO NOTHING`, delete of nothing is a no-op) and audited only when something changed (`ticket.follower_added`, `ticket.follower_removed`, with `self`); the realtime event is a plain `updated` ticket event (no member data) on the department group.
- Queue: `queue=following` (also in saved views and `queue-counts.following`).
- Merge: followers of the merged tickets who still are valid readers follow the canonical ticket too (duplicates collapse, existing followers stay); the merged tickets keep none and refuse follower changes (`ticket_merged`). The merge audit entry carries `followersTransferred`, with no per-follower entries. Links do not propagate followers.
- Portal: no follower data is ever returned to requesters.

## Macros (refinement 5)
**Macro = a manually triggered agent action bundle. Automation = an event-triggered rule.** They are separate: a macro never reacts to events, has no `automation_rules` row and creates no automation execution record.
- `macros` (department scoped, stable id, name unique per department, `is_active`, `sort_order`, structured `actions` JSON, no code). Deactivate instead of deleting; the ticket history keeps the name the macro had when it ran.
- Actions (each kind once per macro; setField once per field key; the three assignee actions are one group): set status, priority, team, assignee, clear assignee, assign to me, set ticket type, set field value, add internal note, prepare public reply (text or a canned response of the same department).
- Management: `department.canned.manage` (agent productivity); no new permission. Every reference (team, person, type, field, canned response) must belong to the macro's department; foreign, missing and unusable ids answer alike.
- Running (`POST /api/tickets/{t}/macros/{id}/run`, baseline `tickets.read`): the caller needs what the same manual actions need (`tickets.update` for status/priority/type/fields, the normal assignment rules for team/assignee, `tickets.note` for a note, `tickets.reply` for a reply draft). Permissions are checked for every action first; a missing one refuses the whole macro. The macro must be active and belong to the ticket's department, the ticket must not be merged (`409 ticket_merged`, no redirect), the version must match.
- Atomic: the final intended state is validated before anything is written (type, then fields against the FINAL type, team, assignee, assignment rights, canned response resolved now; an unavailable canned response is `409 canned_unavailable`). Then one transaction applies the changes through the same audit, SLA and automation hooks as manual edits (`Provenances.User`), so status changes pause or resume the SLA and event-triggered rules react exactly as they would to a manual change. Any validation failure applies nothing.
- **A Macro Public Reply action prepares a draft and never automatically sends an email.** The run returns the text (canned response content as it is at that moment) to the caller only; the web composer inserts it as an editable draft and the agent presses Send. The run never queues outbound mail and the draft is never broadcast.
- Notes: a macro note is a normal internal note (agent author, no requester-visible timestamps moved by it alone). Audit: normal entries for each real change (with `via.macroId`) plus one `ticket.macro_run` entry (macro id and its name then); it names no action kinds and no text, so it reveals nothing about a note or reply. Nothing about macros is returned by any requester API.
- Followers and links are not touched by macros in this version.
