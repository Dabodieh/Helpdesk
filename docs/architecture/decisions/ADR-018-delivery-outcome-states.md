# ADR-018: Explicit delivery outcome states

Status: Accepted (post-Refinement-10 remediation, WS-3; ARC-001, MSG-001, MSG-006, MSG-008, MSG-010; extends ADR-016)

## Context
The outcome of an agent's public reply crossed the Mail/Ticketing boundary as `Failed` plus a magic string (`outcome_uncertain`, `delivery_failed`). The string was compared in the sender, a SQL predicate behind the dashboard and queue filter, and the browser.

## Decision
1. `IDeliveryStateSink` states are `Pending, Sent, Failed, Uncertain, Bounced`. `Failed` = definitely not submitted; `Uncertain` = submission outcome unknown (never retried automatically); `Bounced` = a correlated delivery report confirmed non-delivery. The ticket message delivery state mirrors them (`None, NotDelivered` unchanged). The safe category stays in `delivery_error` for display only; no consumer branches on it.
2. Ticket-level retry is offered for `Failed` and `Bounced` (a retry after a bounce is a recorded re-send, ADR-016 and MSG-001), never for `Uncertain`. The send-problem predicate uses the states.
3. The dispatcher looks for the sent copy of an `Uncertain` message once, 10 minutes after it became uncertain (read-only, never sends; flag `reconcile_attempted`). Found = `Sent`. Otherwise it stays `Uncertain` for an operator.
4. Acknowledgements carry `x-auto-response-suppress: All` (Graph accepts only `x-` headers on a draft). Whether Exchange adds `Auto-Submitted` is a D-16 tenant check (E-37, E-38).
5. Inbound mail with the same Message-ID, subject and sender is a duplicate regardless of the time gap.

## Consequences
- Migration `DeliveryStatesUncertainBounced` widens the state check constraint and converts existing `Failed` rows with the two known categories.
- MSG-007 (forged bounce) is NOT closed: restricting bounces to the tenant's own transport needs real header samples (D-16 E-38).
