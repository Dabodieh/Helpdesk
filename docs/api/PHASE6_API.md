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
