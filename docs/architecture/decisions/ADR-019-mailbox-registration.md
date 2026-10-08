# ADR-019: Mailbox registration authority and access probe

Status: Accepted (post-Refinement-10 remediation, WS-4; decision D10, finding MSG-004)

## Decision
1. Only a Platform Admin registers a mailbox (`POST /api/departments/{id}/mailboxes`; 403 otherwise). Department Admins keep managing already registered mailboxes (`department.mailboxes.manage`). The UI offers Add mailbox only to a Platform Admin.
2. Registration first probes the mailbox with the application's own credentials (`IMailProvider.ProbeMailboxAsync`; Graph: read the inbox folder). 403/404 (permanent) refuses with a validation error on `address`; an unverifiable probe (transient) refuses with a conflict. Nothing is created in either case, and a Platform Admin cannot bypass the probe. There is no approved-address list.

## Consequences
- The probe proves the application can reach the mailbox, not that Exchange scoping is correct: D-16 E-2/E-3 must still show the application cannot access mailboxes outside its approved scope.
