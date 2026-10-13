# Development Plan

Security remediation update (2026-10-13): invitation hardening and missing response headers are implemented with targeted verification. Remaining audit items and development runtime recovery are tracked in [security remediation status](../reports/SECURITY-REMEDIATION-STATUS-2026-10-13.md).

Current status (2026-10-12): core phases, operational dashboards and historical analytics are built; post-Refinement-10 internal remediation is substantially complete. Invitations and guided Entra setup are implemented. The next delivery work is targeted stabilisation and production evidence, not starting Phase 7. Live Microsoft validation, restore/deployment evidence and organisational retention decisions remain open. Attachments stay disabled in production pending malware scanning.

The phase descriptions record the original vertical slices. Teams mentioned in that history have since been removed from active behaviour (ADR-034); schema removal is separately deferred.

| Phase | Goal | Exit criteria |
|---|---|---|
| 0 Foundation | Repo, docs, ADRs, Compose (Postgres 18, S3 dev store), .NET host, Vite app, health endpoints, Serilog, OTel, CI | `docker compose up` deps; API `/health/live` & `/health/ready`; web builds; host + web tests run in CI |
| 1 Identity & organisation | Entra login, users, departments, teams, memberships, RBAC, audit writer, admin UI; Graph spike | Isolation test matrix for org endpoints; arch tests enforce module boundaries; spike result recorded as ADR |
| 2 Core ticketing (CLOSED) | Requesters, tickets, list/detail, status/priority/assign, public reply/note (no email yet), audit history | Isolation tests on all ticket endpoints; ticket workspace usable. Report: `docs/reports/PHASE-2-CORE-TICKETING.md` |
| 3 Email slice (BUILT, tenant validation open) | Mailboxes, Graph, subscriptions, inbound, dedup, threading, outbound, attachments, sender identity | **MVP acceptance scenario** automated + manual |
| 4 Agent productivity | Views, filters, FTS search, canned responses, watchers (followers), bulk actions, SignalR (tags are a locked exclusion and were not built) | |
| 5 SLA & automation | Business hours, SLA policies/timers, escalation, rules engine, retries | |
| 6 Self-service | Portal, KB, branding | |
| 7 Reporting (built) | Operational dashboard, eight historical report types, ticket drill-down, CSV export | Production evidence remains open; see current readiness reports |

## Phase 1 scope (CLOSED, see report)
Entra auth foundation + internal users, departments, teams, memberships/roles/permissions, server-side authorization, audit writer, minimal admin UI, department-isolation tests, migrations on PostgreSQL 18, Graph shared-mailbox threading spike (messaging specialist, documentation + tooling + mocks; real-tenant results only if a dev mailbox is supplied), practical verification of the Phase 0 runtime foundation. Contract: `docs/api/PHASE1_API.md`; design: ADR-009, PERMISSIONS.md. Closed with `docs/reports/PHASE-1-IDENTITY-ORGANISATION.md`.

## Phase 2 scope (CLOSED, see report)
Agent workspace (queues, split ticket view, conversation, composer, history, keyboard); ticketing module (requesters, concurrency-safe department references, tickets, public messages and internal notes, assignment with database-enforced department consistency, queues with keyset pagination); structured problem codes; fail-closed department scope filter; Phase 2 isolation matrix and threat model; Playwright critical-flow test. Design: `docs/architecture/TICKETING.md`, ADR-012, `docs/api/PHASE2_API.md`, `docs/design/agent-workspace/UX.md`.

## Phase 3 scope (built; production readiness gated)
Mail and Attachments modules, mailbox admin, Graph provider + simulator, webhooks/delta/subscriptions, inbound pipeline with threading and idempotency, outbox and delivery states, attachments on S3-compatible storage, HTML sanitising, Hangfire jobs. Report: `docs/reports/PHASE-3-EMAIL.md`. Real-tenant validation (ADR-011) is not done.

## Phase 4 scope (built)
Agent productivity: UX polish and resizable split, saved views, bulk actions, canned responses, trigram search with snippets, SignalR realtime with scoped groups and polling fallback, keyboard/density/sortable columns, axe checks, Playwright flows. Report: `docs/reports/PHASE-4-AGENT-PRODUCTIVITY.md`, API: `docs/api/PHASE4_API.md`.

## Phase 5 scope (built)
Business hours, SLA policies and clocks, SLA visibility and filters, declarative automation with routing, execution history, dry run, loop protection. Report: `docs/reports/PHASE-5-SLA-AUTOMATION.md`, ADR-014, `docs/api/PHASE5_API.md`.

## Phase 6 scope (built)
Requester portal (Home, Submit a request, My requests, Knowledge), explicit requester identity links, request forms with typed custom fields, requester-safe DTOs and realtime, email/portal shared conversation, Knowledge module with full-text search and feedback, form and knowledge administration. Report: `docs/reports/PHASE-6-REQUESTER-SELF-SERVICE.md`, ADR-015, `docs/api/PHASE6_API.md`, `docs/security/PHASE6_THREAT_MODEL.md`.

## Phase 7 scope (built)
Department-scoped historical reports: support overview, ticket volume, SLA, response times, lifecycle, backlog, workload and email. Backend: `ReportingService`; frontend: Analytics home and report pages with filters, drill-down and CSV export. Operational snapshots remain distinct from historical metrics; agent scoring is excluded.

## Phase 0 work packages
1. Repo scaffolding, conventions, CLAUDE.md, agent definitions (done in bootstrap).
2. `src/Helpdesk.Host` skeleton: options pattern, Serilog, OTel, health, OpenAPI, problem details, forwarded headers.
3. `tests/Helpdesk.Host.Tests` with a health integration test.
4. `src/web` Vite + React 19 + TS + Router + TanStack Query + Zod shell, health call, lint/test.
5. `docker-compose.yml` (PostgreSQL 18, S3 dev store), `.env.example`, `docker/` Dockerfiles for host and web.
6. CI workflow (build, targeted tests, secret scan placeholder).
7. `docs/DEVELOPMENT.md` onboarding.

## Testing policy
Targeted tests per change; a full suite only when the user explicitly asks for it for the task (`CLAUDE.md`). Milestone close = acceptance criteria check + targeted tests + focused RBAC/security review of changed areas + known limitations recorded in `docs/reports/`.
