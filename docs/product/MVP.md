# MVP

The MVP is the **central technical proof**: the full email loop, correctly isolated per department.

## MVP acceptance scenario
1. A user signs in via Microsoft Entra ID.
2. A platform admin creates a department and adds members with department roles.
3. A department admin configures a mailbox (Microsoft Graph, shared mailbox) for that department.
4. An email arrives at the mailbox; the system identifies the department from the mailbox, and creates a ticket (idempotent: duplicate webhook/delta events create nothing new).
5. An agent with access to that department sees the ticket; an agent without access cannot see it or learn it exists (404, not 403, for object lookups; absent from lists/search/counts).
6. The agent sends a public reply; it is sent from that department's mailbox identity with its signature.
7. The requester replies; the reply is threaded onto the same ticket via standards-based headers (not subject number alone).
8. All state changes are in the audit log.

## MVP includes
- Entra ID OIDC login, user provisioning on first login (no self-assigned access).
- Departments, teams, department memberships with roles, platform admin role.
- Mailboxes (Graph), inbound processing, outbound replies, attachments (object storage), HTML sanitisation.
- Requesters (auto-created from sender), tickets, public replies, internal notes, status, priority, assignment, participants (CC).
- Ticket list (per-department, my tickets), ticket detail workspace.
- Audit history on tickets and admin configuration.
- Minimal admin UI: departments, members, mailboxes.

## MVP excludes (scheduled later)
Saved views, bulk actions, canned responses, tags, SLA, automation, search beyond basic filter, portal, KB, reporting, realtime push, custom fields/forms, merging/linking.

## Exit criteria
Acceptance scenario passes as an automated integration test (Graph faked behind `IMailProvider`) plus a manual run against a real tenant; isolation tests pass for every department-scoped endpoint; no known critical/high security findings open.
