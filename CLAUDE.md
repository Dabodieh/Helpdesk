# Helpdesk

Multi-department Helpdesk/ITSM platform. Modular monolith: .NET 10 / ASP.NET Core / EF Core / PostgreSQL 18; React 19 + TypeScript + Vite. Read `docs/architecture/ARCHITECTURE.md` and `docs/product/MVP.md` before substantial work.

## Agent usage
Do not spawn subagents, specialist agents, parallel agents or delegated workstreams. This project uses a single-agent workflow to control token/usage costs and keep one coherent project context. The primary Claude Code session owns architecture, implementation, testing, security review, documentation and integration (final architectural authority). Do not invoke definitions under `.claude/agents/` unless the user explicitly requests a subagent for a specific task. Do not delegate because tasks could theoretically run in parallel; prefer sequential implementation with targeted tests. This rule overrides any older documentation recommending multi-agent development. The repo is the source of truth; shared contracts, auth, permissions, DB architecture, module boundaries, common APIs, global dependencies and deployment changes need an ADR when significant.

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
- Deps: `docker compose up -d` (PostgreSQL; object store opt-in via `--profile storage`)
- API: `dotnet run --project src/Helpdesk.Host`
- Web: `npm --prefix src/web run dev`
