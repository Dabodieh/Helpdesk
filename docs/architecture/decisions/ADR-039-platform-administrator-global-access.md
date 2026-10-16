# ADR-039: The Platform Administrator is the global super-administrator

Status: Accepted (product-owner policy decision S-02, 2026-10-16). Supersedes decisions D-02 and D14 and the content/administrative split of ADR-033 as it applied to the platform-admin flag.

## Context
Decision D-02 kept platform administration and ticket-data access apart, and ADR-033 allowed the platform-admin flag to satisfy administrative permissions only. The pre-production audit (S-02) flagged the ability to grant oneself a membership as bypassing that separation. The product owner has decided that the separation is not wanted: the Platform Administrator is the owner of the installation and must be able to see and manage everything.

## Decision
- The platform-admin flag satisfies every department permission, administrative and content, in every existing department, with no membership. Self-assignment to a department stays allowed and needs no approval.
- The change lives in the central authorisation (`PermissionEvaluator`, the authorization handler, `IAuthorizer` department lists, the organisation directory used by realtime and notifications). No endpoint grew a new check.
- Department Administrators, Team Leads, Agents and Viewers keep strict department isolation and role limits. Another department stays invisible (404).
- Privileged changes stay audited: membership events carry `selfGrant` and `viaPlatformAdmin`.
- Assignment, pickers, followers, mentions and lead notifications remain membership-based (a platform administrator joins a department to be assignable or notified).

## Consequences
- S-02 is an intentional permission policy, not a vulnerability. The platform administrator account is now the most sensitive account in the system: its sign-in, session and the number of people holding the flag are the control (S-01 session revocation applies; the last-administrator rule remains).
- The Platform Administrator can read Internal Notes and requester data in every department; this must be reflected in the privacy notice and in the data-protection record of who can access ticket content.
- The former withholding of mail-attention and automation-history content from platform administrators is no longer applied to them; it still applies to everyone without ticket access in that department.
