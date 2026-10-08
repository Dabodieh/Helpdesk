# Phase 3 API contract: email, attachments, mailboxes

Extends `PHASE1_API.md` and `PHASE2_API.md` (same conventions: camelCase JSON, GUID ids, RFC 9457 problems with a `code` extension, `X-CSRF-TOKEN` on unsafe methods, `version` for concurrency, 404 for anything outside the caller's departments). Design: `docs/architecture/EMAIL_ARCHITECTURE.md`, ADR-013.

## Permission
`department.mailboxes.manage` (administrative: DepartmentAdmin; also satisfied by the platform-admin flag).

## Mailbox administration (department-scoped, `department.mailboxes.manage`)
```
Mailbox { id, departmentId, address, displayName, provider ("graph"|"fake"), isActive, isDefault, version,
          subscription { state ("None"|"Active"|"Failed"), expiresAt|null }, lastSyncAt|null, lastSyncError|null, attentionCount }
```
| Method & path | Notes |
|---|---|
| `GET /api/departments/{d}/mailboxes` | default first |
| `POST /api/departments/{d}/mailboxes` `{ address, displayName, providerRef?, isDefault? }` | 201. `address` is unique installation-wide (409 `duplicate`), because a mailbox belongs to exactly one department. The first mailbox of a department becomes its default. Creating tries to create the webhook subscription (its state is visible) and starts a first sync. `providerRef` = Graph user object id (preferred over the address in resource paths). |
| `GET /api/departments/{d}/mailboxes/{id}` | |
| `PATCH /api/departments/{d}/mailboxes/{id}` `{ version, displayName?, isActive?, isDefault? }` | at least one field; 409 `version_conflict`; only an active mailbox can be default (409 `inactive`); deactivating clears the default flag. Audited (`mailbox.*`, category `mail`); signature text is never put in the audit event. |
| `GET /api/departments/{d}/mail/attention?cursor=&limit=` | `{ items: [{ id, mailboxId, direction, state ("Failed"|"Ignored"), ignoreReason|null, occurredAt, address, subject, lastError|null, attempts, ticketId|null }], nextCursor }`, newest first (limit default 50, max 100). Nothing is dropped silently: failed and deliberately ignored mail is listed here. |
| `POST /api/departments/{d}/mail/{mailMessageId}/retry` | 204; only `Failed` mail (inbound or outbound) of this department, else 404. |

Ignore reasons: `own_address`, `auto_submitted`, `precedence_bulk`, `auto_response_suppress`, `mailing_list`, `bounce`, `draft`, `no_sender`, `sender_rate_limited`, `duplicate_internet_message_id`.

## Ticket API changes
- **Message** gains `bodyHtml|null` (sanitised, for display in a sandboxed frame only), `deliveryState` (`None`, `NotDelivered`, `Pending`, `Sent`, `Failed`), `deliveryError|null` (short code), `attachments[]` (`{ id, messageId, fileName, contentType, size, isInline, state ("Stored"|"Blocked"), blockReason|null, scanState, downloadable }`) and `source` may be `email`.
- **TicketDetail** gains `mailbox { id, address, name } | null` (the mailbox the ticket arrived through) and `participants [{ email, displayName|null }]` (CC).
- `POST /api/tickets/{ticket}/messages` (Public): `deliveryState` is `Pending` when an active department mailbox exists (the reply is queued in the same transaction), `NotDelivered` when none does. Only `Sent` means the provider accepted the message.
- `POST /api/tickets/{ticket}/messages/{messageId}/retry-delivery` (baseline `tickets.read`, requires `tickets.reply`): 200 `Message` (`Pending`); 409 `invalid_state` unless the delivery is `Failed`.
- `GET /api/tickets/{ticket}/attachments/{attachmentId}` (`tickets.read`): file download, always `Content-Disposition: attachment`, `nosniff`, `no-store`. 404 for another ticket/department or unknown id; 409 `invalid_state` for blocked items or items awaiting a required malware scan.
- Reply recipients: To = the ticket requester, Cc = ticket participants. Sender identity = the ticket's own stored mailbox, and only when it is active. There is NO fallback: a ticket without a mailbox, or whose mailbox is inactive or unavailable, records the reply as `NotDelivered` (`no_ticket_mailbox` / `mailbox_unavailable`). The department default is chosen once, when the ticket is created; a mailbox-less ticket needs an explicit assignment (ADR-026).

## Audit / activity actions
Ticketing: `ticket.created` (next also `source: "email"`, `mailboxId` for mail), `ticket.message_added`, `ticket.status_changed` (reason `requester_reply` for D-15 reopen, system actor), **`ticket.message_delivery_changed`** (previous/next `{ messageId, delivery }`, next `error` short code). Mail (category `mail`, department-scoped, never with subjects, bodies or correspondents' addresses): `mailbox.created|updated|activated|deactivated`, `mail.ticket_created`, `mail.attached`, `mail.ignored` (`reason`), `mail.failed`, `mail.retried`, `mail.sent`, `mail.send_failed`.

## Webhooks (Microsoft Graph; anonymous and CSRF-exempt by design)
| Path | Behaviour |
|---|---|
| `POST /api/mail/graph/notifications` | `?validationToken=` -> 200 `text/plain` echo (handshake). Otherwise a notification batch (<= 256 KB, <= 200 items): every item must name one of **our** subscription ids with the matching client state; accepted items are recorded (ids only) and nudge the delta sync; anything else is ignored. Always `202` with an empty body so nothing about subscriptions is revealed. 400 for malformed JSON/non-object, 413 above the limit. |
| `POST /api/mail/graph/lifecycle` | same authentication; `reauthorizationRequired` renews the subscription, `subscriptionRemoved` recreates it and syncs, `missed` syncs. |

## Development endpoints (Development/Testing with `Mail:Provider=Fake` only; platform admin)
`POST /api/dev/mail/inbound`, `POST /api/dev/mail/customer-reply`, `GET /api/dev/mail/sent?mailbox=`, `POST /api/dev/mail/dispatch`. They do not exist in any other configuration.

## Background jobs
Hangfire dashboard at `/hangfire` (read-only, platform admins only; 401/403 otherwise).
