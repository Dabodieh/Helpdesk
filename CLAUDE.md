# Helpdesk

Multi-department Helpdesk/ITSM platform. Modular monolith: .NET 10 / ASP.NET Core / EF Core / PostgreSQL 18; React 19 + TypeScript + Vite. Read `docs/architecture/ARCHITECTURE.md` and `docs/product/MVP.md` before substantial work.

## Team model
The main session acts as Principal Engineer (final architectural authority). Specialist agents live in `.claude/agents/`: backend-engineer, frontend-engineer, messaging-integration-engineer, qa-security-engineer. Spawn them only when parallelism helps. They must not silently change shared contracts, auth, permissions, DB architecture, module boundaries, common APIs, global dependencies or deployment: propose, Principal assesses, ADR if significant, then implement. The repo is the source of truth.

## Hard rules
- Department isolation: server-side authorization on every department-scoped operation via `IAuthorizer`; 404 for inaccessible objects. Cross-department leakage is Critical.
- Email: department comes from the receiving mailbox; replies leave from that department's identity; ingestion idempotent.
- No secrets in the repo. Never log message bodies or tokens.
- Layout: code in `src/`, tests in `tests/`, scripts in `scripts/`, docs in `docs/`. No stray root files.
- Scope: ideas go to `docs/plans/BACKLOG.md`; unresolved questions to `docs/plans/OPEN_DECISIONS.md`.

## Testing
Targeted tests only for the area changed. Full suite only at milestones, release candidates, explicit request, or broad-risk changes.
Backend: `dotnet test tests/<Project> --filter ...`. Frontend: `npm --prefix src/web test -- <path>`.

## Commands
- Deps: `docker compose up -d` (PostgreSQL, MinIO)
- API: `dotnet run --project src/Helpdesk.Host`
- Web: `npm --prefix src/web run dev`
