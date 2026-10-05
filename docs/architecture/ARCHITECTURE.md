# Architecture

## Style
Modular monolith, one deployable ASP.NET Core host (ADR-001). One PostgreSQL database (ADR-002). Background work runs in the same host initially via Hangfire (ADR-006), hosted as a separately-scalable role of the same image (`ROLE=web|worker|all`).

## Stack
React 19 + TypeScript + Vite + React Router + TanStack Query + Zod | C# / .NET 10 / ASP.NET Core / EF Core | PostgreSQL 18 | SignalR | Hangfire (PostgreSQL storage) | Entra ID OIDC | Microsoft Graph | S3-compatible object storage (MinIO locally) | Serilog + OpenTelemetry | xUnit + Playwright.

## Repository layout
```
src/
  Helpdesk.Host/            ASP.NET Core composition root, endpoints wiring, config
  Helpdesk.Modules.<X>/     one project per module, added when needed (from Phase 1)
  Helpdesk.SharedKernel/    only cross-cutting primitives (Result, IClock, ids); added when first needed
  web/                      React app
tests/
  Helpdesk.Host.Tests/ ...  per-module test projects + integration tests
  e2e/                      Playwright
docs/  scripts/  docker/
```
Modules are introduced when their functionality is required, not pre-created.

## Module rules
- A module owns its tables (own PostgreSQL schema, e.g. `tickets`, `mail`), its EF `DbContext`, its endpoints and its application services.
- A module exposes a small public contract (interfaces + DTOs in a `.Contracts` namespace/assembly). Other modules depend **only** on contracts, never on another module's internals or tables.
- No cross-schema joins in application code; cross-module reads go through contracts. (Reporting may later read via views — decided then.)
- Cross-module side effects use in-process domain events / outbox rows, written in the same transaction as the state change.
- Dependency direction enforced with architecture tests (NetArchTest) from Phase 1.
- Every module registers itself via `AddXModule()` / `MapXEndpoints()`.

## Planned modules (introduction phase)
| Module | Phase | Responsibility |
|---|---|---|
| Identity | 1 | OIDC login, user records, sessions, current-user context |
| Organisation | 1 | Departments, teams, memberships |
| Permissions | 1 | Roles, permission catalogue, `IAuthorizer` / department-scoped access |
| Audit | 1 | Append-only audit events, `IAuditWriter` |
| Tickets (+Requesters, Conversations) | 2 | Tickets, messages, notes, participants |
| Mail | 3 | `IMailProvider`, Graph provider, mailboxes, inbound/outbound pipeline |
| Attachments | 3 | `IObjectStore`, scanning/validation, metadata |
| Notifications, Search, SLA, BusinessHours, Automation | 4–5 | as named |
| KnowledgeBase, Portal | 6 | |
| Reporting | 7 | |
Splitting Requesters/Conversations out of Tickets is deferred until coupling proves it necessary.

## Request flow and authorization
HTTP → authentication (cookie session, Entra OIDC) → endpoint → `IAuthorizer.Require(permission, departmentId)` (server-side, every operation) → application service → EF. Detail in `docs/architecture/PERMISSIONS.md`. Department-scoped queries additionally go through a department-scope query filter keyed on the caller's accessible departments so a forgotten check still cannot read other departments' rows.

## Frontend
SPA served separately in dev (Vite proxy to API) and as static assets behind the reverse proxy in production. Cookie auth with BFF-style same-site session (no tokens in browser storage) + CSRF protection. API types generated from OpenAPI; Zod validates at the boundary. The frontend never enforces permissions, it only hides what the API would refuse.

## Cross-cutting
- **Errors:** RFC 9457 problem details; no silent swallowing; background failures surface in Hangfire and logs.
- **Time:** `TimeProvider` everywhere; all timestamps `timestamptz` UTC.
- **IDs:** UUIDv7 primary keys; human ticket numbers from a per-department-prefix sequence (decided in DOMAIN_MODEL).
- **Logging:** Serilog structured, with redaction rules (no message bodies, tokens, secrets).
- **Observability:** OpenTelemetry traces/metrics, OTLP exporter configurable.
- **Config:** options pattern, env vars override, secrets never in repo (`docs/security/SECURITY.md`).
- **Migrations:** EF migrations per module context, reversible where practical, applied by an explicit `migrate` command/job, not on web start in production.
- **Deployment:** Docker images, Compose for dev; reverse-proxy friendly (forwarded headers configured, Caddy/Nginx).
