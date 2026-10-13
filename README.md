# Helpdesk

Helpdesk is a UK-focused internal service desk and ITSM platform. Departments manage their own tickets, request forms, shared mailboxes, service levels and automation within one application. Department membership is the hard access boundary; a Platform Admin does not automatically gain access to Department ticket content.

**Status (13 October 2026):** The core product and reporting are implemented, with targeted security remediation under way. It is **not production ready**. Live Microsoft Graph validation, a recorded restore drill and organisational retention decisions remain open. Attachments are disabled in production until a malware scanner is integrated. See the [production readiness report](docs/reports/PRODUCTION-READINESS-2026-10-08.md) and [current security remediation status](docs/reports/SECURITY-REMEDIATION-STATUS-2026-10-13.md).

## What is built

- A queue-first agent workspace with Department-scoped tickets, assignment, status and priority, public replies, Internal Notes, followers, related tickets and merge.
- Requester self-service with Request Forms, audience controls, a portal, ticket history and knowledge articles.
- Microsoft Entra ID sign-in and Microsoft Graph integration for Exchange Online shared mailboxes, including inbound threading, outbound state handling and mailbox health.
- Department administration for members, mailboxes, ticket setup, business hours, service levels, automation, canned responses and macros.
- Search, saved views, realtime updates, durable notifications, an operational dashboard and historical reports.

Teams are no longer an active product concept: Departments provide the access boundary and assignment context. The [architecture](docs/architecture/ARCHITECTURE.md) and [project decisions](docs/architecture/decisions/) explain the model.

## Technology

The application is a modular monolith: React 19, TypeScript and Vite on the frontend; .NET 10 and ASP.NET Core on the backend; PostgreSQL 18 for data and search. It also uses EF Core, SignalR, Hangfire, Serilog and OpenTelemetry. The supported deployment is one application instance behind a reverse proxy.

## Run locally

Install .NET SDK 10, Node.js 22 or later, and Docker with Compose. From the repository root:

```bash
cp .env.example .env
docker compose up -d postgres
dotnet run --project src/Helpdesk.Host
npm --prefix src/web ci
npm --prefix src/web run dev
```

Open <http://localhost:5173>. The frontend proxies API requests to the backend at `http://localhost:5080`. On Windows PowerShell, use `Copy-Item .env.example .env` in place of `cp`. If you change the PostgreSQL port or credentials in `.env`, set `Database__ConnectionString` to match before starting the backend. The [developer guide](docs/DEVELOPMENT.md) covers configuration, development sign-in and targeted tests.

Development sign-in must stay on a private local instance. A Cloudflare Tunnel or public reverse proxy is not a production deployment or an authentication control.

## Documentation

| Topic | Guide |
|---|---|
| Product scope and current plan | [Product](docs/product/PRODUCT.md) · [Development plan](docs/plans/DEVELOPMENT_PLAN.md) |
| Architecture and permissions | [Architecture](docs/architecture/ARCHITECTURE.md) · [Permissions](docs/architecture/PERMISSIONS.md) |
| Deployment and operations | [Production deployment](docs/guides/PRODUCTION_DEPLOYMENT.md) · [Operations runbook](docs/guides/OPERATIONS_RUNBOOK.md) · [Backup and restore](docs/guides/BACKUP_AND_RESTORE.md) |
| Microsoft integration | [Graph tenant validation](docs/guides/GRAPH_TENANT_VALIDATION.md) |
| Security and delivery status | [Security audit](docs/reports/SECURITY-AND-COMPLIANCE-AUDIT-2026-10-09.md) · [Remediation status](docs/reports/SECURITY-REMEDIATION-STATUS-2026-10-13.md) |

The files under [design/redesign](docs/design/redesign/) are separate, unapproved interface studies; they are not the running application.
