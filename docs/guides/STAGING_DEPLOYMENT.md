# Staging deployment

Staging runs the production topology (`docker/production/compose.yml`: one application container, PostgreSQL, Nginx with TLS) under its **own** compose project (`helpdesk-staging`), volumes, network, secrets and test integrations. It must never share a database, bucket, Entra registration or mailbox with production or development. Read `PRODUCTION_DEPLOYMENT.md` for the reasons behind the topology; this guide is the staging-specific path.

## What keeps staging separate
| Concern | How |
|---------|-----|
| Containers, volumes, network | Compose project `helpdesk-staging` (volume `helpdesk-staging_pgdata`, network `helpdesk-staging_internal`). `scripts/ops/staging.sh` is the only supported entry point: it fixes the project name, always uses `docker/staging/.env`, and refuses anything that would delete volumes (`-v`, `--volumes`, `--rmi`, `prune`). |
| Development environment | The development stack is project `helpdesk` (`docker-compose.yml`, volumes `helpdesk_pgdata`, `helpdesk_objectdata`). The production compose file's default project name is also `helpdesk`: **never run it without the staging project name** on a machine that has the development stack. |
| Secrets | `docker/staging/.env` and `/etc/helpdesk-staging/backup.env`, both git-ignored / outside the repository. Staging uses its own PostgreSQL password, backup passphrase and storage key. |
| Integrations | A TEST Entra registration, a TEST Microsoft 365 tenant and mailboxes, a staging-only bucket. |
| Data Protection keys | Stored in the staging database (`identity.data_protection_keys`), so they persist with the volume and travel with its backups. Nothing to supply. They are not encrypted at rest: protect the database and backups. |
| Attachments | Disabled by configuration (`Attachments__Enabled=false`, enforced by the host in Production mode) until a malware scanner exists. |

## What you must supply before staging can run
| Item | Where it goes | Notes |
|------|---------------|-------|
| A Linux host with Docker Engine + Compose v2 | n/a | Ports 80 and 443 free (or choose `HTTP_PORT`/`HTTPS_PORT`; the HTTP to HTTPS redirect assumes standard ports, so use custom ports only on a shared test host). 2 vCPU / 4 GB is enough. |
| A DNS name that resolves to the host | `PUBLIC_HOST` | e.g. `helpdesk-staging.<your-domain>` |
| A TLS certificate chain and key for that name | `docker/staging/tls/fullchain.pem`, `privkey.pem` (git-ignored; `TLS_DIR`) | From your CA or ACME client. Renewal is yours (Nginx reads the files at start: restart nginx after renewing). |
| A **test** Entra app registration (web, single tenant) | `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `ENTRA_CLIENT_SECRET` | Redirect URI `https://<PUBLIC_HOST>/signin-oidc`. Secret expiry date noted. |
| The object id (oid) of the first Platform Admin | `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT` | The identity that signs in first with this oid becomes Platform Admin. |
| A **test** Microsoft Graph app registration restricted to the test shared mailboxes | `GRAPH_TENANT_ID`, `GRAPH_CLIENT_ID`, `GRAPH_CLIENT_SECRET` | Follow `GRAPH_TENANT_VALIDATION.md` section 1 (least privilege, Exchange application access policy). Two shared mailboxes plus one outside-scope control mailbox. |
| An S3-compatible endpoint, a **staging-only** bucket and key | `S3_ENDPOINT`, `S3_BUCKET` (not `helpdesk`), `S3_ACCESS_KEY`, `S3_SECRET_KEY` | The host needs the store at start (readiness checks it) even though attachments are disabled. |
| A strong PostgreSQL password | `POSTGRES_PASSWORD` | Generated, unique to staging. |
| Backup passphrase file and off-host backup storage | `/etc/helpdesk-staging/backup.pass`, `BACKUP_DIR` mounted or synced off the host | Generate with `openssl rand -base64 48`; keep a second copy off the host. |
| An alert route for failed backups (optional but recommended) | `BACKUP_ALERT_COMMAND` | Mail, chat webhook or pager command. |
| A named staging operator | n/a | Runs the verification and the Graph validation session. |

## First deployment
1. **Get the code** on the host at `/opt/helpdesk` at the commit you intend to test (record it): `git clone` then `git checkout <commit>`. Do not deploy from a working tree with local edits. (The Graphite redesign is deployable once it is merged; staging simply builds whatever commit is checked out.)
2. **Configuration:** `cp docker/staging/.env.example docker/staging/.env`, fill every value, set `HELPDESK_VERSION` to the commit id. Put the certificate files in `docker/staging/tls/`. Then `scripts/ops/staging.sh check` must print `staging configuration complete` (it lists missing values by name, never their contents).
3. **Validate compose:** `scripts/ops/staging.sh config -q` (no output = valid).
4. **Build:** `scripts/ops/staging.sh build`.
5. **Back up before migrating** (first deployment: nothing to back up yet; every later deployment: run `scripts/ops/backup-all.sh` with the staging backup environment first, see "Backups").
6. **Start the database and migrate:** `scripts/ops/staging.sh up -d postgres`, then `scripts/ops/staging.sh migrate`. The migrate container applies every module migration in order and exits; the output ends with `All module migrations applied`.
7. **Start everything:** `scripts/ops/staging.sh up -d`.
8. **Verify:** `scripts/ops/staging.sh verify` (before DNS exists: `STAGING_RESOLVE_IP=<host ip> scripts/ops/staging.sh verify`). It checks containers, HTTP to HTTPS redirect, readiness (database, migrations, object store), liveness, API authentication, the dev sign-in route being absent, the Hangfire dashboard not being anonymous, HSTS and nosniff headers, certificate validity, the five recurring background jobs, recent application errors, and backup freshness if `BACKUP_STATUS_FILE` is set. Every line must be PASS.
9. **Sign in** as the bootstrap Platform Admin through Entra and create a test Department, mailbox registration (Platform Admin) and users. Then run the Graph validation session (`GRAPH_TENANT_VALIDATION.md` section 0).
10. **Run the SLA alert backfill** once if the database had tickets before the SLA alert migrations (`SLA_ALERT_BACKFILL.md`); on a new staging database there is nothing to backfill.

## Backups (hourly)
1. `mkdir -p /etc/helpdesk-staging /var/backups/helpdesk-staging`; create `/etc/helpdesk-staging/backup.env` from `docker/staging/backup.env.example` (mode 0600) and the passphrase file.
2. `cp scripts/ops/systemd/helpdesk-staging-backup.{service,timer} /etc/systemd/system/ && systemctl daemon-reload && systemctl enable --now helpdesk-staging-backup.timer`.
3. Check: `systemctl start helpdesk-staging-backup.service`, then `BACKUP_STATUS_FILE=/var/backups/helpdesk-staging/last-backup.json scripts/ops/check-backup-freshness.sh`. Add that command to your monitor.
4. Make `/var/backups/helpdesk-staging` land off the host (mount or sync). Then follow the restore drill in `BACKUP_AND_RESTORE.md` against staging (it is the safe place to do the first real drill).

The backup scripts reach the right database through `COMPOSE_PROJECT_NAME`, `COMPOSE_FILE` and `ENV_FILE` in `backup.env`; do not point them at any other environment.

## Updating staging
1. Back up. 2. `git fetch && git checkout <new commit>`; set `HELPDESK_VERSION`. 3. `staging.sh build`. 4. `staging.sh migrate`. 5. `staging.sh up -d`. 6. `staging.sh verify`.

## Rollback
Rollback is restore, not down-migration (decision D5).
1. `staging.sh stop helpdesk nginx`.
2. Restore the backup taken just before the update into a NEW database with `scripts/ops/restore-database.sh <backup> helpdesk_restored` (staging backup environment loaded).
3. With the application stopped, swap the databases: `scripts/ops/staging.sh exec -T postgres psql -U helpdesk -d postgres -c "ALTER DATABASE helpdesk RENAME TO helpdesk_failed" -c "ALTER DATABASE helpdesk_restored RENAME TO helpdesk"`.
4. Check out the previous commit, set `HELPDESK_VERSION` to it, `staging.sh build`, `staging.sh up -d`, `staging.sh verify`.
5. Keep `helpdesk_failed` until the cause is understood. The staging volume is never deleted by these scripts.

## Stopping and tearing down
`staging.sh stop` and `staging.sh down` keep volumes. Deleting staging data is deliberately manual (`docker volume rm helpdesk-staging_pgdata` on the staging host, after a backup and only by the owner); the wrapper refuses `-v`.

## Known limits of this staging
- Attachments are off; the UI still shows attachment controls until the redesign hides them when disabled (recorded in `docs/plans/BACKLOG.md`).
- No malware scanner (D-08). Retention and pruning are not approved (D-09): watch table growth.
- Single instance only (ADR-021). No HA.
- Nothing here has been run on a real host: the configuration was validated with `docker compose config`, `nginx -t`, the production settings guard and script syntax checks using placeholder values (see the readiness report).
