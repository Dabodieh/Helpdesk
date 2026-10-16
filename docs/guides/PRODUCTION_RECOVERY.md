# Production recovery and health checks (local Windows Docker Desktop)

Scope: the single-PC Production stack in the `helpdesk` Compose project (`app`, `postgres-prod`, `objectstore-prod`; the Cloudflare tunnel is separate and
always an explicit decision). Written after the 15 Oct 2026 outage; evidence in `docs/reports/PRE-PRODUCTION-MASTER-AUDIT-2026-10-15.md` sections 12 and 18.

## What happens after a restart

| Event | Behaviour |
|---|---|
| Windows Update restarts the PC | Docker Desktop (a per-user application) is stopped with the PC. Containers are killed with the VM; PostgreSQL then performs normal crash recovery (WAL replay) on its next start. |
| User signs in | Docker Desktop starts **only if** "Start Docker Desktop when you sign in" is enabled (it was disabled on 15 Oct and is now enabled: `AutoStart=true`, Startup entry approved). The Docker engine starts and brings back every container whose policy is `unless-stopped`. |
| Nobody signs in | Docker Desktop does not run, so Production stays down. Windows automatic sign-in was deliberately not enabled. **This is the limitation of this hosting arrangement:** unattended recovery after an overnight update restart is not guaranteed. A supervised host (Docker Engine as a service on Linux or a Windows Server) removes it. |
| Docker restarts containers | `postgres-prod`, `app` and `objectstore-prod` use `restart: unless-stopped`. `cloudflared` is `restart: "no"` on purpose: the tunnel never returns by itself. The Development `postgres` is `no`. |
| App starts before PostgreSQL | With the key-ring certificate required, the application fails closed after about a minute of connection retries and exits; Docker restarts it until the database answers. Observed live on 15 Oct: PostgreSQL ready 09:34:39 UTC, application ready 09:35:14 UTC, no manual action. Nothing is corrupted by this. |
| `docker kill` / `docker stop` | Docker treats both as a manual stop: the container is NOT restarted. Use `docker start`. A crash or `pg_ctl stop -m immediate` is restarted by the policy. |

## Commands (run in PowerShell 7 or Windows PowerShell, from the repository root)

```powershell
.\scripts\windows\health-check-production.ps1                 # read-only; exit code 1 when a check fails
.\scripts\windows\recover-production.ps1 -WhatIf              # shows what it would do
.\scripts\windows\recover-production.ps1                      # starts Docker Desktop if needed, then postgres-prod, object store, app; runs the health check
```

`recover-production.ps1` only starts existing containers. It never recreates, removes or migrates anything, refuses while the Development application runs, and does
not start the tunnel (`dev.ps1 tunnel-start -ConfirmPublic`, with approval).

## What the health check covers

Docker engine; the three containers (state, restart policy, health, restart count); tunnel state (informational); PostgreSQL accepting connections and the
application's sessions; `/health/live`, `/health/ready` (database, object store, migrations) and that dev sign-in is off; the application points at `postgres-prod`;
the S3 port from the application network; Hangfire recurring jobs on schedule, no failures in 60 minutes, activity in 10 minutes; each active mailbox's Graph
subscription state, expiry, last sync age and failure count (database state only, no call to Microsoft); newest Production backup age and checksum and its copy on the
second medium; free disk space. It does not send notifications and is not scheduled; running it from Task Scheduler or alerting on its exit code is a separate decision.

## Known limitations

- Windows restarts for updates can still take Production down until someone signs in (see above).
- A health check run on the same PC cannot report that the PC itself is off; that needs an external probe.
- The object store has no container healthcheck (adding one recreates the container); `/health/ready` and the S3 port check cover it.
- Backups are created by `scripts/ops/backup-all.sh` (Windows wrapper `backup-production.ps1`), see `PORTABLE_BACKUP_RECOVERY.md`; the Task Scheduler jobs, the independent alerting watchdog, required off-host copies and retention are implemented but not yet activated: see `WINDOWS_MONITORING_AND_BACKUP.md`. The check reports their age and checksum; it does not create them.
