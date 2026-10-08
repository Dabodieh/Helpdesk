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

## Not covered here
Data Protection key persistence, backup and restore, monitoring and the malware scanner are separate gates (see the remediation status document).
