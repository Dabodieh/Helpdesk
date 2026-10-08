# ADR-021: Recovery, key ring, migration order and supported topology

Status: Accepted (post-Refinement-10 remediation, WS-6; decisions D2, D3, D5, D6; findings SRE-002, SRE-003, SRE-007, SRE-008, ARC-007)

## Decision
1. **Key ring (D2).** ASP.NET Data Protection keys are stored in PostgreSQL (`identity.data_protection_keys`) through a small `IXmlRepository` (no new package) with the fixed application name `Helpdesk`. Keys are not encrypted at rest by the application; the database and its backups are the protection boundary.
2. **Backup (D3).** RPO 1 hour, RTO 4 hours. `scripts/ops/backup-database.sh` writes an encrypted (AES-256, passphrase file) custom-format dump with a checksum; `scripts/ops/restore-database.sh` restores into an empty database. The database and the attachment store are one recoverable system; the off-host target, the object-store snapshot and the quarterly restore drill are operational procedures in `docs/guides/BACKUP_AND_RESTORE.md`. A backup is not recoverability: no drill has been recorded yet and its owner is not yet named.
3. **Migrations (D5).** One database, one role, explicit module order (audit, identity, organisation, then dependents), enforced for cross-schema foreign keys by a test. Production rollback is restore from a known-good backup.
4. **Topology (D6).** One application instance. No web/worker split and no realtime backplane until a real scaling requirement exists; documentation corrected.

## Consequences
- Migration `DataProtectionKeys` adds one table. Existing sessions end once at the first deployment of this version (the old ring was never persisted).
- The restore drill is evidence that only a real run produces; the guide holds an empty record table.
