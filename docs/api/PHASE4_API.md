# Phase 4 API additions

Same conventions as earlier phases: cookie session, `X-CSRF-TOKEN` on mutating calls, problem+json errors with a `code` extension, department scope enforced server-side (404 for anything the caller cannot access), `version` for optimistic concurrency.

## Ticket list and search (`GET /api/tickets`)
Existing filters unchanged (`queue`, `departmentId`, `status[]`, `priority[]`, `teamId`, `assigneeUserId`, `unassigned`, `sort`, `cursor`, `limit`). Additions:

| Parameter | Meaning |
|---|---|
| `q` | Search text, 2 to 100 characters, wildcards escaped. Matches reference (prefix), subject, requester name/email and message text (trigram-indexed `ILIKE`). Internal notes are searched only when the caller holds `tickets.notes.read`. |
| `view` | Id of one of the caller's saved views: its stored filter is applied (explicit parameters override). Someone else's id: 404. |

Every list item gains `match`: `null` when there is no search text or the match was reference/subject/requester, otherwise `{ "field": "message" | "note", "snippet": "…" }`. Snippets are plain text (never HTML), at most about 160 characters, built only from messages the caller may read. The list is still keyset-paginated (`nextCursor`).

## Saved views (personal, owner-only)
Permission: any signed-in user, own views only. Another user's id returns 404.

| Call | Notes |
|---|---|
| `GET /api/views` | The caller's views, pinned first. |
| `POST /api/views` | `{ name, filter, isPinned? }`. Name 1 to 80 characters, unique per user (`duplicate`), at most 50 views (`invalid_state`). |
| `PATCH /api/views/{id}` | `{ version, name?, filter?, isPinned? }`; stale `version` gives `version_conflict`. |
| `DELETE /api/views/{id}` | 204. |

`filter` = `{ queue?, departmentId?, teamId?, assigneeUserId?, unassigned?, status[]?, priority[]?, q?, sort? }`, validated with the list endpoint's rules and stored as canonical JSON. A view never widens access: running it is the ordinary scoped list, so ids pointing at departments the caller cannot see simply return nothing.

## Bulk change (`POST /api/tickets/bulk`)
Body `{ ticketIds: [1..100 ids], status?, priority?, team?: { id | null }, assignee?: { id | null } }`; at least one change is required. Each ticket is processed in its own transaction with the same authorization and invariants as the single-ticket PATCH (`tickets.update`; team and assignee need `tickets.assign.any`, or `tickets.assign.self` for claiming unassigned tickets for yourself). Team and assignee changes require the selection to be one department.

Response `200`: `{ updated, unchanged, failed, results: [{ ticketId, reference?, outcome, code? }] }` where `outcome` is `updated`, `unchanged`, `not_found` (missing **or** not accessible: indistinguishable), `forbidden` or `invalid`. Partial failure is normal and is not an HTTP error. Each changed ticket writes its own audit event flagged `bulk: true` and publishes its own realtime event.

## Canned responses
| Call | Notes |
|---|---|
| `GET /api/canned-responses?departmentId=&includeInactive=` | Own personal responses plus the shared responses of departments where the caller can reply or add notes (or manages canned responses). Each row has `canManage` for UI hints. |
| `POST /api/canned-responses` | `{ scope: "Personal" \| "Department", departmentId?, title, body }`. Title at most 120, body at most 5000 plain-text characters. Department scope needs `department.canned.manage` in that department. Limits: 200 personal per user, 200 per department. |
| `PATCH /api/canned-responses/{id}` | `{ version, title?, body?, isActive? }`. Personal: owner only (404 otherwise). Department: `department.canned.manage` (403 for members without it, 404 for non-members). |

There is no delete: deactivate instead. Inserting a response into the composer is a client action; the API never sends anything because of it.

## Realtime (`/hubs/tickets`, SignalR)
- Authenticated with the session cookie; the negotiate call carries the CSRF header. Transport: WebSockets with automatic fallback.
- The client **cannot** join groups, subscribe to ids or invoke methods. The server places the connection into `d:{departmentId}` for every department where the user holds `tickets.read`, and additionally `n:{departmentId}` where they hold `tickets.notes.read`.
- Server-to-client message `ticketChanged`: `{ ticketId, departmentId, reference, kind, actorUserId, at }` with `kind` one of `created`, `updated`, `assignment`, `status`, `priority`, `message`, `note`, `delivery`, `attachment`. **No subject, body, name or address is ever sent**: clients re-read through the authorised REST API. `note` events go only to `n:` groups.
- Group membership is recomputed when memberships or roles change and every 60 seconds; a revoked user stops receiving events without reconnecting.

## Permission catalogue
New permission `department.canned.manage` (administrative kind, membership based; held by the DepartmentAdmin role only).

## Problem codes used
`version_conflict`, `duplicate`, `invalid_state` (limits), `validation` (field errors), as in earlier phases.
