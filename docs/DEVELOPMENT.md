# Developer guide

## Prerequisites
.NET SDK 10 (see `global.json`), Node 22+, Docker with Compose.

## First run
```bash
cp .env.example .env                 # local-only placeholder secrets
docker compose up -d                 # PostgreSQL 18 (5432); object storage is opt-in: `--profile storage`
dotnet run --project src/Helpdesk.Host   # http://localhost:5080  (Development config uses compose defaults)
npm --prefix src/web ci && npm --prefix src/web run dev   # http://localhost:5173 (proxies /api and /health)
```
### Windows: canonical Compose project
On Windows, everything runs in one Docker Compose project, `helpdesk`: `web` (Vite with hot reload, 127.0.0.1:5173), `host` (Development backend, 127.0.0.1:5080) and `postgres` (127.0.0.1:5433, volume `helpdesk_pgdata`). Nothing runs on Windows outside Docker; do not start `npm run dev` there while `web` holds port 5173. Manage it with `scripts\windows\dev.ps1 start|stop|restart|update|status|logs [web|host|postgres]`:
- `web` bind-mounts `src/web` and keeps `node_modules` in the `web_node_modules` volume (the Windows `node_modules` is never used). Dependencies install on first start; after changing `package-lock.json` run `docker compose exec web npm ci`. File watching uses polling (`HELPDESK_WEB_POLLING`) because Docker Desktop bind mounts deliver no change events. API calls are proxied to `http://host:8080` over the Compose network.
- `stop` stops the three containers and removes nothing. `update` refuses unless `D:\DB Backup` (override with `HELPDESK_DEV_BACKUP_DIR`) holds a `helpdesk-*.dump.enc` with a `.sha256` newer than 24 hours (`-Force` skips), tags the running image `helpdesk-host:pre-<timestamp>` as the rollback point (retag it `local` and `docker compose up -d --no-deps host` to roll back), rebuilds `host` and recreates `host` and `web`. Development applies pending migrations when `host` starts. `postgres` is never recreated.
- The object store is not part of normal development (opt-in: `docker compose --profile storage up -d objectstore`).
- Development sign-in is on in this project: all published ports bind to 127.0.0.1; never expose 5173 or 5080 through a tunnel.

In Development the host applies pending migrations on start (`Database:MigrateOnStartup`) and enables dev sign-in (see below). The `.env.example` Postgres password matches the Development connection string; override with `Database__ConnectionString` if you change it.

Health: `GET /health/live` (process), `GET /health/ready` (PostgreSQL, the object store and migration state; a job backlog is a metric, not a readiness failure; see `docs/guides/OPERATIONS_RUNBOOK.md`). OpenAPI (Development): `/openapi/v1.json`.

## Configuration
ASP.NET options pattern; environment variables override JSON (`Database__ConnectionString`). Secrets: user-secrets or environment only. Mail (Graph), storage, attachment, rate-limit and proxy settings are documented in the sections below and in `docs/guides/PRODUCTION_DEPLOYMENT.md`.

| Key | Meaning |
|---|---|
| `Database:ConnectionString` | PostgreSQL connection (required). |
| `Database:MigrateOnStartup` | Apply pending migrations when the web host starts. Default `false`; `true` only in `appsettings.Development.json`. Production uses the `migrate` command. |
| `Authentication:Entra:TenantId` / `ClientId` / `ClientSecret` | Single-tenant Entra ID app registration (code flow + PKCE). Either all three are set (GUIDs, secret non-empty) or none. The secret comes from user-secrets or the environment, never from a committed file. |
| `Authentication:DevSignIn:Enabled` | Development sign-in (see below). Startup fails if it is `true` outside the `Development` and `Testing` environments. |
| `Authentication:BootstrapPlatformAdminSubjects` | List of Entra object ids (`oid`) that become platform admins when first provisioned, or at sign-in when no active platform admin exists (recovery). Every grant is audited (`source: bootstrap`). Afterwards platform admins are managed through the API. |

Options are validated at startup: partial or malformed server Entra settings stop the host. With no Entra configuration, the app starts but Microsoft sign-in is unavailable (ADR-037). Initial production bootstrap still requires server settings; an authenticated platform administrator can subsequently manage database-backed setup from Administration > Microsoft 365. Development sign-in is never permitted in production.

### Entra ID sign-in
Register a single-tenant web app in Entra ID with redirect URI `https://<host>/api/auth/signin-oidc` (locally `http://localhost:5080/api/auth/signin-oidc`), create a client secret, then:
```bash
dotnet user-secrets --project src/Helpdesk.Host set Authentication:Entra:TenantId <tenant-guid>
dotnet user-secrets --project src/Helpdesk.Host set Authentication:Entra:ClientId <client-guid>
dotnet user-secrets --project src/Helpdesk.Host set Authentication:Entra:ClientSecret <secret>
dotnet user-secrets --project src/Helpdesk.Host set Authentication:BootstrapPlatformAdminSubjects:0 <your-entra-object-id>
```
`GET /api/auth/login?returnUrl=/` starts the flow (the id token issuer and `tid` must match the configured tenant). The result is an HttpOnly, SameSite=Lax cookie session (`Secure` outside Development/Testing). Behind a reverse proxy set `Proxy:KnownProxies` or `Proxy:KnownNetworks`; with neither set no forwarded header is honoured (production refuses to start without one, ADR-020). The OIDC `state`/`nonce` correlation cookies and the session cookie are protected with ASP.NET Data Protection: the key ring persists in PostgreSQL (`identity.data_protection_keys`) and travels with database backups, so it survives restarts and container replacement. The key XML is not independently encrypted at rest: protect the database and its backups.

### Development sign-in
`Development` enables it by default (`appsettings.Development.json`, bootstrap subject `dev-admin`). Open
`http://localhost:5080/api/auth/dev-login?subject=dev-admin&name=Dev%20Admin&email=dev@example.test&returnUrl=/` to get a session cookie
for that identity; any other `subject` creates a normal user with no access. It issues the same cookie principal as Entra and uses the same
provisioning, so authorization behaves identically. The route does not exist when disabled.

### CSRF
Every POST/PUT/PATCH/DELETE needs the header `X-CSRF-TOKEN` with the token from `GET /api/csrf`. The token is bound to the signed-in user, so fetch it **after** sign-in (and again after sign-out/sign-in).

## Database and migrations
One PostgreSQL schema per module (`audit`, `identity`, `organisation`), each with its own EF Core DbContext and migrations history table (`<schema>.__ef_migrations_history`). Cross-schema foreign keys (e.g. memberships to `identity.users`) are created in migration SQL, so modules migrate in dependency order: audit, identity, organisation.
```bash
dotnet run --project src/Helpdesk.Host -- migrate        # apply all pending migrations in order, then exit (also: docker run ... helpdesk-host migrate)
dotnet tool restore                                        # installs dotnet-ef from dotnet-tools.json
# add a migration (one per module context; the Host is the startup project):
dotnet ef migrations add <Name> --project src/Helpdesk.Modules.Organisation --startup-project src/Helpdesk.Host --context OrganisationDbContext --output-dir Persistence/Migrations
# roll back one module to a migration (or 0), most dependent module first:
HELPDESK_DESIGN_DB="Host=localhost;Port=5432;Database=helpdesk;Username=helpdesk;Password=<pw>" \
  dotnet ef database update 0 --project src/Helpdesk.Modules.Organisation --startup-project src/Helpdesk.Host --context OrganisationDbContext
```
`HELPDESK_DESIGN_DB` is only read by EF tooling (design-time factories); it defaults to a connection-less placeholder that is enough to scaffold migrations. Migration classes are generated `public`; change them to `internal` (and keep any hand-written SQL, such as expression indexes, cross-schema foreign keys and triggers) before committing. The web host never migrates itself in production.

## Tests (targeted)
```bash
dotnet test tests/Helpdesk.Host.Tests --filter "FullyQualifiedName~Organisation"   # integration + unit tests for the modules
dotnet test tests/Helpdesk.ArchitectureTests                                       # module boundaries, endpoint authorization metadata
npm --prefix src/web test -- src/App.test.tsx
```
Integration tests need PostgreSQL (`docker compose up -d`). Set `HELPDESK_TEST_DB` to a **server** connection string without a database (default `Host=localhost;Port=5432;Username=helpdesk;Password=helpdesk_dev`, which matches `.env.example`). Each test fixture creates a uniquely named database (`helpdesk_test_<guid>`), runs the real migrations through the `migrate` runner and drops the database afterwards; the dev database is never touched. The user needs `CREATEDB`.

Writing integration tests: use `HelpdeskFixture` (`tests/Helpdesk.Host.Tests/Infrastructure`) as an xUnit class fixture. It starts the app in the `Testing` environment with dev sign-in enabled and the bootstrap platform admin `platform-admin`, and offers `SignInAsync(subject)` (real dev-login cookie + CSRF token), `PlatformAdminAsync()`, `NewUserAsync()`, `CreateDepartmentAsync()`, `NewMemberAsync(department, role)`, `CreateTeamAsync()`, raw SQL helpers and `WithScopeAsync` for service-level tests. Tests in one class share a database, so use unique names (`Unique.Id()`, `Unique.Key()`).

Do not run a full suite unless it is explicitly requested for the task: run only the tests directly affected by the change (project rules in `CLAUDE.md`). .NET 10 can run in the `mcr.microsoft.com/dotnet/sdk:10.0` container when no local SDK exists.

### Browser end-to-end tests (Playwright)
`src/web/e2e` holds Playwright specs for the agent workspace. They are **not** part of `npm test` (vitest only collects `src/**/*.test.*`) and they need the real stack:
```bash
docker compose up -d
dotnet run --project src/Helpdesk.Host            # Development: dev sign-in enabled, bootstrap platform admin dev-admin
npm --prefix src/web run dev                      # http://localhost:5173 (proxies /api)
npm --prefix src/web ci                           # once
npm --prefix src/web exec playwright install chromium   # once
# no download possible? reuse an installed browser: E2E_BROWSER_CHANNEL=chrome (or msedge) npm --prefix src/web run e2e
npm --prefix src/web run e2e                      # add -- --headed to watch
```
The config does not start a web server. `E2E_BASE_URL` overrides the target (default `http://localhost:5173`). The spec seeds its own data through the API with dev-login users (a new department with a unique key, a team, a TeamLead and an Agent, two tickets) and needs the `dev-admin` bootstrap platform admin, so it never touches existing data and can be rerun.

## Local object storage
SeaweedFS (S3-compatible, local dev only) is opt-in: `docker compose --profile storage up -d` exposes the S3 API on `127.0.0.1:${S3_PORT:-8333}` with the access/secret keys from `.env` (`S3_ACCESS_KEY`, `S3_SECRET_KEY`). The application only needs an S3-compatible endpoint; nothing uses it before Phase 3.

## Debugging
Serilog writes structured logs to the console. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces.

## Local environment notes (verified Phase 1)
- If host port 5432 is taken, set `POSTGRES_PORT` in `.env` (e.g. 5433) and use the same port in `Database__ConnectionString` / `HELPDESK_TEST_DB`.
- Object storage (SeaweedFS) is opt-in (`--profile storage`); the old public `minio/minio` image is no longer pullable (D-17 resolved).
- Host image: `docker build -f docker/host.Dockerfile -t helpdesk-host:local .`; migrate with `docker run --rm --network <compose-net> -e Database__ConnectionString=... helpdesk-host:local migrate`. In `Development` inside a container also set `Urls=http://+:8080` (appsettings.Development.json binds localhost:5080).
- No local .NET 10 SDK? Run any dotnet command in `mcr.microsoft.com/dotnet/sdk:10.0` with the repo mounted and the compose network attached (set `HELPDESK_TEST_DB` to the compose service host).
- Expected noise: `migrate` logs EF "Failed executing DbCommand" at ERR for the history-table probe on a fresh database, and Npgsql may print `libgssapi_krb5.so.2` on slim images; both are harmless.

## Demo data
`node scripts/dev-demo-seed.mjs` creates a demonstration department (key DEMO) and `docker exec -i helpdesk-postgres-1 psql -U helpdesk -d helpdesk < scripts/dev-demo-reset.sql` removes exactly that department again (local development database only). `node scripts/dev-seed.mjs [apiBase]` seeds a LOCAL Development API (dev sign-in must be enabled) with an IT department, two teams, agents `alice`, `bob`, `carol` (TeamLead) plus `dev-admin`, and 12 realistic tickets with conversations. Idempotent: it leaves existing tickets alone.

## Email (Phase 3)
Development uses the in-process mailbox simulator (`Mail:Provider=Fake`, `Storage:Provider=InMemory`): sign in as `dev-admin`, create a mailbox in Administration (or run `node scripts/dev-seed.mjs`), then inject mail with `POST /api/dev/mail/inbound` and answer sent mail with `POST /api/dev/mail/customer-reply`. Hangfire runs in the host; dashboard (platform admin, read-only) at `/hangfire`.
Real tenant: `Mail:Provider=Graph`, `Mail:NotificationBaseUrl` (public https), `Mail:Graph:TenantId/ClientId` and `ClientSecret` or `CertificatePath` (user-secrets or environment only), then follow `docs/guides/GRAPH_TENANT_VALIDATION.md`. S3: `Storage:Provider=S3` with `Storage:S3:Endpoint/Bucket/AccessKey/SecretKey` (local: `docker compose --profile storage up -d`). Tests that need a real S3 endpoint run when `HELPDESK_TEST_S3_ENDPOINT/ACCESS_KEY/SECRET_KEY` are set. Production defaults require explicit Graph and S3 settings (the host refuses to start without them), `Attachments:RequireCleanScan=true`, and `Attachments:Enabled=false` until a malware scanner is integrated.
