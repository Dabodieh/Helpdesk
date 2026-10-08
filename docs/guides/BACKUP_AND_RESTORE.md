# Backup and restore

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
- **Object store:** `backup-objects.sh` copies the bucket with the AWS CLI (install it on the backup host; it is only a transfer tool for any S3-compatible endpoint) to an `s3://` URI or an off-host directory, never deletes at the destination, and verifies by a second dry-run sync. Attachments are switched off in production until a malware scanner exists (D-08), so the bucket is expected to be empty for now; the step still runs and reports 0 objects.
- **Off-host copy** of `$BACKUP_DIR` is still your responsibility (the scripts write to a staging directory; mount it off-host or copy it onward).

## Restore
1. Stop the application (`docker compose ... stop helpdesk nginx`).
2. `BACKUP_PASSPHRASE_FILE=... scripts/ops/restore-database.sh <backup.dump.enc> <new_database_name>` (refuses a non-empty target, verifies the checksum when present).
3. Restore the object store to the matching snapshot.
4. Point `Database__ConnectionString` at the restored database, start the application, check `/health/ready`, sign in, open a recent ticket and an attachment.
5. Rollback of a failed release is the same procedure with the backup taken just before migrating (decision D5).

## Safe restore test
`scripts/ops/restore-test.sh <backup.dump.enc>` verifies the backup, restores it into a generated scratch database (`restore_test_<timestamp>`), checks the restored schemas contain tables, compares the restore time with the 4 hour RTO and the backup age with the 1 hour RPO, writes `restore-test-result.json` (path via `RESTORE_TEST_RESULT`) and drops the scratch database. It cannot touch the live database: the target name is generated, `restore-database.sh` refuses a non-empty target, and only a `restore_test_` database is ever dropped (`RESTORE_TEST_KEEP=1` keeps it for inspection). Record each result in the drill table below. It reads no row contents.

## Restore drill (at least quarterly)
Perform the restore into a scratch database and environment, measure the time against the 4-hour RTO and the age of the newest backup against the 1-hour RPO, and record it below. **A drill owner has not yet been named; the named platform/IT technical owner records themselves here.** No drill has been run yet against real production backups; the scripts were exercised only against throw-away test data.

| Date | Owner | Backup used (age) | Restore time | Objects matched | Result | Notes |
|------|-------|-------------------|--------------|-----------------|--------|-------|
|      |       |                   |              |                 |        |       |
