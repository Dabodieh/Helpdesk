# Production deployment

Reference topology (ADR-020): `docker/production/` runs one Helpdesk container (SPA + API + SignalR + jobs), PostgreSQL 18 and Nginx. Object storage is an external S3-compatible service. Microsoft Entra ID and the Graph application registration are prerequisites (`GRAPH_TENANT_VALIDATION.md`).

## First deployment
1. `cp docker/production/.env.example docker/production/.env` and fill every value. Put the certificate chain and key in `docker/production/tls/fullchain.pem` and `privkey.pem` (git-ignored).
2. `docker compose -f docker/production/compose.yml --env-file docker/production/.env build`
3. Apply migrations: `... run --rm migrate` (see "Migrations").
4. `... up -d`. The first sign-in of the `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT` identity becomes the Platform Admin; mailboxes are then registered by a Platform Admin (ADR-019).

## Settings the host insists on (it refuses to start otherwise)
`Proxy__KnownProxies` / `Proxy__KnownNetworks` (the proxy's address; compose pins Nginx at 172.29.0.10), `AllowedHosts` (public host name), `Storage__Provider=S3`, `Attachments__RequireCleanScan=true`, Entra, Graph and S3 credentials. `Realtime__AllowedOrigins__0` is the public https origin of the site.

## Headers
Only the application sets security headers (CSP, framing, nosniff, referrer policy). Do not add them in Nginx.

## Migrations
One database, one database role. `... run --rm migrate` applies the module migrations in the registered order: audit (10), identity (20), organisation (30), then the modules that reference them (ticketing, attachments, mail, knowledge; the `Every_cross_schema_foreign_key_points_at_a_module_that_migrates_earlier` test enforces that every cross-schema foreign key points at an earlier module). Run it before starting a new version. **Rollback is restore, not down-migrations:** take a backup immediately before migrating; to roll back, stop the application and restore that backup into a new database (see `BACKUP_AND_RESTORE.md`).

## Sessions
The Data Protection key ring is stored in PostgreSQL (`identity.data_protection_keys`, application name `Helpdesk`), so sessions survive a restart or a replaced container, and the keys are in the database backup. Anyone who can read the database or its backup can read the keys: protect both (encrypted off-host backups, database access control).

## Project name and a second environment
The compose project name (`COMPOSE_PROJECT_NAME`, default `helpdesk`) names every volume and network. The default is also the name of the local development stack, so on a machine that runs development, run this file only under a different project name. Staging does exactly that: see `STAGING_DEPLOYMENT.md` and `scripts/ops/staging.sh`. Port, proxy address, subnet and TLS directory can be overridden with `HTTP_PORT`, `HTTPS_PORT`, `PROXY_IP`, `INTERNAL_SUBNET` and `TLS_DIR`; the defaults are unchanged.

## Topology
One application instance. Do not run two: background jobs, realtime presence and the mail loop assume a single process (ADR-021).

## Not covered here
Monitoring and the malware scanner are separate gates (see the remediation status document).
