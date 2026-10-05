# Development Plan

Vertical slices; each phase leaves a runnable product. Phase order follows the brief; adjustment: a **Phase 3 spike** (Graph threading/send behaviour) happens early in Phase 1 in parallel because R-04 can invalidate the outbound design.

| Phase | Goal | Exit criteria |
|---|---|---|
| 0 Foundation | Repo, docs, ADRs, Compose (Postgres 18, MinIO), .NET host, Vite app, health endpoints, Serilog, OTel, CI | `docker compose up` deps; API `/health/live` & `/health/ready`; web builds; host + web tests run in CI |
| 1 Identity & organisation | Entra login, users, departments, teams, memberships, RBAC, audit writer, admin UI; Graph spike | Isolation test matrix for org endpoints; arch tests enforce module boundaries; spike result recorded as ADR |
| 2 Core ticketing | Requesters, tickets, list/detail, status/priority/assign, public reply/note (no email yet), audit history | Isolation tests on all ticket endpoints; ticket workspace usable |
| 3 Email slice | Mailboxes, Graph, subscriptions, inbound, dedup, threading, outbound, attachments, sender identity | **MVP acceptance scenario** automated + manual |
| 4 Agent productivity | Views, filters, FTS search, canned responses, tags, watchers, bulk actions, SignalR | |
| 5 SLA & automation | Business hours, SLA policies/timers, escalation, rules engine, retries | |
| 6 Self-service | Portal, KB, branding | |
| 7 Reporting | Dashboards and reports | |

## Phase 0 work packages
1. Repo scaffolding, conventions, CLAUDE.md, agent definitions (done in bootstrap).
2. `src/Helpdesk.Host` skeleton: options pattern, Serilog, OTel, health, OpenAPI, problem details, forwarded headers.
3. `tests/Helpdesk.Host.Tests` with a health integration test.
4. `src/web` Vite + React 19 + TS + Router + TanStack Query + Zod shell, health call, lint/test.
5. `docker-compose.yml` (PostgreSQL 18, MinIO), `.env.example`, `docker/` Dockerfiles for host and web.
6. CI workflow (build, targeted tests, secret scan placeholder).
7. `docs/DEVELOPMENT.md` onboarding.

## Testing policy
Targeted tests per change; full suite only at milestones, release candidates, or broad-risk changes (Principal decides). Milestone close = acceptance criteria check + targeted tests + focused RBAC/security review of changed areas + known limitations recorded in `docs/reports/`.
