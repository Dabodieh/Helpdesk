# Permissions / RBAC

## Model
- **Platform scope:** `PlatformAdmin` flag on User. The Platform Administrator is the global super-administrator (S-02, ADR-039): it administers the installation (creates/deactivates departments, manages platform admins, reads platform-level audit) **and holds every department permission, administrative and content, in every existing department without a membership** (tickets, Internal Notes, users and memberships, configuration, reporting). It can add or remove any user in any department and set their department roles, including its own membership (self-assignment is allowed and needs no separate approval). Every such membership change is audited and carries `selfGrant` and `viaPlatformAdmin` in the event.
- **Administrative vs content permissions.** Each permission is still classified (*administrative*: department structure, memberships, configuration; *content*: `tickets.*` and all future ticket/message/attachment access), because the classification documents what a permission protects. Since S-02 the platform-admin flag satisfies **both** kinds (department must exist, otherwise 404); department roles satisfy only what their role grants. The classification is covered by tests.
- **Department scope:** `DepartmentMembership(user, department, role)`. No membership = no access; the department's existence must not be disclosed (404).
- **Team scope:** there are no teams (ADR-034): the Department is the workgroup and the hard security boundary. A department member with the right permission sees all tickets of the department.
- **Emergency ("break-glass") access (D-12) is moot for the Platform Administrator** (it already has global access). It would only matter for a future role below Platform Administrator, and is not planned.
- Permissions are fine-grained codes; roles are fixed bundles of codes in v1. Only permissions for existing resources are defined (no speculative permissions).

## Phase 1 permission catalogue
| Code | Kind | Granted by |
|---|---|---|
| `platform.departments.manage` | platform | PlatformAdmin |
| `platform.users.manage` | platform | PlatformAdmin |
| `platform.audit.read` | platform | PlatformAdmin (organisation/identity categories only, never ticket content) |
| `department.read` | administrative | any department role; PlatformAdmin (every department) |
| `department.manage` (rename, description) | administrative | DepartmentAdmin; PlatformAdmin |
| `department.members.manage` | administrative | DepartmentAdmin; PlatformAdmin |
| `department.audit.read` | administrative | TeamLead, DepartmentAdmin; PlatformAdmin |
| `department.mailboxes.manage` | administrative | DepartmentAdmin; PlatformAdmin (registering a NEW mailbox additionally requires Platform Admin, ADR-019) |
| `tickets.read`, `tickets.notes.read` | **content** | Viewer, Agent, TeamLead, DepartmentAdmin (their department); PlatformAdmin (every department) |
| `tickets.create`, `tickets.reply`, `tickets.note`, `tickets.update`, `tickets.assign.self` | **content** | Agent, TeamLead, DepartmentAdmin (their department); PlatformAdmin (every department) |
| `tickets.assign.any` | **content** | TeamLead, DepartmentAdmin (their department); PlatformAdmin (every department): choose another person as assignee |
Activation (`isActive`) of a department needs `platform.departments.manage`.
Phase 2 adds the ticket resource and the ticket permission set (all *content* kind; roles grant them per department, the platform-admin flag everywhere since S-02): `tickets.read`, `tickets.create`, `tickets.reply`, `tickets.note`, `tickets.notes.read`, `tickets.update`, `tickets.assign.self`, `tickets.assign.any`; role mapping and enforcement details in `TICKETING.md`. The Phase 1 probe endpoint was removed.
`teams.manage` and `tickets.assign.team` were retired with Teams (ADR-034).

## System roles (v1 target set; Phase 1 implements the catalogue above)
| Permission | Viewer | Agent | TeamLead | DeptAdmin |
|---|:-:|:-:|:-:|:-:|
| tickets.read, tickets.notes.read | x | x | x | x |
| tickets.create / reply / note / update (status, priority, subject) | | x | x | x |
| tickets.assign.self | | x | x | x |
| tickets.assign.any / reassign | | | x | x |
| tickets.move (cross-department, needs target too) | | | | x |
| tickets.delete | | | | x |
| department.members.manage | | | | x |
| department.mailboxes.manage | | | | x |
| department.config.manage (templates, signatures, etc.) | | | | x |
| audit.read (department) | | | x | x |
Platform: `platform.departments.manage`, `platform.users.manage`, `platform.settings.manage`, `platform.audit.read`.

Example: User A = IT: TeamLead, Facilities: Agent, HR: none → sees IT and Facilities tickets with those capabilities; HR is invisible.

## Enforcement rules (non-negotiable)
1. Every endpoint declares a required permission and obtains the target department **from the persisted object**, never trusting a department id in the request body for access decisions.
2. Single entry point: `IAuthorizer` (`Require(permission, departmentId)`, `AccessibleDepartments(permission)`). No ad-hoc role checks in handlers.
3. Object lookups by id return **404** when the caller lacks access (no existence oracle). Lists, counts, search and facets are filtered by accessible departments in the query itself.
4. Defence in depth: department-scoped EF queries use a scoped query filter bound to the current user's accessible departments; bypass requires an explicit, named `IgnoreDepartmentScope()` used only by system jobs and reviewed.
5. Background jobs and inbound email run as a `System` principal with an explicit department taken from the mailbox; they never accept department from message content.
6. Mutations of memberships/roles prevent privilege escalation: a DeptAdmin cannot grant a role above their own, cannot touch other departments, and cannot remove the last DeptAdmin silently (warn/block). Only PlatformAdmin grants PlatformAdmin.
7. Moving a ticket requires `tickets.move` in source and `tickets.create` in target.
8. All permission/role/membership changes are audited.
9. Authorization is covered by isolation tests: for each department-scoped endpoint, a matrix test (no membership, wrong department, insufficient role, sufficient role).
10. **Administrative endpoints and Department content (S-02 supersedes decision D14).** Department Administrators, Team Leads, Agents and Viewers still receive Department content only for departments they belong to, through the role that grants it; an administrator of ANOTHER department gets 404 everywhere (non-disclosure). The Platform Administrator holds the permissions in every department, so the former withholding of mail-attention addresses and subjects, automation history, mail retry/resend and Knowledge drafts no longer applies to it. `AdministrativeContentProbeTests` now calls every department-level GET endpoint as the administrator of a DIFFERENT department and fails if any content of the probed department appears; `DepartmentOwnedDataAccessTests` still pins which types may read the Knowledge, Mail and Attachments tables.

## Tooling
Policy-style authorization handlers sit behind `IAuthorizer`; endpoint filters make a missing declaration a startup/architecture-test failure.

## Phase 1 implementation notes
- The frontend infers a platform admin's administrative permissions for departments without a membership from the permission catalogue (a hardcoded list in `src/web/src/api/permissions.ts`); this is UX only and tracked in BACKLOG ("single source for UI hints").
- Enforcement rule 4 is implemented for ticketing (Phase 2): a fail-closed EF global query filter on tickets, messages and requesters, plus per-action `IAuthorizer` checks and composite foreign keys (see `TICKETING.md`). Organisation tables keep authorizer checks and query-level filtering.

## Phase 4 additions
`department.canned.manage` (administrative kind, membership based, DepartmentAdmin only): create, edit and (de)activate **department-scope** canned responses. Personal canned responses and saved views need no permission: they are owner-only and a different user's id returns 404. Using a department canned response requires being able to reply or add notes in that department. Bulk change adds no permission: each ticket is checked against `tickets.update` / `tickets.assign.*` individually. The realtime hub grants group membership from `tickets.read` (department group) and `tickets.notes.read` (notes group), recomputed when memberships change.

## Phase 5 additions
`department.forms.manage` (ticketing configuration: ticket types, ticket fields and request forms) and `department.knowledge.manage` (knowledge categories and articles): administrative kind, DepartmentAdmin only. Portal endpoints (`/api/portal/*`) use no department permission: they are authenticated-only and scoped by the caller's requester links (ADR-015).

`department.sla.manage` (business hours, SLA policies) and `department.automation.manage` (rules, execution history, dry run): administrative kind, DepartmentAdmin only. The platform-admin flag satisfies them in every department (S-02), so the dry run, which needs the caller's `tickets.read` scope, is available to it everywhere. Agents see the SLA state of tickets they can read and nothing of the configuration.

## Department boundary (security refinement 1)
Department is the hard security boundary. There are no teams (ADR-034). Followers, links, merges, saved views and search never widen scope: they all run through the department-scoped query filters and `IAuthorizer`, and a foreign object answers like a missing one. See SECURITY.md for the baseline, the intentional self-service exceptions and the verifying tests.

## User deactivation and sessions (S-01)
- Only `platform.users.manage` (platform administrators) can deactivate or reactivate an account; department roles, including Department admin, never can. Self-deactivation and deactivating the last active platform administrator are refused.
- Every request loads the account: inactive, unknown, a changed session version or a session older than the absolute lifetime (`Authentication:Session:AbsoluteLifetimeHours`, default 12) is refused and signed out. Deactivation and reactivation increment the session version, so sessions issued before a deactivation never become valid again.
- Privilege changes (platform administrator flag, department membership and role) need no session invalidation: permissions are read from the database on every request and take effect on the next one.
- Deactivation releases the account's assigned tickets in every department in the same transaction (audited with reason `user_deactivated`); memberships, messages, audit history and requester records are kept. Sign-in never reactivates an account.

## Platform Administrator policy (S-02, ADR-039)
- **Decision (product owner, 2026-10-16):** the Platform Administrator is the global super-administrator. It has full access to all departments, tickets, Internal Notes, users, memberships, settings, reporting and administration, without department membership. Self-assignment to a department is permitted and needs no separate approval. This supersedes decisions D-02 and D14 and the earlier "no implicit ticket access" rule.
- **Mechanism:** one rule in the central evaluator (`PermissionEvaluator`): the platform-admin flag satisfies every non-platform permission when the department exists (404 when it does not). `IAuthorizer.AccessibleDepartmentsAsync` / `HasInAnyDepartmentAsync` return every department for it, so the ticket query filter, search, queues, dashboards and reports widen with no per-feature change. `IOrganisationDirectory.GetDepartmentsWithPermissionAsync` returns every department for an active platform administrator, so realtime groups (department and note groups) and notification eligibility follow the same access. The flag is read from the database on every request, so removing it takes effect on the next request.
- **Unchanged (deliberately):** (1) *Assignment and membership-derived lists stay membership-based*: a ticket can only be assigned to a department member (composite foreign key), pickers, followers, mentions and SLA/lead notifications list members, and the notes/reply recipient logic is untouched. A platform administrator who wants to own tickets or be mentioned or notified in a department adds itself as a member (allowed, audited). (2) *Department Administrators, Team Leads, Agents and Viewers* keep exactly their role in their own departments; another department is invisible (404); no role carries a platform permission; a Department Administrator cannot grant `PlatformAdmin`. (3) *Mailbox registration, user deactivation, department creation and activation* remain platform permissions exactly as before.
- **Audit:** membership add, role change and removal events carry `next.viaPlatformAdmin` (true when the actor holds the flag and is not the target department's Department Administrator) and `next.selfGrant`; the platform-admin flag change is audited as `user.platform_admin_changed` with `selfChange`. Ticket and message changes made by a platform administrator are recorded like any other actor's (ticket events and audit rows name the actor).
- **Known UI limitation:** `/api/me` lists memberships only, so the web queue and department navigation of a platform administrator show departments it belongs to; other departments are reached through Administration. Showing every department in the queue switcher is a UI change not made here.
- **Tests:** `PlatformAdministratorPolicyTests` (global ticket and Internal Note access, membership administration with audit markers, confinement of every department role), `PermissionModelTests`, `AuthorizerQueryTests`, `AuthorizationDecisionTests`, the isolation matrices, and `AdministrativeContentProbeTests` (administrator of another department sees nothing).
