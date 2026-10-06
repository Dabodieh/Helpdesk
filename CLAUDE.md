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

## STRICT TESTING AND USAGE POLICY

Minimise Claude usage and unnecessary tool execution. Testing validates changed work; it does not repeatedly prove the whole repository. These rules override older instructions (docs, reports, phase plans) that call for broad verification, repeated passes, exhaustive review or automatic phase-boundary full-suite testing.

1. **Never automatically run a full suite** (all backend, all frontend, all Playwright, all repository tests). A phase boundary does not justify one. Report what was not run, e.g. `Not run: full backend suite, full Playwright suite. Targeted affected tests passed.` The user decides when a full regression run is worthwhile.
2. **Test only changed areas**: smallest relevant set (ticket change: ticket tests; Knowledge: Knowledge tests; portal UI: affected Vitest files; SignalR: realtime tests). No unrelated projects.
3. **Never rerun a passing test** in the same task: not for confidence, not after documentation changes.
4. **On failure**: inspect, fix, rerun ONLY the failing test/class/file; once it passes, run the smallest directly affected group once. Never jump to the whole suite.
5. **No repeat browser runs to prove stability.** A passing Playwright flow is sufficient absent specific flakiness evidence. If one flakes: fix the assertion/wait, rerun that single test once.
6. **Full Playwright is opt-in.** Run only the affected flow; no browser flows for backend-only changes.
7. **Targeted builds**: frontend build only for frontend work when needed; backend project build only for backend work when needed.
8. **Docker image builds are not routine tests.** Rebuild only if the Dockerfile or runtime dependencies changed, the user asks, or a container-specific problem is under investigation.
9. **Migration round-trip only when schema changed**, and then only the affected migration path, not every historical module.
10. **No automatic scale testing** (load tests, large-row benchmarks, EXPLAIN benchmarks, soak tests) unless the task changes a performance-critical query, there is a demonstrated concern, or the user asks.
11. **No repeated audits.** Review only changed files, directly affected contracts and directly affected security boundaries. No repository-wide review after each feature.
12. **Documentation changes never trigger retesting.**
13. **Avoid redundant tool calls.** Before each command ask whether it gives new information needed for the task. No reassurance commands.
14. **Keep output small**: quiet/minimal runner output (filtered for .NET), verbose only when investigating a failure, short log excerpts only.
15. **Do not re-verify report claims** with a second pass through the code. Write reports from the work just done; inspect only where uncertain.
16. **Stop once acceptance criteria are met and targeted tests pass.** No extra polish, extra tests, benchmarks, security audits, suites, container rebuilds or repeated browser checks. Update relevant docs and report.
17. **Security testing stays targeted** to the changed boundary (e.g. internal-note change: note REST and note realtime authorization tests, not every department-isolation test).
18. **Full regression needs explicit permission.** If one seems necessary, stop and say: `This change is broad enough that I recommend a full regression run. It will use additional time/usage. Shall I run it?`
19. **Normal task shape**: implement, ONE targeted test run, on failure fix and ONE focused rerun, relevant docs, stop.
20. **Usage efficiency is a project requirement.** Prefer fewer commands, smaller outputs, targeted tests, sequential work and fewer rereads over repeated checking.

Commands: Backend `dotnet test tests/<Project> --filter ...`. Frontend `npm --prefix src/web test -- <path>`.

## Commands
- Deps: `docker compose up -d` (PostgreSQL; object store opt-in via `--profile storage`)
- API: `dotnet run --project src/Helpdesk.Host`
- Web: `npm --prefix src/web run dev`
