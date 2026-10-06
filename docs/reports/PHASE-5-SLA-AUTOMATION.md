# Phase 5 report: business hours, SLA and automation

Status: **built and tested; closed for Phase 5 scope.** Production readiness is still gated by the items under "Remaining production gates". Phase 6 has not been started.

## Phase 4 items closed first
1. **Restricted role test.** A test-only role (`TestRestrictedAgent`: read, create, reply, update; no `tickets.notes.read`) exists only in the test process (`RoleCatalogue.RegisterTestRole`, internal, visible to the test assembly) and in the fixture's throw-away database (the role check constraint is widened there). Ordinary roles are unchanged. The test proves, through the real REST and SignalR paths: messages, ticket detail, search, counts and activity reveal neither the text nor the existence of an internal note; no realtime event is delivered for the note (and the connection still receives a later public reply); the restricted role cannot write notes. It found a real leak: the activity endpoint listed `message_added` entries of internal notes. It now hides them and refills the page so the page size cannot reveal them. Residual: a ticket's `updatedAt` still moves when a note is added (it records any activity).
2. **Lint.** The four oxlint warnings were local and are fixed (a selection reset derived from the filter key instead of an effect, two helpers moved out of component files, one effect dependency). Lint is clean.

## What was built
- **Business hours** per department: name, IANA time zone, active flag, weekly intervals (several per day), full-day closures and date-specific alternative hours. `BusinessCalendar` is pure arithmetic over `TimeZoneInfo` (add business time, business time between instants, next working instant), with defined behaviour for the spring-forward gap and the autumn repeat.
- **SLA policies:** name, active flag, explicit order, structured conditions (priority, team, mailbox, subject contains, requester email/domain), calendar (or 24 x 7), first-response and resolution targets, pause-when-Pending, warning lead. First active match by order wins; policies are never combined.
- **Snapshot per ticket:** the applied policy, targets, pause rule and the calendar itself are frozen on the ticket (`ticket_sla.snapshot`). Editing a policy or calendar afterwards changes only tickets created later; deadlines stay explainable from the event history (`Applied`, `Paused`, `Resumed`, `FirstResponseMet`, `ResolutionMet`, `Reopened`, `Approaching`, `Breached`).
- **Clocks:** first response is met by the first public agent reply, once; internal notes, requester messages, system events and automation notes never count. Resolution is met by Resolved or Closed (late completions are `MetLate`); Pending pauses it only when the policy says so, storing the remaining business time; reopening a resolved or closed ticket starts a new resolution cycle with the full target (first response stays met). All of this is in `SlaEngine`.
- **Monitor:** a one-minute Hangfire job calls `ISlaMonitor`, which selects `next_event_at <= now` from a partial index with `SKIP LOCKED`, records warnings and breaches once, publishes realtime events and runs SLA triggers. It is registered and has run successfully on the live host.
- **Agent experience:** SLA column and inline badge in the queue (word + mark, never colour alone), SLA strip and a details dialog in the ticket header (frozen policy, both clocks, history), queue filter (breached, approaching, due within 4 hours, paused), "SLA due soonest" sort (header button and a Sort select), "SLA breached" entry with count in the navigation, counts that only include readable tickets, SLA in saved views.
- **Automation:** per-department rules with trigger, ordered structured conditions, ordered structured actions, explicit order and an explicit stop-processing flag. Triggers: ticket created, requester message, status, priority, team and assignee changes, SLA approaching, SLA breached. Conditions: status, priority, team, assignee, mailbox, subject contains, requester email and domain, SLA states. Actions: set status, set priority, assign team, assign agent, add internal note. No scripts, no regular expressions, no outbound email action.
- **Routing** is the same engine (for example: created, subject contains "network" assigns the Network team).
- **Execution history** (admin only): per evaluation the rule, ticket, trigger, provenance, chain id and depth, every condition result, every action outcome, errors, skip reasons. Nothing of this appears in ticket conversations; ticket history shows only "reason: automation" without naming the rule.
- **Dry run** ("Test against ticket"): explains trigger context, which conditions matched, which actions would change, which would fail, which rules a stop flag skips; writes nothing.
- **Administration UI:** Department tabs Business hours, SLA policies and Automations (structured weekly editor with exceptions and a "check a deadline" tool, duration inputs, condition and action row editors, ordering buttons, test and history views). No raw JSON.
- **Realtime:** a content-free `sla` event kind; automation changes use the normal assignment, priority, status and note events.
Design: ADR-014, `docs/api/PHASE5_API.md`.

## Loop protection and idempotency
- Every automation run belongs to a chain (id, depth, provenance). A (rule, ticket) pair runs at most once per chain; depth is capped at 4, a chain at 25 executions. Tested with a two-rule cycle (A: High to Urgent, B: Urgent to High): it terminates, both rules run once, the repeat is stored as `Skipped / loop_detected`.
- `(rule, ticket, trigger key)` is unique. A user change gets a fresh key per change; SLA events use `sla:{clock}:{cycle}:{kind}`; a retried event is a no-op (tested: three deliveries of one event add one evaluation, one note, one assignment). Notes also carry a unique source reference. Failed or abandoned executions resume at their first incomplete action (implemented; not covered by a dedicated test).

## Test results (one broad pass at the phase boundary)
| Suite | Result |
|---|---|
| `Helpdesk.Host.Tests` | 782/782. New: business-calendar arithmetic 16, SLA flows 14, automation 12, conditions 4, realtime automation 1, restricted-role notes 1 (plus updated shape/leak/architecture expectations) |
| `Helpdesk.ArchitectureTests` | 61/61 (reviewed scope-bypass callers extended with the SLA/automation system code; `ISlaMonitor` lives in Contracts) |
| `Helpdesk.GraphSpike.Tests` | 43/43 |
| Web lint / build / vitest | clean / builds / 74/74 |
| Playwright (real stack) | 8/8 (agent workflow, email loop, four Phase 4 flows plus axe, new service flow); 4 screenshot captures are opt-in |
| Axe (WCAG 2.0/2.1 A and AA) | no serious or critical violations on the business-hours dialog, SLA policy dialog, rule editor, SLA details dialog, automation history and dry-run result |
| Migrations (PostgreSQL 18) | all six modules down to 0 and up again on a scratch database; 14 ticketing tables |
| Docker host image | rebuilt, running; tzdata present; `sla-monitor` registered and succeeding |

High-value cases covered: Friday 16:30 + 4 business hours = Monday 12:30; holiday closure; ticket created on a Saturday; deadline across the GMT/BST change; Pending pause and resume with remaining business time; paused time never breaches; first public reply satisfies first response once while notes, requester messages and automation notes do not; resolved/closed met, late completion, Pending never completes; reopening starts cycle 2; policy and calendar edits do not touch existing tickets; policy order and inactive policies; monitor records approaching and breached once; SLA filters, counts and sort; automation ordering, stop-processing (and a non-matching stop flag stopping nothing), cycle termination, duplicate events, cross-department references rejected on save and failing closed when stored, crafted payloads rejected, dry run changes nothing and cannot inspect inaccessible tickets, agents and viewers cannot reach configuration or history, notes stay internal, are not mailed and do not satisfy first response, SLA breach triggers automation once.

## Security guarantees tested
Department A cannot read or change Department B's business hours, policies, rules, history or dry runs (404); non-administrators get 403; SLA lists, filters and counts include only readable tickets; automation cannot name another department's team, person or mailbox, even in a stored rule; crafted rule payloads fail closed; the dry run cannot see tickets the caller cannot read (including platform administrators without membership) and writes nothing; automation obeys ticket-domain invariants (status timestamps, internal-note constraints) because it uses the same domain methods and database checks; automation notes are internal, system-authored and never reach the outbox.

## Performance
Per-ticket state is indexed, not recomputed: `next_event_at` (monitor), `(department_id, next_due_at)` and `(department_id, next_warn_at)` (filters, sort). Scratch check on 500,000 synthetic rows with the same partial indexes: the monitor's selection `next_event_at <= now ORDER BY next_event_at LIMIT 200` takes 0.08 ms (index scan); the per-department breached count uses the index. This was a synthetic table, not the application query under load. Automation runs only on events; there is no per-ticket job and no table scan.

## Known limitations
- A policy is matched once, when the ticket is created. A later priority or team change does not switch policies.
- Automation conditions see the ticket after the triggering change (a requester message that reopens a Pending ticket is seen as Open).
- The dry run does not simulate follow-on triggers caused by an action.
- Every evaluation, including non-matches, is stored; there is no retention yet (D-09 covers it in principle).
- The monitor runs every minute, so a warning or breach is recorded up to a minute after the moment; the queue filters and badges use the stored deadlines and are exact.
- No notification channel exists beyond realtime events and internal notes; "internal escalation" is an internal note plus priority/assignment actions.
- Mailbox conditions are covered by evaluator and validation tests; no end-to-end inbound-mail-with-rules test was added.
- The `sla` realtime event is published but has no dedicated test (automation events do).
- The weekly editor shows an end of `24:00` as 23:59 in the time input (the stored value is kept until edited).
- Hangfire shares the main database; the monitor is safe on several hosts but SignalR still assumes one.

## Remaining production gates (unchanged, not closed)
D-16 / ADR-011 real Microsoft 365 validation; D-08 malware scanner engine (`RequireCleanScan` stays fail-closed); D-13 Data Protection key persistence; D-09 retention. Production readiness is not claimed.

## Readiness for Phase 6
Ready. Do not start Phase 6 until asked.
