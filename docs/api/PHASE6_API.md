# Phase 6 API

All portal endpoints require authentication and are scoped to the caller's requester links. Inaccessible objects return 404.

## Portal
- `GET /api/portal/forms`, `GET /api/portal/forms/{formId}`: published request forms with requester-visible fields.
- `GET /api/portal/requests?filter=open|closed|all&cursor=&limit=`: caller's requests, ordered by `lastPublicActivityAt` desc.
- `POST /api/portal/requests`: `{formId, subject, description, fields: {key: value}}`; creates a ticket (source `portal`). 409 if the address belongs to another user's claimed requester.
- `GET /api/portal/requests/{reference}`: public conversation only, requester status (In progress, Waiting, Resolved, Closed).
- `POST /api/portal/requests/{reference}/replies`: JSON `{body}` or multipart with files.
- `GET /api/portal/requests/{reference}/attachments/{id}`: public attachments only.

## Portal knowledge
- `GET /api/portal/knowledge/categories`, `GET /api/portal/knowledge/articles?q=&category=&cursor=`, `GET /api/portal/knowledge/articles/{slug}`, `POST /api/portal/knowledge/articles/{slug}/feedback` `{helpful}`.

## Administration (department scoped)
- `/api/departments/{d}/request-forms` (`department.forms.manage`): list, create, patch, order; `/{formId}/fields` create, patch, order.
- `/api/departments/{d}/knowledge/categories|articles` (`department.knowledge.manage`): list, create, patch, order; articles `publish`, `archive`. Updates carry the `version` for optimistic concurrency.

## Realtime
Hub group `r:{requesterId}`; event `requestChanged {reference, kind: reply|status|attachment, at}`.

## Agent requester history (refinement 1)
- `GET /api/requesters/{id}`: profile (display name, email, first seen, last visible activity, open/resolved/total counts) plus up to 5 open and 3 previous tickets.
- `GET /api/requesters/{id}/tickets?state=active|resolved|all&cursor=&limit=`: keyset-paged (max 50) by visible `updatedAt` desc. Open means Open or Pending; resolved means Resolved or Closed.
- A requester row exists per department (D-05), so a person is the set of rows sharing the normalised email. Everything runs through the department-scope filter: tickets in departments the caller cannot read are absent from lists, counts, last-activity and paging, and a requester row in an unreadable department is a 404. Internal notes never move `updatedAt`. No audit event for reads. The ticket header counts (`openTicketCount`, `totalTicketCount`) use the same scoped, per-person rule.
- UI: requester name in the ticket header and queue opens a drawer; `View full history` goes to `/requesters/{id}`.

## Ticket types and fields (refinement 2)
Administration (`department.forms.manage`, department scoped; foreign department = 404):
- `GET/POST /api/departments/{d}/ticket-types`, `PATCH .../{id}` (name, description, isActive, isDefault, version), `PUT .../order`, `PUT .../{id}/fields` (`{fields:[{fieldId, required?}]}`, replaces the list, order = list order).
- `GET/POST /api/departments/{d}/ticket-fields`, `PATCH .../{id}` (key and type are immutable), `PUT .../order`. The list reports where each field is used (`types`, `forms`) and `hasValues`.
- `PUT /api/departments/{d}/request-forms/{id}/fields` replaces the form's field list; forms take `ticketTypeId`/`clearTicketType`. The per-form field endpoints of Phase 6 are gone.
Agents: `GET/PATCH /api/tickets/{ref}/properties` (type and field values), `POST .../tickets` accepts `ticketTypeId` and `fields`, ticket detail has `type`, the queue accepts `typeId`.
Requesters: `PUT /api/portal/requests/{ref}/fields` (requester-editable fields only); the portal ticket lists `editableFields`.

## Ticket merge and links (refinement 3)
All routes are ticket-id routes (`{ticket}` = id or reference); the department comes from the ticket, never from the request.
- `POST /api/tickets/{canonical}/merge` (`tickets.update`) `{version, sources:[{ticketId, version}]}` (1-20, same department): the route ticket stays, the sources become read-only. Returns the canonical ticket detail (`mergedTickets`). Errors: 404 (a source is missing or not readable, indistinguishable), `409 cross_department`, `409 ticket_merged` (canonical or a source merged elsewhere), `409 version_conflict`, 400 (empty, itself, duplicates). Nothing is partially merged. A source already merged into this canonical ticket is skipped (idempotent).
- `GET /api/tickets/{ticket}/link-candidates?q=` (`tickets.update`): up to 10 same-department tickets by reference prefix or subject (min 2 chars); never the ticket itself, merged tickets or tickets the caller cannot read. Items carry the `version` needed to merge.
- `GET /api/tickets/{ticket}/links` (`tickets.read`): `[{id, relationship, ticketId, reference, subject, status, mergedInto}]`; relationship is Related, DuplicateOf, DuplicatedBy, Parent or Child as seen from this ticket.
- `POST /api/tickets/{ticket}/links` (`tickets.update`) `{ticketId, type: Related|DuplicateOf|Parent|Child}` where type describes the selected ticket relative to this one (`Parent` = the selected ticket is the parent). `DELETE /api/tickets/{ticket}/links/{linkId}`. Errors: `409 duplicate` (pair already linked), `409 invalid_link` (circular), `409 cross_department`, `409 ticket_merged`, 400 (itself, unknown type).
- Ticket detail: `mergedInto {id, reference, subject, mergedAt, mergedBy}` on a merged ticket, `mergedTickets` on a canonical one. List items carry `mergedInto` (reference) in search and history. Messages carry `importedFrom` (reference) for entries that came from a merged ticket. Merged tickets answer every mutation with `409 ticket_merged`.
- Requester history (refinement 1) semantics: total includes merged tickets, open count and the active list exclude them, the previous/resolved list includes them.
- Portal: `GET /api/portal/requests/{ref}` of a merged request returns `{status: "merged", mergedInto: "<ref>"}` (no messages) only when the requester owns the canonical ticket, else 404; replies and edits are `409 ticket_merged`; merged requests are not listed.
- Activity: `ticket.merged_in`, `ticket.merged_into`, `ticket.link_added`, `ticket.link_removed`.

## Ticket followers (refinement 4)
- `GET /api/tickets/{ticket}/followers` (`tickets.read`): `{items:[{userId, displayName, addedAt}], following}`.
- `POST|DELETE /api/tickets/{ticket}/followers/me` (`tickets.read`): follow or unfollow as the caller; both return the follower state and are idempotent.
- `POST|DELETE /api/tickets/{ticket}/followers/{userId}` (`tickets.update`, except for your own id which is the self path): add or remove another user. Adding needs an active user who can read the ticket's department, else 400. A merged ticket answers `409 ticket_merged`.
- `GET /api/tickets/{ticket}/follower-candidates?q=` (`tickets.update`): up to 10 active department readers not following yet, matched by name or email.
- `GET /api/tickets?queue=following`, saved views with `queue: "following"`, and `following` in `GET /api/tickets/queue-counts`.
- Activity: `ticket.follower_added`, `ticket.follower_removed`.

## Macros (refinement 5)
- Administration, `department.canned.manage`, department scoped (foreign department = 404): `GET|POST /api/departments/{d}/macros`, `PATCH .../{id}` (`version`, `name`, `description`, `isActive`, `actions`), `PUT .../order` (`ids`, every macro once). A macro is `{id, name, description, isActive, order, actions:[{type, value?, text?, key?, data?}], summary[], version}`; action types `setStatus`, `setPriority`, `setTeam`, `setAssignee`, `clearAssignee`, `assignToMe`, `setType`, `setField` (`key` + `data`), `internalNote` (`text`), `publicReply` (`value` = canned response id, or `text`). Errors are keyed `actions[i]`.
- `GET /api/tickets/{ticket}/macros?q=` (`tickets.read`): active macros of the ticket's department that the caller may run, filtered by name or description; each carries a `summary` (the preview text).
- `POST /api/tickets/{ticket}/macros/{macroId}/run` `{version}` (`tickets.read`, then the permissions of the actions, else 403): `{ticket, draft:{body, cannedResponseId, title}|null, noteAdded}`. Errors: 404 (foreign or missing), `409 inactive`, `409 ticket_merged`, `409 canned_unavailable`, `409 version_conflict`, 400 (invalid final state), 403. Nothing is applied on any error. It never sends email.
- Activity: `ticket.macro_run`; macro administration is audited as `macro.created` and `macro.updated`.
