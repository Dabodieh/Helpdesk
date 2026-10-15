# Portable Production backup and recovery

Applies to any Docker host: the temporary Windows Docker Desktop PC today, an Unraid server or a dedicated always-on Docker machine later. The backup logic is bash +
docker + openssl + coreutils; the only Windows-specific file is the optional wrapper `scripts/windows/backup-production.ps1`. Nothing here needs DPAPI, Task Scheduler
or automatic sign-in. The older reference-topology guide is `BACKUP_AND_RESTORE.md` (same scripts, compose mode).

## What is backed up

| Item | Script | Output (in `BACKUP_DIR`, prefix `helpdesk-prod` here) | Notes |
|---|---|---|---|
| PostgreSQL `helpdesk_prod` | `backup-database.sh` | `helpdesk-prod-<UTC>.dump.enc` + `.sha256` + `.meta.json` | `pg_dump --format=custom --no-owner --no-acl`, streamed into AES-256-CBC (PBKDF2, 600 000 iterations). Includes the Data Protection key ring, Hangfire state, Graph subscription records, audit trail. No plaintext on disk. |
| Object store (attachments) | `backup-objects.sh` | `helpdesk-prod-objects-<UTC>.tar.enc` + `.sha256` | `OBJECT_VOLUME=<docker volume>`: read-only mount, tar, encrypted. Empty while attachments are disabled (no scanner yet). Alternative S3 mode with the AWS CLI for a remote store. |
| Configuration bundle | `backup-config.sh` | `helpdesk-prod-config-<UTC>.tar.enc` + `.sha256` | `.env`, `.tunnel.env`, the secrets folder (Graph and Data Protection certificates and password files). Contains secrets: protected only by the passphrase. |
| Verification | `verify-backup.sh` (every run) | n/a | checksum, decryption, readable archive with data for `identity` and `ticketing`, no ACL/OWNER entries. |
| Isolated restore test | `restore-test-isolated.sh` | `restore-test-result.json` | disposable container, roles re-applied, least privilege checked, compared with live. Run after changes and at least quarterly. |

Not in these backups (recreated from source or held by the service): the application image (rebuild from the commit, tag `1.0.0-rc1-<sha>` and the revision label),
Cloudflare tunnel and Access configuration, Entra app registrations and secret, Exchange RBAC. Their identifiers and procedures are in
`CLOUDFLARE_PUBLIC_ACCESS.md`, `M365_TEST_TENANT_CHECKLIST.md` and `PRODUCTION_INTEGRATION_VALIDATION.md`.

## The passphrase (custody)

One passphrase protects all three file types. It must exist **outside the host**: in your password manager (as the exact 64-character string). The Windows DPAPI file is a
convenience for this PC only and is not needed to decrypt anywhere else. Never store the passphrase in the same folder or medium as the backups. Anyone with a backup
file and the passphrase can read everything it contains.

Decrypt by hand on any machine with OpenSSL 3 (write the passphrase to a file readable only by you, remove it afterwards):

```bash
openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -pass file:./passphrase.txt -in helpdesk-prod-<UTC>.dump.enc -out helpdesk.dump   # then: pg_restore --list helpdesk.dump
openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -pass file:./passphrase.txt -in helpdesk-prod-config-<UTC>.tar.enc | tar tf -     # list; use `tar xf -` to extract
```

## Running a backup

All settings are environment variables (see the header of `scripts/ops/backup-all.sh`): `BACKUP_PASSPHRASE_FILE`, `BACKUP_DIR`, `PG_CONTAINER`, `BACKUP_NAME_PREFIX`,
`OBJECT_VOLUME`, `CONFIG_PATHS` (`;`-separated), `BACKUP_COPY_DIRS` (`;`-separated extra destinations), `BACKUP_ALERT_COMMAND`.

```bash
# Linux / Unraid / any Docker host
export BACKUP_PASSPHRASE_FILE=/boot/config/helpdesk/backup.pass    # mode 0600, never in the repository
export BACKUP_DIR=/mnt/user/backups/helpdesk
export BACKUP_COPY_DIRS=/mnt/disks/offsite/helpdesk                # optional second medium
export PG_CONTAINER=helpdesk-postgres-prod-1 BACKUP_NAME_PREFIX=helpdesk-prod OBJECT_VOLUME=helpdesk_objectdata_prod
export CONFIG_PATHS="/opt/helpdesk/.env;/opt/helpdesk/.tunnel.env;/opt/helpdesk-secrets"
/opt/helpdesk/scripts/ops/backup-all.sh
```

```powershell
# Windows Docker Desktop (Git for Windows required); passphrase from DPAPI unless -PassphraseFile is given
.\scripts\windows\backup-production.ps1 -IncludeConfig
.\scripts\windows\backup-production.ps1 -PassphraseFile D:\secure\backup.pass -CopyDirs 'I:\HelpdeskBackups\prod'
```

Behaviour and exit codes: `0` ok; `1` failed (reason on stderr and in `last-backup.json`, no secrets, partial files removed); `75` another backup holds the lock. Nothing is ever
deleted at a destination and historical backups are never pruned automatically (apply your own retention deliberately). A failed copy or a checksum mismatch at a
destination fails the run. The status file feeds `check-backup-freshness.sh` and `health-check-production.ps1`.

## Scheduling (not configured; choose when the permanent host exists)

**cron** (Linux): `15 * * * * . /etc/helpdesk/backup.env && /opt/helpdesk/scripts/ops/backup-all.sh >> /var/log/helpdesk-backup.log 2>&1` (hourly matches RPO 1 h; daily is
`15 2 * * *`). **systemd**: `scripts/ops/systemd/helpdesk-backup.service` and `.timer` (set the variables above in `/etc/helpdesk/backup.env`).
**Unraid**: Settings > User Scripts > Add New Script, paste the Linux block above, schedule "Custom" with cron `15 * * * *`; keep the passphrase on the flash drive or a
protected share, not inside the backup share. Alert on non-zero exit with `BACKUP_ALERT_COMMAND=/path/to/notify.sh` (Unraid: `/usr/local/emhttp/webGui/scripts/notify`).
Monitor with `BACKUP_STATUS_FILE=<BACKUP_DIR>/last-backup.json scripts/ops/check-backup-freshness.sh`.

## Restore test (isolated, safe on a live host)

```bash
BACKUP_PASSPHRASE_FILE=... PG_CONTAINER=helpdesk-postgres-prod-1 RESTORE_TEST_RESULT=./result.json \
  scripts/ops/restore-test-isolated.sh /path/helpdesk-prod-<UTC>.dump.enc
```

It starts a throw-away PostgreSQL (no network, no volume), restores, applies `scripts/ops/prod-db-roles.sql`, proves the application role cannot create or truncate, compares
every stable table (row counts and content checksums) and the migration history with the live database (read-only), then removes the container. Differences in tables that
change continuously (Hangfire, mailbox sync) are counted but do not fail it. Take a fresh backup first when comparing with live, or expect "data differs" for changed tables.

## Disaster recovery on a new host (order matters)

1. Install Docker. Get the repository at the recorded commit and the image (build `docker/host.Dockerfile` or load the saved image) tagged as in `.env` (`HELPDESK_VERSION`).
2. Restore the configuration bundle: decrypt and extract it (command above). Put `.env`/`.tunnel.env` in the repository root and the secrets folder where `SECRETS_DIR` points
   (outside the repository). The certificates and their password files are in that bundle; the monthly certificate backups on the second medium are the fallback.
3. Start only the database: `docker compose --profile production up -d --no-deps postgres-prod` (creates an empty `helpdesk_prod` and the administrator role from `.env`).
4. Restore: `PG_CONTAINER=helpdesk-postgres-prod-1 BACKUP_PASSPHRASE_FILE=... scripts/ops/restore-database.sh <dump.enc> helpdesk_prod` (the empty target is accepted).
5. Re-create the application role (the archive has no grants): `docker exec -i helpdesk-postgres-prod-1 psql -U "$PROD_PG_ADMIN_USER" -d helpdesk_prod -v ON_ERROR_STOP=1
   -v app_user=helpdesk_app -v app_password="$PROD_APP_DB_PASSWORD" < scripts/ops/prod-db-roles.sql`.
6. Object store (skip while attachments are disabled and the bundle is empty): `openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -pass file:./passphrase.txt -in helpdesk-prod-objects-<UTC>.tar.enc | docker run --rm -i -v helpdesk_objectdata_prod:/data alpine tar xf - -C /data`, before the app starts.
7. Start the app: `docker compose --profile production up -d --no-deps app`, then `scripts/windows/health-check-production.ps1` (Windows) or `/health/ready` and the Hangfire
   and subscription checks. The key ring restores with the database; it needs the same Data Protection certificate (step 2). Mail catches up from the stored delta token; if the
   Graph subscription expired, use "Retry subscription" on the Department mailbox page.
8. Only then decide on the Cloudflare tunnel (separate approval); Access and webhook applications live in Cloudflare and are unaffected by a host move.

## Limits

- The off-PC protection is only as good as the destinations you configure; the same PC's second disk is not off-site.
- Object backup by volume is crash-consistent, not application-consistent (fine while attachments are off).
- The Windows wrapper briefly writes the passphrase to a user-only temporary file; use a portable `-PassphraseFile` on a protected path if that is not acceptable.
- Lock recovery: a crashed run blocks the next one for up to `BACKUP_LOCK_STALE_MINUTES` (180) unless `BACKUP_FORCE_UNLOCK=1`.
- Recovery time and the quarterly drill record belong in `BACKUP_AND_RESTORE.md` (drill log).
