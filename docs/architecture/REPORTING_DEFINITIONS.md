# Reporting definitions

The one place that says what a headline figure means. The code lives in `QueuePredicates` (Ticketing) and is used by the sidebar counts, the queue list filters, the department dashboard and any future report. Historical Reports have **not** been started; they must reuse these definitions (decisions D7, D18, D25, D26, D27; ADR-027).

| Figure | Definition | Where |
|--------|------------|-------|
| Backlog / open work | Status Open or Pending, and not a merged source ticket | `QueuePredicates.Backlog` |
| Sidebar counts (My, Unassigned, All, Following) | Backlog, narrowed as the name says. Open + Pending, not Open only | `TicketQueryService.QueueCountsAsync` |
| SLA badges (breached, near deadline) | Backlog tickets with a deadline passed / inside the policy's own warning lead | `QueuePredicates.SlaBreached`, `SlaApproaching` |
| Due within 4 hours | Backlog tickets whose deadline falls in the next 4 wall-clock hours. Not the same idea as "near deadline" | `QueuePredicates.SlaDueSoon` |
| Awaiting first response | Backlog tickets with no PUBLIC message by a human agent yet. Independent of any SLA policy; an acknowledgement or an internal note never counts | `QueuePredicates.AwaitingFirstResponse` |
| SLA first response pending | The SLA-specific view: a first-response target exists and its clock is still running. Always labelled as an SLA figure | `QueuePredicates.SlaFirstResponsePending` |
| First response when a ticket is resolved or closed without a reply | Not applicable: neither met nor breached (D18) | SLA state `NotApplicable` |
| Created in a period | Every ticket raised in it, including one merged away later (it was raised) | `QueuePredicates.CreatedBetween` |
| Merged volume | Duplicates merged away, reported as their own figure, never as backlog or independent demand | `QueuePredicates.MergedAway` |
| Received in a period | `GET /api/departments/{id}/reports/volume?from&to` (yyyy-MM-dd, Europe/London days, inclusive, max 366 days, default last 30). Same rule as Created in a period | `DepartmentVolumeReportService` |
| Resolved in a period | Distinct tickets that moved into Resolved, or into Closed from an open state, in the period (lifecycle record); merged sources excluded; reopen and re-resolve counts once. Team split uses the ticket's current team | `DepartmentVolumeReportService` |
| Received and resolved per day | The `daily` series of the volume report (Europe/London days, only for periods up to 62 days; longer periods return totals and an empty series). Days add up to the totals; a ticket resolved more than once counts on the day of its first qualifying move. Used by the dashboard activity chart, which is a short trend beside the live figures, not a replacement for the historical reports | `DepartmentVolumeReportService` |
| Who spoke last | The latest PUBLIC message's author (requester or agent); notes never count | ticket list `lastPublicBy` |

## Sources of history
- Lifecycle columns (`resolved_at`, `closed_at`, the requester's name and address) hold the **current** value only.
- **`ticketing.ticket_lifecycle_events`** is the content-free history reports read: ticket, department, kind (`created`, `status`, `priority`, `assignee`, `message`, `merged`), the previous and new value (enumerations and ids only) and the time. It has no actor, subject, address or body and no foreign keys, so it survives any later anonymisation or deletion of audit detail or tickets (D7b). It is written in the same transaction as the audit event and was back-filled from the audit stream when it was created.
- SLA history lives in `sla_events` (applied, met, paused, resumed, reopened, policy changed, breached, not applicable). Reopen counts and first-resolution times come from these two streams.
- Creation events of every channel share one payload: `reference, source, status, priority, assigneeUserId, ticketTypeId, mailboxId, formId`.

## Caveats a report must state
- Distinct-requester counts are **approximate**: requesters are per department and per address; only a linked account joins rows (DATA-009).
- Dashboard age buckets use the creation time of Open and Pending tickets alike; a ticket waiting on the requester ages like one nobody touched. Split by status when age trends are built.
- Facts that are not captured cannot be rebuilt: mailbox outage history (only the latest sync state is kept), article views, and why a ticket had no SLA (DATA-008). No decision to capture them has been made.
- Retention periods are provisional (D7, D-09). Nothing here prunes anything.
