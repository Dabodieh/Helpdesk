# ADR-033: What an administrative permission means for a Platform Admin

Status: Accepted (recorded retroactively in WS-16 from decision D14 and WS-1; finding ARC-006)

## Decision
- Permissions are classified. *Administrative* permissions (department structure, teams, memberships, mailboxes, forms, SLA and automation configuration, department audit of configuration) may be satisfied by the platform-admin flag without a department membership. *Content* permissions (everything under `tickets.*`) never are.
- An administrative endpoint returns configuration, never department content. Anything content-derived (a ticket subject or sender address, requester history, the contents of mail or automation runs that name tickets) additionally requires ticket read by membership in that department; without it the data is withheld or the object does not exist (404).
- Platform Admins may keep form-audience configuration, membership configuration and administrative audit views, provided those carry no ticket, message or requester content. Registering a new mailbox is a Platform-Admin-only action (ADR-019); assigning a mailbox to a ticket and releasing ignored mail additionally require membership and ticket access (ADR-026).
- A probe test calls every administrative GET as a non-member Platform Admin and fails if a content-classified field appears.

## Consequences
- A new administrative endpoint that returns anything derived from tickets must apply the membership rule or fail the probe.
