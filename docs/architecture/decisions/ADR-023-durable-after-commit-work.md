# ADR-023: Durable after-commit work

Status: Accepted (post-Refinement-10 remediation, WS-9; decision D16; findings BE-004, BE-005, BE-015, ARC-004)

## Decision
1. **Automation intents.** `ticketing.automation_outbox` follows the notification outbox pattern (no event framework). `TicketLifecycle.BeforeCommitAsync` (and `StageAutomationAsync` for the assignment path) stages one intent per automation trigger in the caller's transaction; the SLA monitor stages the SLA warning/breach intent in the same transaction that records the warning/breach. The intent carries ids, the trigger name, the engine's deterministic trigger key and the provenance only.
2. **Fast path and drain.** After commit the intents of that change run immediately. The recurring job (`notification-outbox`, which now also drains automation) runs any intent that is still unprocessed after 30 seconds. The engine's unique `(rule, ticket, trigger key)` makes any repeat a no-op. Engine infrastructure failures propagate (`RunDurableAsync`) so the intent is retried instead of being marked done.
3. **Attempts and parking (BE-005).** Both outboxes keep `attempts` and the exception type name of the last failure (`last_error`). After 5 failed attempts an item is parked: it stays unprocessed and visible, still counts in the oldest-age metric, and no longer delays newer items (ordering is by attempts, then age).
4. **Classification by the event's message (BE-015).** A public-message outbox item carries the message id; the recorder decides requester reply or agent reply from that message. Older rows without it keep the old behaviour.

## Consequences
- Migration `DurableAutomationOutbox` adds `automation_outbox` and the two columns. Existing notification rows get `attempts = 0`.
- Second-order triggers raised by an automation's own actions (chained rules) are still run in-process only; a crash mid-chain loses the nested step, but each parent execution resumes from its first incomplete action when its intent is retried.
- `AutomationEngine.DispatchAsync` is no longer used by the lifecycle.
