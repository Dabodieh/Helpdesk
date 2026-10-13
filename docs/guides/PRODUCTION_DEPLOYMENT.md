# Production deployment

Reference topology (ADR-020): `docker/production/` runs one Helpdesk container (SPA + API + SignalR + jobs), PostgreSQL 18 and Nginx. Object storage is an external S3-compatible service. Microsoft Entra ID and the Graph application registration are prerequisites (`GRAPH_TENANT_VALIDATION.md`).

## First deployment
1. `cp docker/production/.env.example docker/production/.env` and fill every value. Put the certificate chain and key in `docker/production/tls/fullchain.pem` and `privkey.pem` (git-ignored).
2. `docker compose -f docker/production/compose.yml --env-file docker/production/.env build`
3. Apply migrations: `... run --rm migrate` (see "Migrations").
4. `... up -d`. The first sign-in of the `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT` identity becomes the Platform Admin; mailboxes are then registered by a Platform Admin (ADR-019).

## Settings the host insists on (it refuses to start otherwise)
`Proxy__KnownProxies` / `Proxy__KnownNetworks` (the proxy's address; compose pins Nginx at 172.29.0.10), `AllowedHosts` (public host name), `Storage__Provider=S3`, `Attachments__RequireCleanScan=true`, Entra, Graph and S3 credentials. `Realtime__AllowedOrigins__0` is the public https origin of the site. It also refuses: an empty `Database__ConnectionString`, `Database__MigrateOnStartup=true` (migrations are the explicit one-off `migrate` run), non-https `Realtime__AllowedOrigins`, and `Serilog__MinimumLevel__Default` of Debug or Verbose. Infrastructure rate limits (sign-in and OIDC per client address, Graph webhooks per address, invitations and administrative writes per user; section `RateLimits`) are always on in production; the numbers are provisional. `BIND_ADDRESS` (default `0.0.0.0`; the Windows example sets `127.0.0.1`) chooses the host interface that publishes the proxy. See `docs/reports/PRODUCTION-HARDENING.md`.

## Headers
Only the application sets security headers (CSP, framing, nosniff, referrer policy, Permissions-Policy denying camera/microphone/geolocation, and same-origin COOP/CORP). Do not add them in Nginx. Verify they survive the real proxy on pages, assets, API errors and sign-in redirects. Full-page Entra sign-in is supported; review the policy before introducing popup authentication or cross-origin resource consumers. HSTS and real-browser behaviour still require deployment evidence.

## Migrations
One database, one database role. `... run --rm migrate` applies the module migrations in the registered order: audit (10), identity (20), organisation (30), then the modules that reference them (ticketing, attachments, mail, knowledge; the `Every_cross_schema_foreign_key_points_at_a_module_that_migrates_earlier` test enforces that every cross-schema foreign key points at an earlier module). Run it before starting a new version. **Rollback is restore, not down-migrations:** take a backup immediately before migrating; to roll back, stop the application and restore that backup into a new database (see `BACKUP_AND_RESTORE.md`).

## Sessions
The Data Protection key ring is stored in PostgreSQL (`identity.data_protection_keys`, application name `Helpdesk`), so sessions survive a restart or a replaced container, and the keys are in the database backup. Anyone who can read the database or its backup can read the keys: protect both (encrypted off-host backups, database access control).

## Project name and a second environment
The compose project name (`COMPOSE_PROJECT_NAME`, default `helpdesk`) names every volume and network. The default is also the name of the local development stack, so on a machine that runs development, run this file only under a different project name. Staging does exactly that: see `STAGING_DEPLOYMENT.md` and `scripts/ops/staging.sh`. Port, proxy address, subnet and TLS directory can be overridden with `HTTP_PORT`, `HTTPS_PORT`, `PROXY_IP`, `INTERNAL_SUBNET` and `TLS_DIR`; the defaults are unchanged.

## Windows Docker Desktop
The same compose file runs on a Windows PC with Docker Desktop, driven by PowerShell with Task Scheduler backups instead of systemd: see `WINDOWS_DOCKER_DESKTOP.md`.

## Topology
One application instance. Do not run two: background jobs, realtime presence and the mail loop assume a single process (ADR-021).

## Not covered here
Monitoring and the malware scanner are separate gates (see the remediation status document).


## Invitation URL logging (2026-10-12)
New invitation links use `/activate#<token>`; the fragment never reaches the HTTP server. The browser keeps it in tab-scoped session storage through sign-in and uses `/activate` as the return URL. Existing `/activate/<token>` emails still work, but their initial request necessarily reaches the reverse proxy. Redact those legacy paths to `/activate` in proxy/access-log and tracing configuration, and restrict access to existing logs according to your policy. Application request-completion logs redact the legacy path. Do not enable request-body logging on invitation POST endpoints. This release does not scrub old logs.

## Data Protection key encryption at rest (optional, gate F-04)

The key ring in `identity.data_protection_keys` is plain by default: anyone who can read the database or a backup can read the keys (and with them forge sessions or decrypt the stored Entra secret). With a certificate configured the host encrypts **new** keys with the certificate's public key; reading them needs the private key, which lives only in a PFX file you supply.

| Setting (env name in compose) | Meaning |
|---|---|
| `DataProtection__CertificatePath` (`DP_CERT_PATH`) | In-container path of the PFX (RSA, with private key, not expired). |
| `DataProtection__CertificatePasswordFile` (`DP_CERT_PASSWORD_FILE`) | In-container path of a file holding the PFX password (preferred). `DataProtection__CertificatePassword` also works for non-production only. |
| `DataProtection__PreviousCertificatePaths__0..n` | Earlier certificates, used only to read keys they protected. |
| `DataProtection__RequireCertificate` (`DP_REQUIRE_CERT`) | `true` makes a production host refuse to start without a certificate. Off until the policy is approved. |

**Rules:** the PFX and the password file are never in Git or in an image; mount them read-only from a folder outside the repository whose NTFS permissions allow only the account that runs Docker, using a compose override (`volumes: ["C:\\HelpdeskSecrets\\dp:/run/dp:ro"]` on the `helpdesk` and `migrate` services). A configured certificate that is missing, expired, wrong-password or public-only stops the start; there is no fallback to plain keys, and no message prints a password.

**Existing keys are not touched.** Plain keys stay readable and are replaced by encrypted ones as the ring rolls (a key lives 90 days). Forcing every key to be re-protected means revoking the ring, which signs everyone out and must be approved first.

**Rotation:** create the new PFX; set `CertificatePath` to it and move the old one into `PreviousCertificatePaths`; restart. Remove the old certificate only when no key and no backup you may still restore depends on it.

**Backup and recovery:** a database backup now contains keys that only the PFX can open. Back the PFX and its password up separately from the database backup (for example the password manager and an offline copy) and include "restore the database, mount the PFX, start" in the restore drill. Losing the PFX makes the stored Entra secret unreadable (sign-in stays off until reconnected) and invalidates sessions.

**Generating a certificate** (any RSA 2048+ certificate works; it is not used for TLS): on Windows `New-SelfSignedCertificate -Subject "CN=Helpdesk key ring" -KeyExportPolicy Exportable -KeySpec KeyExchange -NotAfter (Get-Date).AddYears(3)` then `Export-PfxCertificate` with a password you keep in the password manager. Choosing the validity, custody and rotation owner is a decision for the operator and is not made by the application.
