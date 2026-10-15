# Backup and restore

> Portable local-Docker variant (PG_CONTAINER, object volume, configuration bundle, isolated restore test, Unraid/cron scheduling): see `PORTABLE_BACKUP_RECOVERY.md`. The scripts below are shared; backups are now made with `pg_dump --no-owner --no-acl` and PBKDF2 600 000 iterations.

Targets (decision D3): RPO 1 hour, RTO 4 hours. Backups are encrypted and kept off the Helpdesk host and its infrastructure. The database and the attachment (object) store are backed up and restored as one system.

## What to back up
1. **PostgreSQL** (includes the Data Protection key ring and the audit trail): `scripts/ops/backup-database.sh` at least hourly. It streams `pg_dump` (custom format) into AES-256 encryption with a passphrase file and writes a checksum. Keep the passphrase where it survives the loss of the host.
2. **Object storage** (attachments): use the storage provider's own versioning/replication or a scheduled copy to the off-host target. Take it at the same cadence; record the timestamp pair (database dump, object-store snapshot). Restoring a database without the matching objects leaves attachment rows without bytes; restoring objects without the database leaves orphans.
3. **Configuration and secrets**: `.env`, TLS material and the backup passphrase are stored separately by their owner; they are not in the repository.

## Scheduling, verification and monitoring (hourly)
- **Schedule:** install `scripts/ops/systemd/helpdesk-backup.service` and `.timer` (copy to `/etc/systemd/system/`, create `/etc/helpdesk/backup.env` with `BACKUP_PASSPHRASE_FILE`, `BACKUP_DIR`, optional `BACKUP_ALERT_COMMAND`, then `systemctl enable --now helpdesk-backup.timer`). Cron works too: `0 * * * * cd /opt/helpdesk && . /etc/helpdesk/backup.env && scripts/ops/backup-all.sh`.
- **What one run does** (`backup-all.sh`): encrypted database backup, then `verify-backup.sh` on the new file (checksum, decryption, readable archive with data for the `identity` and `ticketing` schemas), then, if `OBJECT_BACKUP_DEST` is set, `backup-objects.sh`. Concurrent runs are refused; a failed run leaves no partial backup file.
- **Error reporting:** a failed run exits non-zero (the systemd unit shows failed), prints the reason on stderr, writes `"status":"failed"` with the reason to `$BACKUP_DIR/last-backup.json`, and runs `BACKUP_ALERT_COMMAND "<message>"` if configured (mail, chat webhook or pager: your choice; the command is yours to provide).
- **Monitoring:** run `scripts/ops/check-backup-freshness.sh` from the host's existing monitor (needs `BACKUP_STATUS_FILE`). It fails when the last run failed, or when the last good run finished more than `BACKUP_MAX_AGE_MINUTES` (default 120) ago.
- **Object store:** `backup-objects.sh` copies the bucket with the AWS CLI (install it on the backup host; it is only a transfer tool for any S3-compatible endpoint) to an `s3://` URI or an off-host directory, never deletes at the destination, and verifies by a second dry-run sync. It does not encrypt attachments itself: for an `s3://` destination set `OBJECT_BACKUP_SSE=AES256` (or `aws:kms`) to request server-side encryption, and for a directory the off-host volume must be encrypted. The AWS CLI v1 or v2 both work (`pip install awscli` in a virtual environment is enough; no administrator rights). Attachments are switched off in production until a malware scanner exists (D-08), so the bucket is expected to be empty for now; the step still runs and reports 0 objects.
- **Off-host copy** of `$BACKUP_DIR` is still your responsibility (the scripts write to a staging directory; mount it off-host or copy it onward).

## Restore
1. Stop the application (`docker compose ... stop helpdesk nginx`).
2. `BACKUP_PASSPHRASE_FILE=... scripts/ops/restore-database.sh <backup.dump.enc> <new_database_name>` (refuses a non-empty target, verifies the checksum when present).
3. Restore the object store to the matching snapshot.
4. Point `Database__ConnectionString` at the restored database, start the application, check `/health/ready`, sign in, open a recent ticket and an attachment.
5. Rollback of a failed release is the same procedure with the backup taken just before migrating (decision D5).

## Safe restore test
`scripts/ops/restore-test.sh <backup.dump.enc>` verifies the backup, restores it into a generated scratch database (`restore_test_<timestamp>`), checks the restored schemas contain tables, compares the restore time with the 4 hour RTO and the backup age with the 1 hour RPO, writes `restore-test-result.json` (path via `RESTORE_TEST_RESULT`) and drops the scratch database. It cannot touch the live database: the target name is generated, `restore-database.sh` refuses a non-empty target, and only a `restore_test_` database is ever dropped (`RESTORE_TEST_KEEP=1` keeps it for inspection). Record each result in the drill table below. It reads no row contents.

## Restore drill (at least quarterly, and after any change to the backup chain)
**Owner:** a named platform/IT technical owner (none is named yet: the first entry below must name them). **Environment:** the same database server software version as production, a scratch machine or database name, never the live database. A drill that was not signed by a named owner does not count.

### Checklist
1. Pick the newest backup from the off-host location (not the staging copy) and note its file name (its UTC timestamp is its age) and the matching object-store snapshot, if attachments are enabled.
2. `scripts/ops/verify-backup.sh <file>` must pass (checksum, decryption with the passphrase held by the owner, readable archive).
3. `scripts/ops/restore-test.sh <file>` must report `"result":"ok"`, `withinRto` true and `withinRpo` true (RPO is judged on the newest backup at the time of the drill; if the newest is older than one hour, that itself is a finding). Keep the `restore-test-result.json`.
4. Start a scratch application instance against the restored database (`RESTORE_TEST_KEEP=1`, then point a non-production host at it): `/health/ready` is healthy, a platform administrator can sign in, a recent ticket opens with its conversation, and the Data Protection ring was restored (an existing session cookie from before the backup is still accepted, or sign-in works without key errors).
5. If attachments are enabled: open one attachment from the restored system and compare its SHA-256 with the stored value; if disabled, state "attachments disabled, object restore not applicable".
6. Object store: restore the object copy into a scratch bucket (`aws s3 sync <backup> s3://<scratch>`) and compare checksums with the source for a sample.
7. Record the elapsed time of the whole procedure (not only the restore step) against the 4 hour RTO.
8. Drop the scratch database and scratch application; confirm no restored data remains on the scratch machine.

### Evidence required to count the drill
- Table row below, complete, with the owner's name and the date.
- The `restore-test-result.json` and the output of `verify-backup.sh` (no data contents, no passphrase).
- The measured whole-procedure time and the backup age at the time of the drill.
- Who held the passphrase and where its copy lives (location only, never the value).
- Any finding (older-than-one-hour backup, step that failed, step that needed an undocumented action) with a follow-up owner and date. A failed drill is recorded as failed; it is not repeated until the cause is fixed.

### Operator instructions in an incident (restore for real)
1. Stop the application (`docker compose ... stop helpdesk nginx`) so nothing writes to the old database.
2. Choose the newest backup that verifies (`verify-backup.sh`); if the newest does not verify, take the previous one and record the data-loss window.
3. Restore into a NEW database name with `restore-database.sh`; never over the live one. Restore the matching object snapshot if attachments are enabled.
4. Point `Database__ConnectionString` at the new database, start the application, and check `/health/ready`, sign-in, a recent ticket, and the notifications bell.
5. Mail catches up from the last delta token on its own (see the runbook); check "Mail needing attention" afterwards.
6. Record what was lost (time between the backup and the incident), and keep the old database until the owner approves its disposal.

Drill log (one row per drill; **no drill has been run against real production backups yet**; the scripts were exercised only on throw-away test data):

| Date | Owner | Backup used (age) | Whole-procedure time | Objects matched | Result | Findings and follow-up |
|------|-------|-------------------|----------------------|-----------------|--------|------------------------|
|      |       |                   |                      |                 |        |                        |
