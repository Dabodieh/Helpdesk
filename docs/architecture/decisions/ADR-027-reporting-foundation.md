# ADR-027: Reporting foundation

Status: Accepted (post-Refinement-10 remediation, WS-14; decisions D7b, D25, D26, D27 and D18; findings DATA-001 to DATA-006, DATA-009, ARC-005)

## Decision
1. **Shared definitions.** Backlog, awaiting first response, created and merged volume are defined once in `QueuePredicates` and written up in `docs/architecture/REPORTING_DEFINITIONS.md`. The sidebar counts, including the SLA badges, now use the shared backlog (Open + Pending, no merged sources, D25), which fixes the badges counting finished tickets. "Awaiting first response" is read from the message stream (D26); the SLA view is kept as a separate filter, `slafirstresponse`, labelled as an SLA figure. Created counts include merged duplicates, which are their own volume (D27).
2. **Content-free lifecycle record (D7b).** Table `ticketing.ticket_lifecycle_events`, written by `TicketingAudit` next to the audit event (ids, enumerations and time only; no foreign keys; back-filled from the audit stream by the migration `TicketLifecycleEvents`). Historical reports read this, not the audit detail, so they survive anonymisation. No warehouse or snapshot is introduced.
3. **One creation payload** for agent, portal and email tickets.
4. Pruning of any of this is NOT implemented: the retention periods are provisional (D-09).

## Consequences
- Changing the sidebar from Open-only to Open + Pending changes the numbers agents see.
- The back-fill covers history up to the migration; the table is as complete as the audit stream was.
