# Backup and restore

Targets (decision D3): RPO 1 hour, RTO 4 hours. Backups are encrypted and kept off the Helpdesk host and its infrastructure. The database and the attachment (object) store are backed up and restored as one system.

## What to back up
1. **PostgreSQL** (includes the Data Protection key ring and the audit trail): `scripts/ops/backup-database.sh` at least hourly. It streams `pg_dump` (custom format) into AES-256 encryption with a passphrase file and writes a checksum. Keep the passphrase where it survives the loss of the host.
2. **Object storage** (attachments): use the storage provider's own versioning/replication or a scheduled copy to the off-host target. Take it at the same cadence; record the timestamp pair (database dump, object-store snapshot). Restoring a database without the matching objects leaves attachment rows without bytes; restoring objects without the database leaves orphans.
3. **Configuration and secrets**: `.env`, TLS material and the backup passphrase are stored separately by their owner; they are not in the repository.

## Restore
1. Stop the application (`docker compose ... stop helpdesk nginx`).
2. `BACKUP_PASSPHRASE_FILE=... scripts/ops/restore-database.sh <backup.dump.enc> <new_database_name>` (refuses a non-empty target, verifies the checksum when present).
3. Restore the object store to the matching snapshot.
4. Point `Database__ConnectionString` at the restored database, start the application, check `/health/ready`, sign in, open a recent ticket and an attachment.
5. Rollback of a failed release is the same procedure with the backup taken just before migrating (decision D5).

## Restore drill (at least quarterly)
Perform the restore into a scratch database and environment, measure the time against the 4-hour RTO and the age of the newest backup against the 1-hour RPO, and record it below. **A drill owner has not yet been named; the named platform/IT technical owner records themselves here.** No drill has been run yet.

| Date | Owner | Backup used (age) | Restore time | Objects matched | Result | Notes |
|------|-------|-------------------|--------------|-----------------|--------|-------|
|      |       |                   |              |                 |        |       |
