# ADR-014: Business hours, SLA and automation live in the Ticketing module

Status: Accepted (Phase 5)

## Context
Phase 5 adds department business hours, SLA policies and clocks, and a constrained rule engine. SLA clocks change in the same moments as ticket status and replies; automation changes tickets through the same invariants as people do. Both must be department-scoped, auditable and loop-safe, and must not scan the database continuously.

## Decision
1. **Module placement.** The code lives in `Helpdesk.Modules.Ticketing` (`Sla/`, `Automation/`), not in a new module. Clocks and rules read and write ticket data and must commit with it; a separate module would need cross-module transactions or a copy of the ticket model. The seam to split later is the pair of hooks below. Mail still never references Ticketing.
2. **Two hooks, one place.** `TicketLifecycle.BeforeCommitAsync` (SLA clocks change inside the ticket's transaction) and `AfterCommitAsync` (automation runs after commit; realtime is told). Every ticket-changing path calls them (create, patch, assignment, post message, bulk, requester message, inbound mail). No controller decides SLA or automation behaviour.
3. **SLA state is persisted, not derived.** One `ticket_sla` row per ticket stores the frozen policy snapshot (including the calendar), both clocks and three indexed summary columns (`next_due_at`, `next_warn_at`, `next_event_at`). The monitor selects `next_event_at <= now` through a partial index with `FOR UPDATE SKIP LOCKED`: no job per ticket and no scan of tickets. Queue filters (breached, approaching, due soon, paused), the SLA sort and the counts read the same columns.
4. **Snapshots.** Applying a policy copies name, order, targets, pause rule, warning lead and the calendar (time zone, weekly hours, exceptions). Later edits never rewrite existing tickets, and calendar changes (including new holidays) apply to tickets created afterwards. Policy matching happens once, at creation; changing priority later does not re-apply a policy (see limitations).
5. **Business time.** `BusinessCalendar` is a pure class over `TimeZoneInfo`: local working intervals become instants, so DST days are 23 or 25 hours long in real terms. A spring-forward gap moves a start forward; an autumn repeat counts as working time when the interval contains it. IANA zone ids; the host image ships tzdata.
6. **Clock rules in one class.** `SlaEngine` alone decides start, pause, resume, stop and restart: Open runs; Pending pauses the resolution clock only when the policy says so (remaining business time is stored); Resolved and Closed complete it; leaving them for Open or Pending starts a new resolution cycle with the full target. First response is met only by a public agent reply, exactly once.
7. **Rules are data.** Triggers, condition fields/operators and actions are closed sets validated on save and again at run time. No scripts, no expressions, no regular expressions. Every team, person and mailbox is checked against the rule's own department on save and again before each action (fail closed).
8. **Execution identity and loop protection.** `automation_executions` has a unique key (rule, ticket, trigger key). Retried events reuse the key, so nothing repeats; interrupted or failed executions resume at the first incomplete action. Each run belongs to a chain (id, depth, provenance); a (rule, ticket) pair runs at most once per chain, depth is capped at 4 and a chain at 25 executions. Skips are recorded with their reason.
9. **System principal.** Automation changes go through `SystemTicketOps`: own transaction and row lock, domain rules (`ApplyStatus`), audit with no actor and `reason: automation`, SLA hook and realtime. Internal notes it creates are `Internal`, authored by `System`, delivery `None`, source `automation`, and are never handed to the mail outbox. Reviewed `IgnoreDepartmentScope()` callers are added to the architecture test.
10. **Visibility.** Configuration, execution history and the dry run need `department.sla.manage` / `department.automation.manage` (DepartmentAdmin). Agents see only the SLA state of tickets they can read. The dry run resolves the ticket through the caller's own ticket scope and writes nothing.

## Consequences
- Ticketing grows; the architecture tests continue to pin module boundaries and scope-bypass callers.
- The execution history stores a trace (matched or not, actions, outcomes) for every evaluation; retention is a future decision (BACKLOG).
- Single host only for the in-process realtime; the monitor itself is safe to run on several hosts.
