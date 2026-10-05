# ADR-009: Phase 1 module layout, identity mapping and transactional audit

Status: Accepted (2026-10-05). Refines ADR-001, ADR-003, ADR-005.

## Context
Phase 1 introduces the first modules. ARCHITECTURE.md listed Identity, Organisation, Permissions and Audit separately. Memberships (Organisation) are the source of roles, so the authorizer needs them. Audit entries must commit atomically with the change they describe, while each module owns its own DbContext.

## Decision
1. **Modules** (one project each, `Helpdesk.Modules.<X>`, public surface in a `Contracts` namespace, internals `internal`): **Identity**, **Organisation** (includes roles, permission catalogue and `IAuthorizer`; the separate Permissions module is not created), **Audit**. `Helpdesk.SharedKernel` holds only the current-principal abstraction, clock and small DB helpers. Cross-module code references only another module's `Contracts` namespace; enforced by architecture tests.
2. **Schemas and migrations:** one PostgreSQL schema and one EF `DbContext` per module with its own migrations history table in that schema. A `migrate` host command applies them in dependency order (audit, identity, organisation). Cross-schema foreign keys to another module's primary key are allowed for integrity (added in migration SQL); application queries never join across modules.
3. **Identity mapping:** external identity = (provider, Entra tenant id `tid`, subject `oid`), unique. The internal `User` has its own id. Email/UPN and display name are mutable attributes refreshed at sign-in, never keys. First sign-in creates a user with **no access**. Only the single configured tenant is accepted; issuer and `tid` are validated. Platform admins are bootstrapped from configuration (`Authentication:BootstrapPlatformAdminSubjects`) and thereafter managed by platform admins; every grant is audited. The Entra OIDC handler and the development sign-in are both thin front ends that call the same `IUserProvisioner`; nothing downstream knows which was used.
4. **Development sign-in** (`Authentication:DevSignIn:Enabled`) is refused at startup unless the environment is `Development` or `Testing`; it issues the same cookie principal shape as OIDC, so authorization and tests exercise the real pipeline.
5. **Authorization pipeline:** ASP.NET fallback policy requires an authenticated user; endpoints state permissions via `PermissionRequirement` evaluated by an `IAuthorizationHandler` against a `DepartmentResource`; `IAuthorizer` wraps `IAuthorizationService` for services. Missing membership maps to 404, insufficient role to 403. A test fails if any endpoint lacks authorization metadata.
6. **Audit writer:** `IAuditWriter.WriteAsync(AuditEntry, DbContext caller)` inserts into `audit.audit_events` on the **caller's connection and transaction** (parameterised SQL owned by the Audit module), so state change and audit record commit or roll back together. Callers must run inside an explicit transaction. No update/delete path exists in application code.
7. **CSRF:** cookie-authenticated unsafe requests require an antiforgery token header obtained from `GET /api/csrf`.

## Alternatives considered
Separate Permissions module (circular dependency with Organisation); one shared DbContext (erodes module ownership); audit via outbox/eventual consistency (can lose or reorder security-relevant records); EF interceptors for audit (hidden magic, weak for intent-level events); email as identity key (mutable, reusable).

## Consequences
+ Atomic audit, small module count, database-enforced integrity. - Cross-schema FKs couple migrations (ordering handled by `migrate`); audit writer relies on callers using transactions (tested).
