# Production readiness review: 2026-10-08

Scope: the seven checks requested after the SLA alert work. Method: targeted source and document inspection plus targeted tests. Nothing was deployed, no backup or restore was executed, the SLA backfill was not run against production, and no production data was touched. Simulator and unit tests are not live-tenant evidence.

**Verdict: NOT production ready.** Open gates: live Graph validation and a recorded production restore drill block go-live. The malware scanner is intentionally deferred and attachments are disabled in production instead (update below). None of the remaining gates is a code defect that can be closed by a small fix.

| # | Check | Result |
|---|-------|--------|
| 1 | SLA backfill procedure documented | PASS |
| 2 | Data Protection keys persist across restarts | PASS (WARN: not encrypted at rest) |
| 3 | Attachment malware scanning enforced | PASS: external scanner integration intentionally deferred; attachments are disabled in production (see update) |
| 4 | Backups and restore | WARN: scheduling, verification, object-store copy and restore test now exist and passed against test data; FAIL until a real production drill is recorded (see update) |
| 5 | Department permissions and requester isolation | PASS |
| 6 | Graph mailbox health, delivery failures, recovery | PASS by design / FAIL on live evidence |
| 7 | Security, migration and deployment risks | see ranked list |

## Findings

**1. SLA backfill: PASS.** `docs/guides/SLA_ALERT_BACKFILL.md` covers prerequisites, dry run, apply, verification and recovery. The command has only run against a throw-away test database.

**2. Data Protection keys: PASS, with a WARN.** The key ring is stored in PostgreSQL (`identity.data_protection_keys`, fixed application name `Helpdesk`, `DataProtectionKeyStore.cs`), so sessions survive restarts and container replacement, and the keys travel with the database backup. WARN: the key XML is stored unencrypted; anyone who can read the database or a backup can read the keys. This is an accepted, documented decision (database is the boundary; backups are encrypted). Documentation drift: the "persistent Data Protection keys" gate in the project `CLAUDE.md` known-gates list is closed in code and should be removed from it (not edited here: it is the project rules file).

**3. Malware scanning: enforcement PASS, capability FAIL.** Enforcement is fail-closed: the host refuses to start in production unless `Attachments:RequireCleanScan=true` (`ProductionHosting.cs`; the reference compose sets it), and downloads require scan state Clean (`AttachmentStoreService.IsDownloadable`). The only scanner implementation is `NoScanner`, which reports Skipped, never Clean. Consequence: with the required setting, **no attachment, inbound or outbound, can ever be opened in production** until a scanning engine is chosen and integrated (D-08 open). Safe, but a functional blocker for a mail-based service desk.

**4. Backups and restore: WARN/FAIL.** Present and sound on paper: `scripts/ops/backup-database.sh` (streamed `pg_dump`, AES-256, checksum, size plausibility check), `restore-database.sh` (refuses non-empty target), RPO 1 h / RTO 4 h targets, quarterly drill procedure. Gaps: nothing in the repository schedules the hourly backup; object-store backup is "provider procedure" with no tooling; **no restore drill has been run and no owner is named** (the drill table is empty). These scripts were reviewed, not executed.

**5. Isolation: PASS.** Targeted run of the isolation and portal classes: `DepartmentIsolationMatrixTests`, `TicketIsolationMatrixTests`, `TicketIsolationLeakageTests`, `PortalTests`: 245 tests; 241 passed and 4 failed. The 4 failures were a stale test expectation, not a leak: the queue-count figure is Open plus Pending without merged sources (`REPORTING_DEFINITIONS.md`) while the test compared it with an Open-only SQL count. Fixed in the test only; `TicketIsolationMatrixTests` then passed 196/196. Earlier in the same work: requester identity 9/9, mentions 3/3, notifications and SLA alerts passing. No production code was changed by this check.

**6. Graph: PASS by design, FAIL on evidence.** Delivery states (queued, accepted, failed, uncertain, bounced), a single automatic recheck of uncertain sends, NDR handling, mailbox health on the dashboard (visible only with the mailbox-management permission) and recovery are implemented and described in `OPERATIONS_RUNBOOK.md`. Live-tenant validation (D-16, experiments E-1..E-38) has **never been run**; behaviour of From/Reply-To, Sent Items identifiers, NDR correlation and `Auto-Submitted` against real Exchange Online is unproven.

## Outstanding risks, ranked

| Severity | Risk | Status / action |
|----------|------|-----------------|
| High | No malware scanning engine; attachments unusable in production (D-08) | Decision and integration needed |
| High | Graph never validated against a real tenant (D-16) | Needs a test tenant, shared mailbox and safe recipient; follow `GRAPH_TENANT_VALIDATION.md` |
| High | No restore drill, no named owner, no automated hourly backup, no object-store backup tooling | Name owner, schedule, run one drill and record it |
| Medium | Data Protection keys and audit data unencrypted in the database; backup passphrase and `.env` custody is a manual process | Documented boundary; revisit if the database is shared |
| Medium | Single instance only (jobs, presence and mail loop assume one process, ADR-021); no HA | Accepted topology; state it in the go-live decision |
| Medium | Rollback of a bad release is restore-from-backup, not down-migration; the two newest migrations do have tested Up/Down but older ones are not guaranteed | Take a backup immediately before every migrate |
| Medium | Retention and pruning not approved (D-09); logs and operational tables grow unbounded | Watch table growth; policy decision needed |
| Medium | Reference compose not run behind a real reverse proxy and TLS; CSP not checked in a browser from a deployment | Needs a deployment; not done here |
| Low | SLA rows older than the 80% alert alert only at breach until the backfill is run | Run `SLA_ALERT_BACKFILL.md` once after deploy |
| Low | Platform admin can self-grant Department membership (D-11, audited) | Consider dual control |
| Low | Major dependency upgrades pending (TypeScript 7, jsdom 30, @types/node 26, xunit runner 4); Playwright minor; SeaweedFS tag `4.48` not confirmed against the registry | Separate, planned upgrades |
| Info | Project `CLAUDE.md` still lists persistent Data Protection keys as an open gate | Remove the line |

## Not verified in this review
The migrate-on-startup default for the production configuration, the reference compose behaviour under a real proxy, SignalR behaviour behind the proxy, the encrypted backup/restore pipeline end to end, and any behaviour in a real Entra or Graph tenant.

## Update: verified follow-up work (same day)

**Malware scanning (decision: external antivirus integration is intentionally deferred).** An attempted ClamAV integration was stopped and nothing from it is in the repository. Instead, attachments are switched off in production (`7bde968`): `Attachments:Enabled=false` is set in `docker/production/compose.yml` and the host **refuses to start in production** with it on or unset (`ProductionHosting`, covered by `ProductionHostingTests`). With it off: uploads (agent and portal) are recorded as blocked metadata with reason `attachments_disabled` and no bytes are stored; inbound email attachments are not even fetched from Graph and are recorded the same way; downloads, including of files stored earlier, are refused with `attachments_disabled`. The existing fail-closed checks (`RequireCleanScan`, validation, infected handling) are unchanged. Verified by `Mail.AttachmentTests` (new test for the off switch with mail attachments and an earlier stored file), `ProductionHostingTests` and `AuthConfigurationTests`: 55/55 pass. Limitation: the web UI still offers attaching files; the server records them as blocked (UI untouched on request). To enable attachments later: choose and integrate a scanner (D-08), then change the guard deliberately.

**Backups (`7ad8a93`).** Added `scripts/ops/backup-all.sh` (hourly entry point: lock, backup, verify, optional object copy, status file, failure alert hook), `verify-backup.sh`, `backup-objects.sh` (AWS CLI, copy-only, never deletes at the destination, dry-run verification), `restore-test.sh` (generated scratch database, schema checks, RTO/RPO comparison, result file, scratch dropped), `check-backup-freshness.sh` (monitoring probe) and systemd service and timer units (`OnCalendar=hourly`, `Persistent=true`). `backup-database.sh` no longer leaves a partial file on failure (defect found while testing). Procedures are in `BACKUP_AND_RESTORE.md` and `OPERATIONS_RUNBOOK.md`.

Verified against a throw-away database in the local development PostgreSQL container (never production data): backup + verification + status file OK; restore test OK (2 tables, within RTO and RPO, scratch database dropped); tampered file fails checksum; wrong passphrase fails decryption; a failing backup exits 1, writes the failed status, runs the alert command and leaves no partial file; the freshness probe passes after success and fails after a failed run; object copy verified with a stub `aws` (argument construction, `--delete` never used, incomplete copy detected). **Not verified:** the object-store copy against a real S3 endpoint (the AWS CLI is not installed here), the systemd units on a Linux host, and anything on production backups. The scheduling is provided, not installed: someone must install the timer, the environment file and an off-host mount, and name a drill owner.

**Data Protection.** `CLAUDE.md` now records persistence as implemented and keeps the warning that the key ring is not encrypted at rest.

**Remaining blockers, ranked.** High: live Graph tenant validation (D-16). High: install the backup timer, run and record one real restore drill with a named owner, and provide off-host storage. Medium: no malware scanner (D-08), so attachments stay off; retention policy (D-09); single-instance topology; deployment behind a real proxy and TLS not exercised.

## Update 2: backup chain, Graph procedure and drill checklist (verified results)

| Check | Result | Evidence |
|-------|--------|----------|
| Object-store backup with AWS CLI (v1.46.1 in a user-level virtual environment, no admin rights) | PASS on an isolated S3-compatible bucket (a throw-away SeaweedFS container, removed afterwards) | upload of 3 objects incl. 3 MB binary and nested keys; copy to a directory and to a second bucket; MD5 of every file identical at the copy; deleting a source object did not delete the backup copy (no `--delete`); wipe of the source followed by restore from the copy gave identical MD5s; unreachable endpoint exits 1; incomplete copy detected by the dry-run check |
| Object backup against a real production endpoint, SSE option on a real AES256/KMS destination | NOT TESTED | no production endpoint used; SeaweedFS test bucket does not exercise SSE |
| Database backup encryption | PASS | output starts with `Salted__`; a marker string stored in the database is absent from the file (0 hits); checksum, wrong passphrase and tampering all rejected by `verify-backup.sh`; empty passphrase file refused |
| Failure alerts and status file | PASS | failing backup exits 1, writes `"status":"failed"` with the reason, runs `BACKUP_ALERT_COMMAND`, leaves no partial file; the freshness probe fails after a failed run and passes after a good one; a live lock makes a second run skip quietly |
| Script syntax | PASS | `bash -n` on every script in `scripts/ops/` |
| Hardening in this round | DONE | backup files created with `umask 077`; optional `OBJECT_BACKUP_SSE` for `s3://` destinations |
| Scheduling configuration (systemd units) | WARN: reviewed, not executed | `OnCalendar=hourly`, `Persistent=true`, `Type=oneshot`; no Linux host or `systemd-analyze` available here. Cron alternative documented |
| Graph live-tenant validation | NOT RUN: procedure ready | `GRAPH_TENANT_VALIDATION.md` section 0 (send, receive, threading, NDR, acknowledgement, recovery, uncertain send, pass criteria). No Graph configuration, tenant or approved mailbox exists in this environment, so nothing was executed. D-16 stays open |
| Restore drill | NOT RUN: checklist final | `BACKUP_AND_RESTORE.md`: checklist, evidence required, incident instructions, drill log. No owner is named and no production backup exists to drill against |

Attachment UI: recorded in `docs/plans/BACKLOG.md` that the redesigned UI must hide attachment controls while attachments are disabled; the server needs a read-only "attachments enabled" flag first (endpoint undecided). Until then the controls show and uploads are recorded as blocked.

### Deployment requirements still outstanding (operator actions)
1. Install `helpdesk-backup.timer`/`.service`, `/etc/helpdesk/backup.env` (passphrase file path, backup directory, `BACKUP_ALERT_COMMAND`, `OBJECT_BACKUP_*` if attachments are ever enabled), an off-host mount for the backup directory, the AWS CLI if the object store is backed up, and a monitor calling `check-backup-freshness.sh`.
2. Name the drill owner and run and record the first real restore drill.
3. Run the live Graph validation (section 0) in a test tenant; close D-16 and R-04.
4. Run the one-off SLA alert backfill after deploying the migrations (`SLA_ALERT_BACKFILL.md`).
5. Deploy behind the reverse proxy with TLS and check the CSP in a browser; confirm the host starts with `Attachments:Enabled=false`.
6. Decide the malware scanner (D-08) before enabling attachments; approve retention (D-09).

**Current verdict unchanged: NOT production ready** until items 2 and 3 are done. Everything that could be verified without a production environment or a Microsoft tenant has passed.

