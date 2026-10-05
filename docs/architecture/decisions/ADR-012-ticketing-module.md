# ADR-012: Ticketing module, references, scope filter and activity

Status: Accepted (Phase 2)

## Decision
- New module `Helpdesk.Modules.Ticketing` (schema `ticketing`) depending on SharedKernel, Audit.Contracts, Identity.Contracts and Organisation.Contracts (a new `IOrganisationDirectory` contract answers team/assignee/membership questions without table access). Nothing depends on Ticketing except the Host and, from Phase 3, the mail module through `Ticketing.Contracts` (`ITicketIngress`).
- Human references use the existing immutable department key (`IT-1042`); numbers come from a per-department counter row updated inside the creation transaction (row-lock serialisation), never `MAX+1`.
- Ticket history is the existing immutable audit stream (category `ticketing`), read through a new `IAuditReader` contract; no second history table.
- Internal vs public is an explicit `kind` column with database CHECK constraints; internal notes have their own permission (`tickets.notes.read`) enforced in the query.
- Department scope is enforced by composite foreign keys (team and assignee belong to the ticket's department), a fail-closed EF global query filter driven by `IAuthorizer`, and per-action `IAuthorizer` checks. RLS (D-04) remains deferred.
- Late requester replies reopen the same ticket (D-15 resolved). Realtime is deferred to Phase 4.
- Structured problem `code` extension replaces message-text matching in clients.

## Consequences
Ticket-id routes cannot use the `{departmentId}` route convention; they resolve the ticket through the scoped set and then authorise against its persisted department (architecture tests accept a resource-permission metadata for this). Removing a department membership silently unassigns that member's tickets (FK `SET NULL`), recorded as a limitation. Full text in `docs/architecture/TICKETING.md`.
