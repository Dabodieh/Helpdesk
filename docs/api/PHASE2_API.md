# Phase 2 API contract: ticketing (shared by backend, frontend, QA)

Owner: Principal Engineer; changes need Principal approval. Extends `PHASE1_API.md` (same conventions: JSON camelCase, GUID ids, ISO-8601 UTC, RFC 9457 problem details, `X-CSRF-TOKEN` on unsafe methods, `version` for optimistic concurrency). Domain rules: `docs/architecture/TICKETING.md`.

## Structured errors (all modules, Phase 1 endpoints included)
Problem details carry an extension `code`. Clients branch on `code`, never on `detail`/`title` text.
| `code` | Status | Meaning |
|---|---|---|
| `version_conflict` | 409 | `version` mismatch / concurrent update. The client reloads. |
| `duplicate` | 409 | uniqueness conflict |
| `last_admin` | 409 | last DepartmentAdmin / platform admin rule |
| `inactive` | 409 | parent/user/team is inactive |
| `invalid_state` | 409 | operation not allowed in the current state (e.g. public reply on a Closed ticket, assignee not eligible, team of another department) |
| `conflict` | 409 | any other conflict |
| `csrf_invalid` | 400 | missing/invalid CSRF token: fetch `GET /api/csrf` again and retry once |
Validation 400s keep the `errors` map. 401/403/404 follow Phase 1 (404 = missing **or** no access, indistinguishable).

## Enumerations (case-insensitive on input, PascalCase on output)
Status: `Open`, `Pending`, `Resolved`, `Closed`. Priority: `Low`, `Normal`, `High`, `Urgent`. Message kind: `Public`, `Internal`. Author type: `Agent`, `Requester`, `System`. Delivery state: `None`, `NotDelivered`. Message source: `web`, `manual`.

## Permissions (listed in `/api/me` `memberships[].permissions`)
`tickets.read`, `tickets.create`, `tickets.reply`, `tickets.note`, `tickets.notes.read`, `tickets.update`, `tickets.assign.self`, `tickets.assign.any` (roles: see TICKETING.md). A platform admin without a membership holds none of them (404 on every ticket route).

## Shapes
```
Ref        { id, displayName }
TicketListItem {
  id, reference, departmentId, departmentKey, subject, status, priority,
  requester { id, displayName, email }, team { id, name } | null, assignee { id, displayName } | null,
  createdAt, updatedAt }
TicketDetail = TicketListItem + {
  departmentName, resolvedAt|null, closedAt|null, version,
  requester { id, displayName, email, openTicketCount, totalTicketCount }   // counts: this requester's tickets in THIS department only
}
Message { id, ticketId, kind, authorType, author { id|null, displayName }, body, source, deliveryState, createdAt }
Activity { id, occurredAt, actor { id, displayName } | null, action, previous, next, reason|null }   // actor null = system
Page<T> { items: T[], nextCursor: string | null }
```
`previous`/`next` are small objects (see audit actions). Names in `team`/`assignee`/`actor` are current display names (actor: snapshot at write time).

## Endpoints
| Method & path | Permission | Notes |
|---|---|---|
| `POST /api/departments/{departmentId}/tickets` | `tickets.create` | Body `{ subject, description, requester: { email, displayName? } \| requesterId, priority?, teamId?, assigneeUserId? }`. Creates the requester if the normalised email is new in this department; creates ticket (`Open`, priority default `Normal`) and first message (`Public`, author `Requester`, source `manual`, delivery `None`). `teamId` / `assigneeUserId` need `tickets.assign.any` (an agent holding only `assign.self` may set `assigneeUserId` to themself). 201 `TicketDetail`, `Location: /api/tickets/{id}`. 409 `invalid_state` for a team of another department, an inactive team, or an ineligible assignee. |
| `GET /api/departments/{departmentId}/requesters/lookup?q=` | `tickets.create` | `q` min 2, max 10 results `{ id, displayName, email }`, this department only. |
| `GET /api/tickets` | authenticated; scoped in query | List, see below. |
| `GET /api/tickets/queue-counts` | authenticated; scoped in query | `{ my, team, unassigned, all }`: counts of **Open** tickets in accessible departments. |
| `GET /api/tickets/{ticket}` | `tickets.read` (on the ticket's department) | `{ticket}` is the GUID **or** the reference (`IT-1042`, case-insensitive). 404 when missing or inaccessible. |
| `PATCH /api/tickets/{ticket}` | `tickets.update` | `{ version, subject?, status?, priority? }`, at least one. Returns `TicketDetail`. |
| `PUT /api/tickets/{ticket}/assignment` | `tickets.assign.any`, or `tickets.assign.self` when `assigneeUserId` is the caller and the ticket is unassigned or already theirs (claim / keep), or null while the caller is the current assignee (release), and `teamId` equals the current team | `{ version, teamId: guid\|null, assigneeUserId: guid\|null }` replaces both. Returns `TicketDetail`. Team/assignee validation as for create. |
| `GET /api/tickets/{ticket}/messages?cursor=&limit=` | `tickets.read` | Newest first, `limit` default 100 (max 200). `Internal` rows are excluded unless the caller holds `tickets.notes.read`. |
| `POST /api/tickets/{ticket}/messages` | `tickets.reply` (kind `Public`) / `tickets.note` (kind `Internal`) | `{ kind, body, status? }`; `status` additionally needs `tickets.update` and is applied in the same transaction. Public: author `Agent`, delivery `NotDelivered`, refused with 409 `invalid_state` on a Closed ticket. Returns 201 `{ message: Message, ticket: TicketDetail }`. |
| `GET /api/tickets/{ticket}/activity?cursor=&limit=` | `tickets.read` | Newest first, `limit` default 50 (max 200), from the audit stream. Includes message events (`ticket.message_added`, no body) so clients can interleave; clients should not show them twice. |

Messages are not version-guarded (no `version` in the request), but posting one changes the ticket and therefore its `version`: an editor holding the previous version gets 409 `version_conflict` and reloads. `assign.self` lets an agent claim an unassigned ticket, keep one they hold, or release one they hold; taking a ticket assigned to another agent is a reassignment and needs `assign.any`.

Authorization order for ticket-id routes: resolve the ticket through the department-scoped set (not found / no `tickets.read` = 404), then check the action permission against the ticket's persisted department (member lacking it = 403). The department id is never taken from the body or query.

### `GET /api/tickets` query
`queue=my|team|unassigned|all` (default `all`), `departmentId`, `status` (repeatable; default = all except `Closed`), `priority` (repeatable), `teamId`, `assigneeUserId`, `unassigned=true`, `q` (reference prefix or subject substring, 2..100 chars, wildcards escaped), `sort=updated_desc` (default) `| updated_asc | created_desc | created_asc | priority_desc` (priority_desc = Urgent first, then `updated_desc`), `cursor`, `limit` (default 50, max 100). Returns `Page<TicketListItem>`. Invalid enum/cursor/limit = 400. Filters only narrow the caller's scope; an inaccessible `departmentId`, `teamId` or ticket yields an empty page, never an error that distinguishes it from an empty result.
`queue` semantics are in TICKETING.md ("Queues"); `team` uses the caller's team memberships.

## Audit/activity actions (category `ticketing`, object type `ticket` unless noted)
`ticket.created` (next: reference, status, priority, teamId, assigneeUserId) · `ticket.subject_changed` (previous/next subject) · `ticket.status_changed` (previous/next status, optional `reason`: `requester_reply`) · `ticket.priority_changed` · `ticket.team_changed` (previous/next team id) · `ticket.assignee_changed` (previous/next user id) · `ticket.message_added` (next: messageId, kind, authorType; **never the body**) · `requester.created` (object type `requester`, department-scoped, not shown in ticket activity). Actor null with `source: system` for ingestion-driven changes.
Payload keys (exact): `status_changed` {status}; `priority_changed` {priority}; `subject_changed` {subject}; `team_changed` {teamId} (null when unset); `assignee_changed` {assigneeUserId} (null when unset); `created` next {reference, status, priority, teamId, assigneeUserId}; `message_added` next {messageId, kind, authorType}. Activity `reason` is read from `next.reason` (e.g. `requester_reply`); `previous`/`next` are returned as stored, including `reason`.

## Removed
`GET /api/departments/{id}/ticket-access-probe` (Phase 1 probe) is deleted; its tests move to the real ticket endpoints.
