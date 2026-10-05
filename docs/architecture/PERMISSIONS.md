# Permissions / RBAC

## Model
- **Platform scope:** `PlatformAdmin` flag on User. Administers the installation (departments, users, global settings, identity). Does **not** implicitly read ticket content; ticket access needs a department membership (platform admins may grant themselves one, which is audited). *(Decision D-02, see ADR-005.)*
- **Department scope:** `DepartmentMembership(user, department, role)`. No membership = no access, and the department's existence/objects must not be disclosed.
- **Team scope:** teams refine assignment and queues inside a department. In v1 a team is **not** a security boundary (all department members with `tickets.read` see the department's tickets); team-restricted visibility is a backlog item. *(D-03)*
- Permissions are fine-grained codes; roles are fixed bundles of codes in v1.

## System roles (v1)
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
