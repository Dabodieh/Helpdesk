# Who can read ticket content

Status: DRAFT for organisational approval. The wording below is not an approved privacy notice and must not be published or relied on as one until the decision in OPEN_DECISIONS D-20 is closed by the organisation (data-protection lead or equivalent).

## Purpose
Tickets, replies, Internal Notes, requester names and email addresses and attachments can contain personal data. This page records who can read them so that it can be reflected in the privacy notice and in the record of processing.

## Who can read ticket content
| Who | Which tickets | Internal Notes |
|---|---|---|
| Requester (employee) | Their own requests in the portal | Never |
| Viewer, Agent, Team Lead, Department Administrator | Tickets of the departments they belong to, according to their role | Yes, in their own departments |
| **Platform Administrator** | **Tickets of every department, without belonging to it** | **Yes, in every department** |
| Anyone else | None. Another department's tickets do not exist for them (not found) | Never |

## Platform Administrators
- A Platform Administrator is the installation's global super-administrator (ADR-039). It can open, search, report on and manage tickets and Internal Notes in all departments, and administer departments, memberships and roles.
- The number of Platform Administrators is kept small and each is a named individual (the last active one cannot be removed or deactivated). Their sign-in and session controls are the same as everyone's (Entra sign-in, session revocation on deactivation, absolute session lifetime).
- Changes to who holds the role, to department memberships and roles, and ticket changes are recorded in the audit trail with the acting person. Reading a ticket is not recorded individually.
- Access is a role of the Platform Administrator, not a copy of the data: nothing is exported, and departments do not receive notifications, assignments or follows because a Platform Administrator looked at a ticket.

## Outstanding decisions (not assumed approved)
- D-20: organisational approval of this wording and of Platform Administrators having access to all ticket content, including Internal Notes, for the privacy notice and the record of processing.
- Whether reads of ticket content by a Platform Administrator should be logged (not implemented).
- Retention of ticket content remains governed by D-09 (unresolved).
