# ADR-026: Service-desk workflow clarity

Status: Accepted (post-Refinement-10 remediation, WS-13; decisions D12, D15, D20, D21, D22, D23, D24; findings ITSM-001 to ITSM-006, MSG-009, BE-012, SEC-007)

The rules are in "Service-desk workflow" in `docs/architecture/TICKETING.md`. Decisions that change shared contracts, authorization or the data model:

1. **New permission `tickets.assign.team`** (content kind, Agent and above) splits team routing from choosing a person (`tickets.assign.any`). D21 says agents may change the team; it did not name the mechanism, and a separate code keeps the assignee rule untouched and lets the macro picker and bulk bar state exactly what is allowed. `RequireAssignmentRightsAsync` allows a team change with this permission when the assignee is unchanged or the change is a self take/release; on create the team still needs `assign.any`.
2. **Ticket mailbox assignment (D22)**: column `tickets.mailbox_assigned_at` marks an explicitly assigned mailbox (the only kind that can be corrected); `IMailboxDirectory.HasExternalSendAsync` tells whether anything was, or may have been, sent. Migration `TicketMailboxAssignment`.
3. **Portal availability (D22-A)** is a function of the department's default mailbox, evaluated per request (no stored flag).
4. **`InboundTicketRequest.Acknowledge`** (default true) lets Mail suppress the acknowledgement for released ignored mail.
5. **Identity (D15)**: portal linking no longer takes over an existing requester row by address.

## Consequences
- Departments without a default mailbox lose their portal forms until one exists (by decision).
- A person whose address already has an unlinked requester row (even without tickets) now needs an administrator's link before the portal works for them.
