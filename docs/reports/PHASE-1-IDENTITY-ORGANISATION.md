# Phase 1 report: identity and organisation

Status: **complete with noted limitations.** Phase 2 has not been started.

## Implemented
- **Backend (earlier commits):** Entra OIDC + cookie session + dev sign-in, user provisioning, bootstrap platform admins, CSRF; departments, teams, department/team memberships, four system roles, permission catalogue (administrative vs content), `IAuthorizer` + ASP.NET policy handler; append-only audit (DB trigger); per-module schemas/migrations and `migrate` runner; architecture tests for module boundaries and endpoint authorization metadata.
- **Admin UI (this session, `src/web`):** authenticated shell and protected routes; departments list/create/detail/edit; teams list/create/edit/activate; department members (lookup, add, role change, remove); team members; loading/empty/error states; problem-details mapping; CSRF handling; permission-aware controls (UX only).
- **Isolation matrix:** `tests/Helpdesk.Host.Tests/Organisation/IsolationMatrixTests.cs`.
- **Graph spike:** `EMAIL_ARCHITECTURE.md` findings section, ADR-011 (Proposed), `docs/guides/GRAPH_TENANT_VALIDATION.md`, risk R-04, mock tests `tests/Helpdesk.GraphSpike.Tests`.
- **Infra fixes found by verification:** `docker/host.Dockerfile` now copies `.editorconfig` (the Release image build failed on CA1861 in migrations without it); MinIO moved behind a compose `storage` profile.

## What was tested and results
Run in a `mcr.microsoft.com/dotnet/sdk:10.0` container against compose PostgreSQL 18.6 (no .NET 10 SDK on this machine). One broad phase-boundary run:

| Suite | Result |
|---|---|
| `Helpdesk.Host.Tests` (integration, real migrations) | 317/317 pass (includes 150 isolation-matrix cases) |
| `Helpdesk.ArchitectureTests` | 26/26 pass |
| `Helpdesk.GraphSpike.Tests` (mocks) | 41/41 pass |
| Web: vitest / oxlint / `tsc -b && vite build` | 19/19 pass / clean / builds |

Live smoke: the built host image served the API on PostgreSQL 18; the Vite dev UI signed in via dev-login and created a department against the real API (CSRF + cookie). The UI was otherwise tested with mocked fetch only; no browser E2E suite exists.

## Security / isolation guarantees verified
See `docs/security/SECURITY.md` ("Phase 1 verified isolation guarantees"). Summary: 15 endpoints x 9 personas return the exact contract status; denied calls leave the DB unchanged; 404s for non-members, wrong-department and guessed ids are indistinguishable from non-existent ids; platform admin gets administrative but never content access; teams are not a visibility boundary; body/query department ids cannot override the route; lists, counts and audit do not leak; CSRF/session enforced. No cross-department defect found.

## Runtime / Docker / PostgreSQL
- PostgreSQL 18.6 healthy via Compose (host port had to be 5433 locally because 5432 was occupied).
- Host image builds (after the `.editorconfig` fix), `migrate` applies audit, identity, organisation in order and is idempotent; `/health/live` and `/health/ready` healthy.
- Down to 0 then up again succeeded for every module on a scratch database (10 tables after up; schemas were not inspected between down and up).
- `docker compose up -d` works for Postgres; MinIO is not pullable and is opt-in (D-17). MinIO was not otherwise investigated (unused in Phase 1).
- Not done: web Dockerfile/reverse proxy (none exists), CI workflow not executed on GitHub.

## Graph spike findings
Documented in `EMAIL_ARCHITECTURE.md` in three classes: verified from Microsoft documentation (cited), covered by mocks (ordered correlation rules, same-mailbox/department isolation, idempotency, duplicate/out-of-order notifications, clientState, lifecycle mapping), and **requires real Microsoft 365 test mailbox** (checklist E-1..E-38). Key points: never rely on `conversationId` or the subject token alone; use immutable ids on every request and the subscription; `x-` headers do not come back in customer replies so threading depends on Exchange setting `In-Reply-To`/`References` on reply drafts (untested, MIME fallback documented); mail subscriptions expire in under 7 days (1 day with resource data); duplicates and missed events need delta reconciliation; scope the app with Exchange RBAC for Applications and remove tenant-wide `Mail.*` consent. **No live validation was possible.** R-04 stays OPEN; ADR-011 stays Proposed.

## Requires real-tenant validation
Entra OIDC sign-in end to end; the whole Graph checklist (immutable-id stability, draft `internetMessageId`, from/sender under app-only access, reply headers and MIME fallback, Sent Items correlation, `conversationId` behaviour, cross-mailbox isolation, subscription renewal and throttling).

## Unresolved decisions
D-04 RLS (re-evaluate before Phase 2 ticket tables), D-08, D-09, D-11, D-12, D-13 (Data Protection keys), D-14, D-15 (late reply on closed ticket), D-16 (accept ADR-011), D-17 (object store image).

## Known limitations
- Real Entra tenant never exercised; sessions end on container restart (D-13).
- UI: 409 and CSRF failures detected by message heuristics; platform-admin permission list hardcoded; no audit or platform-admin screens; no browser E2E; no visual/accessibility audit beyond semantic markup and unit tests.
- User lookup searches all active users (id/name/email only).
- No rate limiting, no separate DB roles, EF department query filter deferred to Phase 2.
- ADR-011 spike deviation (superseded in Phase 2): the spike let the subject key attach for known participants; Phase 2 decided a subject reference is a hint only and never attaches on its own (see EMAIL_ARCHITECTURE.md).
- Backend tests could not be run on the host directly; they ran in a Docker SDK 10 container.

## Readiness for Phase 2
Ready to start. Org/identity/authorization/audit foundations are tested; the department scope must become an EF query filter and `ticket-access-probe` must be replaced by real ticket endpoints with their own isolation matrix. Phase 2 does not depend on the real-tenant items; Phase 3 does (D-15, D-16, D-17).
