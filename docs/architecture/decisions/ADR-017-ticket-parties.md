# ADR-017: Who is a party to a ticket

Status: Accepted (post-Refinement-10 remediation, WS-2; decisions D8 and D9 in `docs/plans/POST-REFINEMENT-10-DECISIONS.md`)

## Context
Inbound mail, the portal, merge and the outbound Cc each decided separately who belongs to a conversation (findings MSG-002/SEC-006, MSG-003, BE-001, SEC-004/BE-002, ARC-003). A correlated email from anyone was attached, reopened the ticket, and added the sender and every To/Cc address as participants, who then received every later public reply. Participants had no cap and could not be removed. Merging tickets of different requesters orphaned the source requester.

## Decision
1. **One rule, one service.** `TicketParties` (Ticketing) answers who is a party and is the only code that adds, protects, removes or counts participants. A party is the primary requester or an ACTIVE participant. Inbound correlation, participant addition, merge and the outbound Cc builder all use it. It is not a generic party or ACL framework.
2. **Correlation never grants membership.** Reply headers, the correlation token and the conversation id only say which ticket a message is about. `IInboundTicketSink.AddMessageAsync` attaches only when the sender is a party and otherwise returns null, so Mail opens a NEW ticket with an internal note naming the ticket it looked like a reply to (the existing subject-hint precedent; no held-mail workflow). Nothing is written to the original ticket: no message, no reopen, no participants, no requester row.
3. **Copied people.** People copied on a message sent by a party become ordinary participants, at most 20 per ticket; the primary requester and protected requesters are not counted. Further addresses are not added, the message is still accepted, and one internal system note lists them (never requester visible). Nothing is ever created from a non-party's To/Cc.
4. **Removal is final.** A user who can reply may remove an ordinary participant from future Cc (`POST /api/tickets/{ticket}/participants/remove`, address in the body). The row stays, inactive with who and when, so a later Cc does not bring the person back and they stop being a party. History is untouched and the removal is audited (`ticket.participant_removed`). The primary requester and protected requesters cannot be removed.
5. **Merge keeps the source requester.** Merging tickets of different requesters is allowed. Each source requester becomes a protected participant of the canonical ticket (`is_protected`, `requester_id`): always copied on public replies, never removable, not counted against the cap. A ticket that already held protected requesters passes them on to the final canonical ticket. The requester portal lists and shows the canonical request to a protected requester by requester ownership (never by address), read-only: replying and editing fields stay with the ticket's own requester. Merges made before this change are not back-filled.
6. **UI.** The composer lists the actual Cc recipients (name and address) with a remove control for ordinary ones; the merge dialog warns that another requester will join the conversation and receive future public replies.

## Consequences
- Migration `TicketParties` adds `is_protected`, `requester_id`, `removed_at`, `removed_by_user_id` to `ticketing.ticket_participants` (existing rows are ordinary and active). The shared contract `IInboundTicketSink` gains `FindByIdAsync`; the meaning of a null `AddMessageAsync` result is widened.
- A person who is not the requester or a participant and replies to a ticket thread now gets a new ticket instead of joining the conversation. Colleagues who should be able to reply by email must be copied by the requester (or be a merged requester).
- Merges made before this change left their source requesters outside the conversation; surfacing them for deliberate remediation is in `docs/plans/BACKLOG.md`.
- The visible correlation token in replies (MSG-012) is unchanged and remains open.
