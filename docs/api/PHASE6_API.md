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
