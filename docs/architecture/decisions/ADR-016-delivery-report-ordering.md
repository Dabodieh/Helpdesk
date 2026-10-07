# ADR-016: Delivery reports are ordered by a monotonic sequence, and a retry after a bounce is a new send

Status: Accepted (post-Refinement-10 remediation, WS-3 first slice: MSG-001, BE-009)

## Context
Mail reports how an agent's public reply ended to Ticketing through `IDeliveryStateSink`. The sink applied whatever it received, so a late or replayed report could overwrite a newer one (BE-009). Separately, retrying a reply after a confirmed bounce (`ndr_delivery_failed`) kept the provider draft id; the sender found the original in Sent Items, skipped the send and marked the message Sent (MSG-001).

## Decision
1. **Ordering value.** `mail.messages.delivery_seq` (bigint, default 0) is a per-message counter. A database trigger adds one on every change of an outbound message's `state`, whichever code path makes the change. No wall clock is used for correctness. Rows that existed before the migration start at 1.
2. **The report carries it.** `IDeliveryStateSink.SetAsync` takes a `sequence` argument; Mail passes the row's `delivery_seq`.
3. **The sink enforces it.** Ticketing stores the highest accepted value in `ticketing.messages.delivery_report_seq` (default 0). A report is applied only when its sequence is higher; an equal or lower value is ignored (a repeat or a stale report) and cannot regress the state or repeat audit and realtime work. The check is part of the update statement, so concurrent reports cannot both pass it. A higher report that states what the ticket already shows only moves the sequence. The guarantee is in the sink, not in any one caller.
4. **Retry after a bounce.** Retrying a failed message whose `last_error` is `ndr_delivery_failed` clears `provider_draft_id`, so the sender creates a new draft and sends again. It is audited as `mail.resent_after_bounce`, on both retry routes (ticket "Retry sending" and the administrator's mail retry). Any other failure keeps its draft, so a send that actually happened is still reconciled to Sent and not repeated. Uncertain messages are unchanged: never retried blindly, `mail.uncertain_retried` with an explicit duplicate-risk decision.

## Consequences
- Two migrations (Mail: column, backfill, trigger; Ticketing: column) and one shared-contract signature change.
- A test or tool that resets a ticket message's delivery state by SQL must also reset `delivery_report_seq`, or the re-report is treated as a repeat.
- Explicit Uncertain/Bounced states (ARC-001) are separate, later work and do not change this mechanism.
