# ADR-024: Replay and staleness protection at the write edges

Status: Accepted (post-Refinement-10 remediation, WS-10; decision D19; findings BE-003, BE-010, BE-013, QA-001, QA-004, UX-003, UX-016)

## Decision
1. **Optional client message id (BE-003).** `POST /api/tickets/{ticket}/messages` (`clientMessageId`) and the portal reply (`clientMessageId` in JSON or the multipart form) take a GUID the browser creates once per composed message and re-sends on every retry. It is stored as the message's unique `source_ref` (`client:<guid>`, unique per department and source, the mechanism portal creation and email already use). Under the ticket row lock a repeat returns the first message and writes, reopens, queues, notifies and audits nothing again; the same id on another ticket or by another agent is `409 duplicate`. A malformed id is `400`. Clients that send none behave as before.
2. **Portal reply with files.** The reply is saved first; each file then goes through the attachment pipeline on its own. A file that cannot be stored is counted in `attachmentsNotSaved` on the successful response (the requester is told to attach it again) instead of failing a reply that exists. A replayed reply does not store files a second time.
3. **Bulk edit (D19-B).** The list returns each ticket's row `version`; `POST /api/tickets/bulk` accepts optional `versions` (ticket id to version). A ticket whose version differs is reported as `changed` and left untouched; the others proceed. Without `versions` the behaviour is unchanged.
4. **First save of queue preferences (BE-013)**: a primary-key race is redone as an update.
5. **Client:** one reconcile function (tickets, ticket, notifications, portal) after every way back to a live connection; the knowledge editor remembers the last server-confirmed copy after each step (save, then publish); portal request and form pages are keyed by route so drafts and ids never carry over.

## Consequences
- No migration (the `source_ref` column and its unique index exist).
- Bulk edits from clients that do not send versions remain last-write-wins.
