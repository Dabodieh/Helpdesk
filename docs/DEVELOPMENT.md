# Developer guide

## Prerequisites
.NET SDK 10 (see `global.json`), Node 22+, Docker with Compose.

## First run
```bash
cp .env.example .env                 # local-only placeholder secrets
docker compose up -d                 # PostgreSQL 18 (5432), MinIO (9000 API, 9001 console)
dotnet run --project src/Helpdesk.Host   # http://localhost:5080  (Development config uses compose defaults)
npm --prefix src/web ci && npm --prefix src/web run dev   # http://localhost:5173 (proxies /api and /health)
```
In Development the host applies pending migrations on start (`Database:MigrateOnStartup`) and enables dev sign-in (see below). The `.env.example` Postgres password matches the Development connection string; override with `Database__ConnectionString` if you change it.

Health: `GET /health/live` (process), `GET /health/ready` (includes PostgreSQL). OpenAPI (Development): `/openapi/v1.json`.

## Configuration
ASP.NET options pattern; environment variables override JSON (`Database__ConnectionString`). Secrets: user-secrets or environment only. Microsoft Graph settings are added in Phase 3.

| Key | Meaning |
|---|---|
| `Database:ConnectionString` | PostgreSQL connection (required). |
| `Database:MigrateOnStartup` | Apply pending migrations when the web host starts. Default `false`; `true` only in `appsettings.Development.json`. Production uses the `migrate` command. |
| `Authentication:Entra:TenantId` / `ClientId` / `ClientSecret` | Single-tenant Entra ID app registration (code flow + PKCE). Either all three are set (GUIDs, secret non-empty) or none. The secret comes from user-secrets or the environment, never from a committed file. |
| `Authentication:DevSignIn:Enabled` | Development sign-in (see below). Startup fails if it is `true` outside the `Development` and `Testing` environments. |
| `Authentication:BootstrapPlatformAdminSubjects` | List of Entra object ids (`oid`) that become platform admins when first provisioned, or at sign-in when no active platform admin exists (recovery). Every grant is audited (`source: bootstrap`). Afterwards platform admins are managed through the API. |

Options are validated at startup: Entra settings may only be absent when `DevSignIn` is enabled, and partial or malformed Entra settings stop the host.

### Entra ID sign-in
Register a single-tenant web app in Entra ID with redirect URI `https://<host>/api/auth/signin-oidc` (locally `http://localhost:5080/api/auth/signin-oidc`), create a client secret, then:
```bash
dotnet user-secrets --project src/Helpdesk.Host set Authentication:Entra:TenantId <tenant-guid>
dotnet user-secrets --project src/Helpdesk.Host set Authentication:Entra:ClientId <client-guid>
dotnet user-secrets --project src/Helpdesk.Host set Authentication:Entra:ClientSecret <secret>
dotnet user-secrets --project src/Helpdesk.Host set Authentication:BootstrapPlatformAdminSubjects:0 <your-entra-object-id>
```
`GET /api/auth/login?returnUrl=/` starts the flow (the id token issuer and `tid` must match the configured tenant). The result is an HttpOnly, SameSite=Lax cookie session (`Secure` outside Development/Testing). Behind a reverse proxy set `Proxy:KnownProxies` so the scheme and client IP are trusted. The OIDC `state`/`nonce` correlation cookies and the session cookie are protected with ASP.NET Data Protection: the key ring is not persisted yet (open item, see the Phase 1 backend hand-over), so sessions end on every restart or container replacement until it is.

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

Run the full suite only at milestones (see `docs/plans/DEVELOPMENT_PLAN.md`).

## Local object storage
MinIO console at http://localhost:9001 (credentials from `.env`). Used from Phase 3.

## Debugging
Serilog writes structured logs to the console. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces.
