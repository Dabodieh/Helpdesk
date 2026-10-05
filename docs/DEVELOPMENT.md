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
The `.env.example` Postgres password matches the Development connection string; override with `Database__ConnectionString` if you change it.

Health: `GET /health/live` (process), `GET /health/ready` (includes PostgreSQL). OpenAPI (Development): `/openapi/v1.json`.

## Configuration
ASP.NET options pattern; environment variables override JSON (`Database__ConnectionString`). Secrets: user-secrets or environment only. Microsoft Graph and Entra settings are added in Phases 1 and 3 and will be documented here.

## Database and migrations
No schema yet (Phase 1 introduces module DbContexts). Migrations will be applied by an explicit command, not on web startup in production.

## Tests (targeted)
```bash
dotnet test tests/Helpdesk.Host.Tests            # or add --filter
npm --prefix src/web test -- src/App.test.tsx
```
Run the full suite only at milestones (see `docs/plans/DEVELOPMENT_PLAN.md`).

## Local object storage
MinIO console at http://localhost:9001 (credentials from `.env`). Used from Phase 3.

## Debugging
Serilog writes structured logs to the console. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export traces.
