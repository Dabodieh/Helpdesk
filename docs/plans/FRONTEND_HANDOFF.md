# Frontend hand-off: backend changes the web client must pick up

Maintained by the backend work; the frontend (Graphite redesign) owner ticks items off. Nothing here is implemented in `src/web/` by the backend work.

## Blocking: deploy-order dependency
- **`unassigned` notification type.** `GET /api/notifications` can now return items with `type: "unassigned"` (a ticket lost its assignee because its assignee's department membership was removed; sent to everyone holding `tickets.assign.any`, at most 5 per removal or role change; the reason in the ticket activity is `department_membership_removed` or `department_role_changed`). The zod enum in `src/web/src/api/notifications.ts` is strict: until `'unassigned'` is added, the whole notification list fails to parse for those users. Also add text to the map in `NotificationBell.tsx`, for example "Ticket unassigned (member removed)". The same applies to `sla_approaching` and `sla_breached` if the redesign replaced those files: all six types are `assigned`, `requester_reply`, `followed_update`, `mentioned`, `sla_approaching`, `sla_breached`, `unassigned`. **Do not deploy the backend to an environment with real users before the client accepts all seven.**

## Needed for features already in the backend
- **Canned responses:** insert via `GET /api/tickets/{ticket}/canned-responses/{id}/render` (placeholders filled), not the raw body from the list; warn when `emptyPlaceholders` is not empty; placeholder hints from `GET /api/canned-responses/placeholders`. Saving a body with an unknown `{{placeholder}}` returns 400 on `body`.
- **Bulk operations:** `POST /api/tickets/bulk` accepts `versions` (the versions the agent saw); show per-ticket outcomes, especially `changed` (modified since selected, not applied) and `forbidden`.
- **Attachments are disabled in production** (`Attachments:Enabled=false`): hide attach buttons, drop zones and download links when attachments are disabled. The server has no flag for the client yet (a read-only "attachments enabled" value in an authenticated config/profile response is needed first; endpoint undecided).
- **Sender mailbox row** ("Replies sent from") is shown only to users with `department.mailboxes.manage`; keep that behaviour in the redesigned ticket properties rail.
- **Department volume report:** `GET /api/departments/{id}/reports/volume?from&to` (received and resolved counts by period and team) has no UI yet.
- **SLA alerts** arrive as in-app notifications to the assigned agent only (80% of target consumed, then breach).

## Email delivery monitoring (mailbox administrators, `department.mailboxes.manage`)
- **New:** `GET /api/departments/{id}/mail/waiting?limit` lists outbound email that is late: `{ items, total, waitingAfterMinutes }`. Each item has `reason` (`retrying` after a failed send, `overdue` queued longer than expected, `sending_stalled` claimed by a worker that never finished), `state`, `attempts`, `queuedAt`, `updatedAt`, `nextAttemptAt`, `lastError`, `category`, `nature`, and, only for callers who also hold `tickets.read` in the department, `address`, `subject`, `ticketId` (empty/null for a platform administrator without a membership).
- **New field:** the existing `GET .../mail/attention` items now carry `nature` for outbound problems: `temporary` (provider/throttling; retry normally works), `permanent` (rejected or bounced), `action_required` (mailbox consent or configuration), `uncertain` (may already have been delivered; resend needs the explicit duplicate-risk confirmation), `unknown`. Use it for wording and for which action to offer.
- Manual retry of a failed send (`POST .../mail/{id}/retry`) is now audited (`mail.failed_retried`); no API change.
