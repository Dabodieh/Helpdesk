# ADR-028: Notification outbox written by a SaveChanges interceptor

Status: Accepted (recorded retroactively in WS-16 from the Phase 4-6 refinements; extended by ADR-023; finding ARC-006)

## Context
A change that can notify somebody (a new assignee, a status or priority change, a new public message, an @mention) must not be lost if the process stops after the change committed, and notifying must not slow or fail the change.

## Decision
- `NotificationOutboxInterceptor` (an EF `SaveChangesInterceptor`) stages a `notification_outbox` row in the SAME SaveChanges, hence the same transaction, as the qualifying mutation. A row carries ids, the event kind and (for a message) the message id; never text.
- `NotificationOutboxDrainer` claims each row, turns it into notifications with the existing recorder rules (one unread row per user and ticket, strongest reason wins; actor and permission suppression) and marks it processed in one transaction, so a replay or a second worker cannot duplicate. SignalR only sends an empty "refresh" prompt afterwards; clients fetch through the authorised API.
- It runs right after a change (fast path) and from a recurring job (durable path). ADR-023 adds attempts, parking and the age metric.

## Consequences
- Only EF-tracked mutations are seen. A path using `ExecuteUpdate` or raw SQL for a relevant field must insert its own outbox row or use tracked entities. This is not an event framework and must not grow into one.
