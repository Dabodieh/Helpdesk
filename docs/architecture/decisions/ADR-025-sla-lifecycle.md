# ADR-025: SLA policy re-evaluation and first response on resolve

Status: Accepted (post-Refinement-10 remediation, WS-12; decisions D17 and D18; findings BE-006, BE-007, BE-008, BE-014, DATA-007)

See "SLA lifecycle" in `docs/architecture/TICKETING.md` for the rules. In short: the policy is re-evaluated on Team, Type or Priority changes without restarting the cycle (targets are recalculated from the original timing, spent business time is kept, an exceeded target is shown as exceeded); resolving or closing before any public agent reply records first response as Not applicable.

## Consequences
- No migration: Not applicable is represented by a first-response target with neither a deadline nor a met time, plus the event `FirstResponseNotApplicable`. A merged ticket's stopped first-response clock is also reported as Not applicable.
- A ticket whose policy stops matching keeps its current policy (nothing is removed). This case is not covered by D17 and is the conservative choice; revisit if the desk wants the SLA dropped.
- Policy changes on a ticket that is Pending with a pause-enabled policy keep it paused with the recomputed remaining time.
