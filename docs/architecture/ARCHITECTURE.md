# Architecture

## Style
Modular monolith, one deployable ASP.NET Core host (ADR-001). One PostgreSQL database (ADR-002). Background work runs in the same host initially via Hangfire (ADR-006), hosted in the same process. A SINGLE application instance is the supported topology (ADR-021); there is no web/worker role split and no realtime backplane until a real scaling requirement exists.

## Stack
React 19 + TypeScript + Vite + React Router + TanStack Query + Zod | C# / .NET 10 / ASP.NET Core / EF Core | PostgreSQL 18 | SignalR | Hangfire (PostgreSQL storage) | Entra ID OIDC | Microsoft Graph | S3-compatible object storage (SeaweedFS locally; production provider is deployment-configurable) | Serilog + OpenTelemetry | xUnit + Playwright.

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

## Modules (introduction phase; all built except Reporting)
| Module | Phase | Responsibility |
|---|---|---|
| Identity | 1 | OIDC login, internal users, external-identity mapping, current-user context |
| Organisation | 1 | Departments, memberships, roles, permission catalogue and `IAuthorizer` (Permissions folded in, ADR-009) |
| Audit | 1 | Append-only audit events, `IAuditWriter` |
| Ticketing (requesters, tickets, conversation) | 2 | Tickets, department-scoped requesters, public messages and internal notes, per-department references, queues; `ITicketIngress` for Phase 3 (ADR-012, `TICKETING.md`) |
| Mail | 3 | Mailboxes, `IMailProvider` (Graph + simulator), webhooks/delta, inbound pipeline, outbox, attention list (ADR-013) |
| Attachments | 3 | `IObjectStore` (S3-compatible), validation, scan seam, metadata (ADR-013) |
| Notifications, Search, SLA, BusinessHours, Automation | 4–5 | as named (inside the Ticketing module; ADR-012, ADR-014) |
| Knowledge (articles, categories, audiences, feedback, reply links), Portal | 6 | built (ADR-015) |
| Reporting | 7 | not started: reporting definitions are in `REPORTING_DEFINITIONS.md` (ADR-027) |
Splitting Requesters/Conversations out of Tickets is deferred until coupling proves it necessary. `TicketService` is large (messages, bulk and delivery retry are partial files of it); split it along those seams only when a change forces it, not as a refactor of its own (ARC-009).

## Request flow and authorization
HTTP → authentication (cookie session, Entra OIDC) → endpoint → `IAuthorizer.Require(permission, departmentId)` (server-side, every operation) → application service → EF. Detail in `docs/architecture/PERMISSIONS.md`. Department-scoped queries additionally go through a department-scope query filter keyed on the caller's accessible departments so a forgotten check still cannot read other departments' rows.

## Frontend
SPA served separately in dev (Vite proxy to API) and as static assets behind the reverse proxy in production. Cookie auth with BFF-style same-site session (no tokens in browser storage) + CSRF protection. The client's response shapes are hand-written Zod schemas that validate at the boundary (generating types from OpenAPI is planned, see `docs/plans/BACKLOG.md`; until then a server DTO change is caught only by the tests that render it). The frontend never enforces permissions, it only hides what the API would refuse.

## Cross-cutting
- **Errors:** RFC 9457 problem details; no silent swallowing; background failures surface in Hangfire and logs.
- **Time:** `TimeProvider` everywhere; all timestamps `timestamptz` UTC.
- **IDs:** UUIDv7 primary keys; human ticket numbers from a per-department-prefix sequence (decided in DOMAIN_MODEL).
- **Logging:** Serilog structured, with redaction rules (no message bodies, tokens, secrets).
- **Observability:** OpenTelemetry traces/metrics, OTLP exporter configurable.
- **Config:** options pattern, env vars override, secrets never in repo (`docs/security/SECURITY.md`).
- **Migrations:** EF migrations per module context, reversible where practical, applied by an explicit `migrate` command/job, not on web start in production.
- **Deployment:** Docker images, Compose for dev; reverse-proxy friendly (forwarded headers configured, Caddy/Nginx).

## Hosting target (D-10)
Linux + Docker + Docker Compose + PostgreSQL + S3-compatible object storage, behind any reverse proxy. Cloud agnostic: nothing may depend on Azure hosting services; Entra ID and Microsoft Graph are integrations only. Must remain deployable to an internal Linux server, Azure, AWS or another Docker environment without redesign.

## Phase 1 structure (ADR-009)
```
src/Helpdesk.SharedKernel      current-principal abstraction (ICurrentUser), authorization primitives (IAuthorizer, PermissionRequirement,
                               DepartmentResource, permission codes, endpoint metadata), module migration runner, DB helpers, problem exceptions
src/Helpdesk.Modules.Identity  (Contracts/ + internals) users, external identities, provisioning, cookie/OIDC/dev sign-in, CSRF, platform-user endpoints
src/Helpdesk.Modules.Organisation  departments, memberships, permission catalogue, roles, IAuthorizer implementation and handler
src/Helpdesk.Modules.Audit     audit.audit_events, IAuditWriter, audit read endpoints
src/Helpdesk.Host              composition root: AddXModule/MapXEndpoints wiring, middleware order, exception mapping, `migrate` command
tests/Helpdesk.ArchitectureTests, tests/Helpdesk.Host.Tests (PostgreSQL-backed integration harness)
```
Module dependency direction (project references, enforced by architecture tests): Audit -> SharedKernel; Identity -> SharedKernel, Audit.Contracts; Organisation -> SharedKernel, Audit.Contracts, Identity.Contracts. `IAuthorizer` and its primitives live in SharedKernel (implemented by Organisation) because Audit and Identity expose permission-protected endpoints but cannot reference Organisation without a cycle.

The authentication scheme registration (cookie session, Entra OIDC, dev sign-in, antiforgery) is part of the Identity module and wired by the Host (`AddIdentityModule`, `UseIdentityModule`). ASP.NET authorization (fallback policy = authenticated, `PermissionRequirement` handler) is the single enforcement pipeline.

## Phase 1 web (admin UI)
`src/web` is an authenticated SPA shell over the Phase 1 API: `api/` (fetch wrapper with CSRF handling, zod schemas, error mapping, permission helpers), `pages/` (departments, department detail, team detail, sign-in, status), `components/` (route guard, teams/members sections, shared widgets). Controls are shown or disabled from `/api/me` permissions as a UX hint only; every request is still authorised by the API and 401/403/404 are handled in the UI. Platform administrators without a membership see departments as administrative metadata only. Known gaps are tracked in `docs/plans/BACKLOG.md`.

## Phase 2 structure
`src/Helpdesk.Modules.Ticketing` (schema `ticketing`, migration order 40) depends on SharedKernel, Audit.Contracts, Identity.Contracts and Organisation.Contracts (`IOrganisationDirectory`). Ticket history is the audit stream read through `IAuditReader`. Ticket-id routes use resource-scoped authorization (`RequireResourcePermission`) over a fail-closed department-scoped query filter. The web app has two areas: the agent workspace (queues, split ticket view; `docs/design/agent-workspace/UX.md`) and Administration under `/admin`. Realtime (SignalR) arrived in Phase 4; before that workspaces polled.

## Phase 3 structure
`src/Helpdesk.Modules.Mail` (schema `mail`, order 35) and `src/Helpdesk.Modules.Attachments` (schema `attachments`, order 50). Mail never references Ticketing: it calls interfaces it declares and Ticketing implements (`IInboundTicketSink`, `IDeliveryStateSink`); Ticketing calls `IMailboxDirectory` and `IMailOutbox`. Hangfire lives only in the Host (`Jobs/`), behind `IMailJobRunner`/`IMailJobScheduler`. Webhook endpoints are the only anonymous, CSRF-exempt routes besides sign-in helpers (listed in the architecture tests). The web app gains delivery states, attachments, a sandboxed formatted-email view and mailbox administration.

## Phase 4 structure
No new module. Ticketing gains saved views, canned responses, bulk change, trigram-indexed search and the SignalR hub `/hubs/tickets`. The hub is **notification only**: after a change commits, Ticketing (and Mail, through `NotifyTicketChangedAsync` and the delivery-state sink implemented by Ticketing) publishes a content-free `ticketChanged` event to server-chosen groups (`d:{department}`, `n:{department}` for internal-note events); clients re-read through the authorised REST API. Group membership is refreshed on membership changes (`IAccessChangeListener`, implemented by the hub, raised by Organisation) and every 60 s. Realtime is best-effort; polling stays as fallback (120 s live, 30 s otherwise). Single host instance; a multi-instance deployment needs a SignalR backplane. API: `docs/api/PHASE4_API.md`; report: `docs/reports/PHASE-4-AGENT-PRODUCTIVITY.md`.

## Phase 5 structure
Business hours, SLA and automation live inside the Ticketing module (ADR-014): `Sla/` (business-time calendar, SLA engine, conditions, admin services) and `Automation/` (rule model, engine, system ticket operations, lifecycle hooks, SLA monitor). Every ticket-changing path calls `TicketLifecycle` (SLA clocks inside the transaction, automation after commit). A one-minute Hangfire job (`SlaJobs` in the Host) calls `ISlaMonitor` (declared in Ticketing.Contracts), which selects due rows from an indexed column. API: `docs/api/PHASE5_API.md`; report: `docs/reports/PHASE-5-SLA-AUTOMATION.md`.
