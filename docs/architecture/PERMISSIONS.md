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
| `tickets.read` | **content** | Viewer, Agent, TeamLead, DepartmentAdmin (membership only) |
Activation (`isActive`) of a department needs `platform.departments.manage`.
Phase 1 has no ticket resource; `tickets.read` is exercised through a clearly marked probe endpoint (`GET /api/departments/{id}/ticket-access-probe`, returns 204/404/403 and no data) which Phase 2 replaces with real ticket endpoints and tests.
TeamLead-scoped team management is deferred (backlog): in Phase 1 `teams.manage` is DepartmentAdmin only.

## System roles (v1 target set; Phase 1 implements the catalogue above)
| Permission | Viewer | Agent | TeamLead | DeptAdmin |
|---|:-:|:-:|:-:|:-:|
| tickets.read | x | x | x | x |
| tickets.create / reply / note | | x | x | x |
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
