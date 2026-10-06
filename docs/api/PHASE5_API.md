# Phase 5 API additions

Conventions as before (cookie session, `X-CSRF-TOKEN` on mutations, problem+json with `code`, `version` for concurrency). Configuration endpoints require an administrative permission of the department in the route (404 for departments the caller cannot see, 403 without the permission). `department.sla.manage` and `department.automation.manage` are held by DepartmentAdmin; a platform administrator satisfies them (administrative kind) but still has no ticket content access.

## Business hours (`department.sla.manage`)
| Call | Notes |
|---|---|
| `GET/POST /api/departments/{d}/business-hours` | At most 10 per department. |
| `GET/PATCH /api/departments/{d}/business-hours/{id}` | PATCH needs `version`. |
| `POST .../{id}/due` | `{ start, minutes }` returns `{ dueAt }`: what a target would produce from that moment (read-only explanation). |

Body: `{ name, timeZone, isActive?, weekly: [{ day, intervals: [{ start: "09:00", end: "17:00" }] }], exceptions: [{ date: "2026-12-25", name?, intervals: [] }] }`. `end` may be `24:00`. An exception with no intervals closes the whole day, otherwise it replaces that date's hours. Rules: IANA time zone, at least one weekly interval, intervals ordered and non-overlapping within a day, at most 400 exceptions, unique name per department (`duplicate`).

## SLA policies (`department.sla.manage`)
`GET/POST /api/departments/{d}/sla-policies`, `PATCH .../{id}` (with `version`), `PUT .../order` (`{ ids }`: every policy once, renumbered 10, 20, ...).
Body: `{ name, isActive?, conditions: [{ field, operator, values }], calendarId?, firstResponseMinutes?, resolutionMinutes?, pauseWhenPending?, approachingLeadMinutes? }`; PATCH adds `clearCalendar`, `clearFirstResponse`, `clearResolution`. At least one target. Policy condition fields: `priority`, `team`, `mailbox`, `subjectContains`, `requesterEmail`, `requesterDomain`. At most 50 policies. The first **active** policy, by order, whose conditions all match a new ticket is applied; policies are never combined. A policy with no calendar counts round the clock (UTC).

## Ticket SLA
- Every ticket list item and detail has `sla`: `null` (no policy) or `{ state, dueAt, clock, firstResponse, resolution }`. `state` is the worst of the two clocks: `Breached`, `Approaching`, `Running`, `Paused`, `MetLate`, `Met`.
- `GET /api/tickets/{ticket}/sla` (tickets.read): `204` when none, otherwise `{ policyName, calendar, timeZone, pauseWhenPending, appliedAt, firstResponse: { targetMinutes, dueAt, metAt, pausedAt, state, cycle }, resolution: {...}, events: [{ at, kind, clock, detail }] }`. Event kinds: `Applied`, `FirstResponseMet`, `Paused`, `Resumed`, `ResolutionMet`, `Reopened`, `Approaching`, `Breached`. Content free.
- `GET /api/tickets` gains `sla=breached|approaching|duesoon|paused` and `sort=sla_due_asc` (tickets with a running deadline, soonest first; keyset cursor). `breached`: unmet and past due; `approaching`: inside the warning window; `duesoon`: due within four hours. Both are department scoped.
- `GET /api/tickets/queue-counts` gains `slaBreached` and `slaApproaching` (only tickets the caller can read).
- Saved views accept `sla` and `sort: sla_due_asc`.

## Automation (`department.automation.manage`)
| Call | Notes |
|---|---|
| `GET/POST /api/departments/{d}/automations` | At most 100 rules. |
| `PATCH .../automations/{id}` | With `version`. |
| `PUT .../automations/order/{trigger}` | `{ ids }`: every rule of that trigger once. |
| `GET .../automations/history` | Filters `ruleId`, `ticket` (reference), `state`, `matched`; keyset `cursor`, `limit` up to 100. |
| `POST .../automations/test` | Dry run, below. |

Rule body: `{ name, trigger, isActive?, stopProcessing?, conditions: [{ field, operator, values }], actions: [{ type, value?, text? }] }`.
- **Triggers:** `TicketCreated`, `RequesterMessageAdded`, `StatusChanged`, `PriorityChanged`, `TeamChanged`, `AssigneeChanged`, `SlaApproaching`, `SlaBreached`.
- **Condition fields** (at most 10 per rule): `status`, `priority`, `team`, `assignee`, `mailbox` (`in` / `notIn`; ids or `none`), `subjectContains` (`contains`, literal, case-insensitive), `requesterEmail`, `requesterDomain` (`equals`), `slaFirstResponse`, `slaResolution` (`in` / `notIn` of `None, Running, Approaching, Breached, Paused, Met, MetLate`). All conditions must hold. Conditions are evaluated against the ticket's state at the moment the rule runs (after the triggering change). No regular expressions.
- **Actions** (1 to 10, run in order): `setStatus`, `setPriority`, `assignTeam` (id or `none`), `assignAgent` (id or `none`), `addInternalNote` (`text`, at most 1000 characters; staff only, never emailed). There is no email or public-reply action.
- `stopProcessing`: when the rule matched, no later rule runs for the same event. Order is `order` within a trigger, then creation.
- Saving fails with 400 field errors for unknown fields, operators, actions or values, and for any team, person or mailbox that does not belong to the department.

### Execution history item
`{ id, ruleId, ruleName, ticketId, ticketReference, trigger, chainId, depth, provenance, state, matched, conditions: [{ field, operator, values, actual, result }], actions: [{ index, type, value, outcome, detail }], error, skipReason, startedAt, finishedAt }`. `state`: `Running`, `Completed`, `Failed`, `Skipped` (`skipReason`: `loop_detected`, `max_depth`, `max_executions`). `provenance`: `user`, `email`, `sla`. `actual` is only given for non-sensitive values (status, priority, ids, requester domain). Subject text and addresses are never stored in the trace.

### Dry run: `POST .../automations/test`
Body `{ ticket: "IT-1042", trigger?, ruleId? }`. With `ruleId`: that rule only; otherwise every active rule of the trigger in order. The ticket is looked up through the caller's ticket scope (a ticket the caller cannot read, or from another department, is 404). Response: `{ ticket, trigger, wouldChangeAnything, notice, rules: [{ ruleId, name, evaluated, matched, stopsProcessing, conditions, actions: [{ index, type, value, result: WouldChange|WouldNotChange|WouldFail, detail }], note }] }`. Nothing is written: no ticket change, no execution row, no audit, no event. Effects of one rule are applied to the next rule's evaluation, but changes are not re-run as new triggers.

## Realtime
New event kind `sla` (content free, like the others) when a ticket's SLA is applied, completed, breached or approaching. Automation changes arrive as the ordinary `assignment`, `priority`, `status` and `note` kinds. Rule configuration and diagnostics never travel through the hub.

## Problem codes
`validation` (field errors keyed like `conditions[0]`, `actions[1]`, `weekly[2]`), `version_conflict`, `duplicate`, `invalid_state` (limits).
