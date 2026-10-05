# Phase 1 API contract (shared by backend, frontend, QA)

Owner: Principal Engineer. Changes require Principal approval. JSON, camelCase, ids are GUID strings, times ISO-8601 UTC. Errors are RFC 9457 problem details.

## Conventions
| Status | Meaning |
|---|---|
| 401 | not authenticated |
| 404 | object does not exist **or the caller has no access to its department** (no existence oracle) |
| 403 | caller is a member but lacks the permission (or lacks a platform permission) |
| 400 | validation (`errors` map) |
| 409 | uniqueness conflict, optimistic-concurrency conflict (`version` mismatch), last-admin rule |
All unsafe methods (POST/PUT/PATCH/DELETE) need header `X-CSRF-TOKEN` from `GET /api/csrf`. Department-scoped sub-resources are always resolved **by both ids** (`departments/{d}/teams/{t}` finds the team only if it belongs to `d`). Mutable resources carry `version` (opaque string); updates send it back, mismatch -> 409.

## Auth
| Method & path | Auth | Notes |
|---|---|---|
| `GET /api/auth/login?returnUrl=/` | anonymous | Entra OIDC challenge. `returnUrl` must be a local path. |
| `GET /api/auth/dev-login?subject=&name=&email=&returnUrl=` | anonymous, **only when `Authentication:DevSignIn:Enabled` and env is Development/Testing**, otherwise route does not exist | Issues the same cookie principal as OIDC. |
| `POST /api/auth/logout` | auth | |
| `GET /api/csrf` | anonymous | `{ "headerName": "X-CSRF-TOKEN", "token": "..." }` |
| `GET /api/me` | auth | `{ id, displayName, email, isPlatformAdmin, platformPermissions: string[], memberships: [{ departmentId, departmentKey, departmentName, role, permissions: string[] }] }` (permissions are informational for UI; the server enforces independently) |

## Departments
| Method & path | Permission | Notes |
|---|---|---|
| `GET /api/departments` | auth | Departments where the caller is a member, plus **all** departments for platform admins (administrative metadata only). Item: `{ id, key, name, description, isActive, myRole|null, version }` |
| `POST /api/departments` `{ key, name, description? }` | `platform.departments.manage` | `key`: `^[A-Z][A-Z0-9]{1,9}$`, immutable. 201 |
| `GET /api/departments/{id}` | `department.read` | |
| `PATCH /api/departments/{id}` `{ version, name?, description?, isActive? }` | `department.manage` (name/description); `platform.departments.manage` (isActive) | |

## Teams (department-scoped)
| Method & path | Permission |
|---|---|
| `GET /api/departments/{id}/teams` | `department.read` (item: `{ id, name, description, isActive, memberCount, version }`) |
| `POST /api/departments/{id}/teams` `{ name, description? }` | `teams.manage` |
| `GET /api/departments/{id}/teams/{teamId}` | `department.read` |
| `PATCH /api/departments/{id}/teams/{teamId}` `{ version, name?, description?, isActive? }` | `teams.manage` |
| `GET /api/departments/{id}/teams/{teamId}/members` | `department.read` -> `[{ userId, displayName, email }]` |
| `PUT /api/departments/{id}/teams/{teamId}/members/{userId}` | `teams.manage`; user must already be a department member (else 409) |
| `DELETE /api/departments/{id}/teams/{teamId}/members/{userId}` | `teams.manage` |

## Members and roles
| Method & path | Permission | Notes |
|---|---|---|
| `GET /api/departments/{id}/members` | `department.read` | `[{ userId, displayName, email, role, teamIds[] }]` |
| `PUT /api/departments/{id}/members/{userId}` `{ role }` | `department.members.manage` | add or change role. Roles: `DepartmentAdmin`,`TeamLead`,`Agent`,`Viewer`. Demoting/removing the last DepartmentAdmin -> 409. Audit event carries `selfGrant` when actor == target. |
| `DELETE /api/departments/{id}/members/{userId}` | `department.members.manage` | removes team memberships (cascade) |
| `GET /api/users/lookup?q=` | `department.members.manage` in at least one department, or `platform.users.manage` | `q` min 3 chars, max 20 results `{ id, displayName, email }`, active users only |

## Platform
| Method & path | Permission |
|---|---|
| `GET /api/platform/users?search=&cursor=` | `platform.users.manage` -> users with `isPlatformAdmin`, `isActive` |
| `PUT /api/platform/users/{id}/platform-admin` `{ value }` | `platform.users.manage`; cannot remove the last platform admin (409) |
| `GET /api/platform/audit?cursor=` | `platform.audit.read`; categories `organisation` and `identity` only |

## Audit (department)
`GET /api/departments/{id}/audit?cursor=&limit=` requires `department.audit.read`. Returns `{ items: [{ id, occurredAt, actor: {id, displayName}|null, action, objectType, objectId, previous, next, source }], nextCursor }`. Read-only; no write API exists.

## Probe (temporary, Phase 1 only)
`GET /api/departments/{id}/ticket-access-probe` requires the **content** permission `tickets.read`; 204/403/404; returns no data. Replaced by real ticket endpoints in Phase 2.

## Audit actions emitted (Phase 1)
`department.created`, `department.updated`, `department.activated`, `department.deactivated`, `team.created`, `team.updated`, `team.deactivated`, `team.activated`, `membership.added`, `membership.role_changed`, `membership.removed`, `team_membership.added`, `team_membership.removed`, `user.provisioned`, `user.platform_admin_changed`, `auth.signin` is **not** audited per request (log only). Values are redacted (no tokens/claims dumps).

## Clarifications (as implemented and tested)
- **CSRF:** a missing/invalid token returns 400 problem details. The token is bound to the signed-in user; fetch `GET /api/csrf` again after sign-in/sign-out. Unauthenticated requests (including unmatched URLs) get 401 with no redirect. Failed Entra sign-in redirects to `/?authError=failed|inactive`.
- **PATCH:** `version` is required (missing/malformed 400, mismatch 409). `description`: `null`/absent = unchanged, `""` clears. A PATCH with none of `name`, `description`, `isActive` is 400.
- **Members:** `PUT .../members/{userId}` returns 200 with `{ userId, displayName, email, role, teamIds }`; `PUT .../teams/{t}/members/{u}` is idempotent, 204; DELETE is 204, or 404 if the membership does not exist.
- **Inactive:** adding members/teams/team members to an inactive department, members to an inactive team, or an inactive user as a member is 409.
- **Content probe:** a platform admin without membership gets 404 on `ticket-access-probe`.
- **Audit events** additionally carry `category` and `departmentId`. Membership events: `objectType: department_membership`, `objectId: <userId>`, `selfGrant` inside `next`. Team membership events: `objectType: team_membership`, `objectId: <teamId>`, `{teamId, userId}` in previous/next. The department audit endpoint returns the `organisation` category only. The actor display name is a snapshot taken at write time.
- **Platform users list** is ordered by id (creation order); the cursor is the last id; `search` and `limit` (default 50, max 200) are supported.
- **Bootstrap admins:** a subject in `Authentication:BootstrapPlatformAdminSubjects` becomes platform admin at first provisioning, or at sign-in when no active platform admin exists (recovery); it is not re-applied on every sign-in.
