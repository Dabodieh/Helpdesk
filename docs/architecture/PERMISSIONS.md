# Permissions / RBAC

## Model
- **Platform scope:** `PlatformAdmin` flag on User. Administers the installation: creates/deactivates departments, manages the department structure and memberships, manages platform admins, reads platform-level audit. **Platform administration and ticket-data access are separate privileges (D-02).** A platform admin has no ticket/content access to any department unless they hold a department membership whose role grants it. Granting oneself a membership is an ordinary membership change, audited and flagged as a self-grant (D-11).
- **Administrative vs content permissions.** Each permission is classified. *Administrative* permissions (department structure, teams, memberships, department audit of configuration) may be satisfied by the platform-admin flag. *Content* permissions (`tickets.*` and all future ticket/message/attachment access) are satisfied **only** by a department membership. This classification is a property of the permission definition in code and is covered by tests.
- **Department scope:** `DepartmentMembership(user, department, role)`. No membership = no access; the department's existence must not be disclosed (404).
- **Team scope:** teams are **not** a security boundary in v1 (D-03). They provide assignment, routing, queues, organisation, workload management and reporting. A department member with the right permission sees tickets across all teams of the department. Team-level restrictions are a future option; do not hard-wire "all department members see everything" into storage (visibility is decided in the authorizer, not in the schema).
- **Future emergency access (not implemented, D-12):** if ever added it must be explicit, strongly audited, temporary where practical, clearly distinguishable from ordinary access, and never available merely because someone is a platform administrator.
- Permissions are fine-grained codes; roles are fixed bundles of codes in v1. Only permissions for existing resources are defined (no speculative permissions).

## Phase 1 permission catalogue
| Code | Kind | Granted by |
|---|---|---|
| `platform.departments.manage` | platform | PlatformAdmin |
| `platform.users.manage` | platform | PlatformAdmin |
| `platform.audit.read` | platform | PlatformAdmin (organisation/identity categories only, never ticket content) |
| `department.read` | administrative | any department role; PlatformAdmin |
| `department.manage` (rename, description) | administrative | DepartmentAdmin; PlatformAdmin |
| `department.members.manage` | administrative | DepartmentAdmin; PlatformAdmin |
| `teams.manage` | administrative | DepartmentAdmin; PlatformAdmin |
| `department.audit.read` | administrative | TeamLead, DepartmentAdmin; PlatformAdmin |
| `department.mailboxes.manage` | administrative | DepartmentAdmin; PlatformAdmin |
| `tickets.read`, `tickets.notes.read` | **content** | Viewer, Agent, TeamLead, DepartmentAdmin (membership only) |
| `tickets.create`, `tickets.reply`, `tickets.note`, `tickets.update`, `tickets.assign.self` | **content** | Agent, TeamLead, DepartmentAdmin (membership only) |
| `tickets.assign.any` | **content** | TeamLead, DepartmentAdmin (membership only) |
Activation (`isActive`) of a department needs `platform.departments.manage`.
Phase 2 adds the ticket resource and the ticket permission set (all *content* kind, membership only): `tickets.read`, `tickets.create`, `tickets.reply`, `tickets.note`, `tickets.notes.read`, `tickets.update`, `tickets.assign.self`, `tickets.assign.any`; role mapping and enforcement details in `TICKETING.md`. The Phase 1 probe endpoint was removed.
TeamLead-scoped team management is deferred (backlog): in Phase 1 `teams.manage` is DepartmentAdmin only.

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
| teams.manage | | | x(own team) | x |
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

## Tooling
Policy-style authorization handlers sit behind `IAuthorizer`; endpoint filters make a missing declaration a startup/architecture-test failure.

## Phase 1 implementation notes
- The frontend infers a platform admin's administrative permissions for departments without a membership from the permission catalogue (a hardcoded list in `src/web/src/api/permissions.ts`); this is UX only and tracked in BACKLOG ("single source for UI hints").
- Enforcement rule 4 is implemented for ticketing (Phase 2): a fail-closed EF global query filter on tickets, messages and requesters, plus per-action `IAuthorizer` checks and composite foreign keys (see `TICKETING.md`). Organisation tables keep authorizer checks and query-level filtering.

## Phase 4 additions
`department.canned.manage` (administrative kind, membership based, DepartmentAdmin only): create, edit and (de)activate **department-scope** canned responses. Personal canned responses and saved views need no permission: they are owner-only and a different user's id returns 404. Using a department canned response requires being able to reply or add notes in that department. Bulk change adds no permission: each ticket is checked against `tickets.update` / `tickets.assign.*` individually. The realtime hub grants group membership from `tickets.read` (department group) and `tickets.notes.read` (notes group), recomputed when memberships change.

## Phase 5 additions
`department.forms.manage` (ticketing configuration: ticket types, ticket fields and request forms) and `department.knowledge.manage` (knowledge categories and articles): administrative kind, DepartmentAdmin only. Portal endpoints (`/api/portal/*`) use no department permission: they are authenticated-only and scoped by the caller's requester links (ADR-015).

`department.sla.manage` (business hours, SLA policies) and `department.automation.manage` (rules, execution history, dry run): administrative kind, DepartmentAdmin only. The platform-admin flag satisfies them but grants no ticket content access, so the dry run (which needs the caller's own `tickets.read` scope) is not available to a platform administrator without membership. Agents see the SLA state of tickets they can read and nothing of the configuration.

## Department boundary (security refinement 1)
Department is the hard security boundary. Teams are assignment/routing constructs and do not grant cross-Department visibility. Followers, links, merges, saved views and search never widen scope: they all run through the department-scoped query filters and `IAuthorizer`, and a foreign object answers like a missing one. See SECURITY.md for the baseline, the intentional self-service exceptions and the verifying tests.

