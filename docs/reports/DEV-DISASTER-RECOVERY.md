# Development environment: backup and disaster recovery — 2026-10-13

Scope: the local Windows Docker Desktop development database (`helpdesk` in `helpdesk-postgres-1`, volume `helpdesk_pgdata`). No secrets are recorded here. Nothing in the live environment was modified by the verification.

## 1. Backups

| Item | Value |
|---|---|
| Backup A | `%USERPROFILE%\HelpdeskBackups\dev\helpdesk-dev-20261013T221327Z.dump.enc` (+ `.sha256`), taken before the c73db3c deployment |
| Backup B | `D:\DB Backup\helpdesk-dev-20261013T223240Z.dump.enc` (+ `.sha256`), 582 032 bytes, taken after the deployment (moved from `F:\DB Backup`; hashes compared before the original was removed) |
| Content | `pg_dump --format=custom --no-owner` of database `helpdesk`, then encrypted |
| Format | OpenSSL `enc` AES-256-CBC, PBKDF2 (600 000 iterations), salted (`Salted__` header). Only `openssl` and `pg_restore` are needed. |
| Passphrase | 64 characters, generated once; the same passphrase protects A and B. Held (1) as a Windows DPAPI blob at `%USERPROFILE%\HelpdeskBackups\keys\dev-backup-key.dpapi.txt` (only this Windows account on this installation can open it) and (2) in the owner's password manager. |

## 2. Independent recovery verification (2026-10-13, backup B)

Performed without the DPAPI key file: the passphrase was typed from the password manager at a hidden prompt (length checked: 64) into a throw-away `postgres:18.6` container (own network, no published ports, no volumes), then discarded with the container.

| Check | Result |
|---|---|
| File SHA-256 equals the recorded value | PASS |
| Decryption with the password-manager passphrase | PASS |
| `pg_restore` (`--exit-on-error`) into an empty database | PASS, exit 0, 484 archive entries |
| Counts: tickets / departments / users / audit events | 14 / 6 / 398 / 2903, identical to the live database |
| Constraints | 49 foreign keys present, 0 unvalidated, 0 orphan tickets |
| Migration history | audit 4, identity 3, organisation 2, mail 3, ticketing 26, attachments 1, knowledge 2; latest ids equal the source |
| Usable without the original volume | The application image's own `migrate` step ran against the restored copy: "All module migrations applied" (nothing pending) |
| Live database, `hd-host`, E2E stack, Cloudflare | Untouched (live counts still 14 / 6 / 398; tunnel stopped) |

An earlier attempt failed with `bad decrypt` because the password-manager entry held the contents of the DPAPI key file (716 characters) instead of the passphrase. The entry was replaced; the check above used the corrected entry.

## 3. Portability

- **Verified:** the backup file and the passphrase together decrypt and restore with only generic tooling (OpenSSL 3, PostgreSQL 18), independently of Windows DPAPI and of the original volume.
- **Not verified:** recovery on a different Windows installation or PC. The test ran on this PC with Docker images already present.
- **No offsite copy yet:** `D:` is a fixed internal NVMe disk in this PC (as `F:` was), so both backups are lost with the PC.
- A replacement PC needs only: the `.dump.enc` and `.sha256` files, the passphrase from the password manager, Docker Desktop (or OpenSSL 3 and PostgreSQL 18 client tools) and, to run the application again, the repository at the commit matching the backup.

### Recovery procedure (replacement PC)

1. Install Docker Desktop. Copy the `.dump.enc` and `.sha256` files to the new machine and compare `Get-FileHash <file> -Algorithm SHA256` with the `.sha256` content.
2. Start an empty PostgreSQL 18 database (`docker compose up -d postgres` from the repository, or `docker run -e POSTGRES_USER=helpdesk -e POSTGRES_PASSWORD=<new> -e POSTGRES_DB=helpdesk postgres:18.6`).
3. Decrypt inside the container so the plaintext dump never touches the host disk; type the passphrase when asked:

```bash
docker cp helpdesk-dev-YYYYMMDDTHHMMSSZ.dump.enc <container>:/tmp/b.enc
docker exec -it <container> sh -c 'openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -in /tmp/b.enc -out /tmp/b.dump'
docker exec <container> pg_restore -U helpdesk -d helpdesk --no-owner --exit-on-error /tmp/b.dump
docker exec <container> rm -f /tmp/b.enc /tmp/b.dump
```

4. Check out the matching commit, run `helpdesk-host migrate` (expect "All module migrations applied"), start the application and sign in.
5. The ASP.NET Data Protection key ring is inside the database, so sessions and the stored Entra secret survive a restore of the same backup. The key ring is not encrypted at rest (known production gate).

Never restore over a non-empty database: restore into a new one and compare first.

## 4. Ticket count investigation

| Finding | Status |
|---|---|
| Live target is `helpdesk` on `helpdesk-postgres-1` (PostgreSQL 18.6) through the `hd-host` connection string | Confirmed |
| Live ticket count is 14 (12 Open, 1 Pending, 1 Resolved), all in IT, all created 2026-10-05; the application API returns the same 14 | Confirmed |
| The count was 14 before the deployment (backup taken first, restore test: 14) and 14 after | Confirmed: the deployment lost nothing |
| "68 tickets" appears in no repository document, memory note or commit message | Confirmed (absence) |
| The audit log holds 416 `ticket.created` events across 160 departments; 14 of those tickets exist and 1 of those departments (IT) exists | Confirmed |
| The 159 missing departments are named "E2E …" (created by Playwright runs against this database between 2026-10-05 and 2026-10-07) and their 402 tickets are gone; no table other than audit has rows for them | Confirmed |
| The application has no department or ticket delete endpoint, and the audit log has no delete/purge action | Confirmed |
| The removal was done outside the application, after the last E2E activity (2026-10-07 15:06 UTC), by a clean-up that removed the test departments and everything keyed to them but could not remove audit rows (append-only trigger) | **Inference** from the pattern; who ran it and when is unknown. `scripts/dev-demo-reset.sql` is not it: it targets only department key `DEMO` and deletes audit rows too |
| PostgreSQL recovered from an unclean shutdown on 2026-10-09 20:55 UTC (container recreated, volume kept) | Confirmed; no evidence of lost committed data |
| Where "68" came from | **Unexplained.** Possibilities not proven: a count taken while test data still existed; the number of test departments/tickets at some point; the 68 architecture tests that passed in this session. No evidence of unexpected loss of real data: every present department and all 14 original IT tickets are intact |

Conclusion: ticket count CONFIRMED at 14 for the real data; the loss of the E2E test tickets looks like test-data removal rather than corruption, but the exact event is not documented. This is also why the isolated E2E stack (`scripts/e2e/e2e.ps1`) now exists.

## 5. Remaining disaster-recovery risks

1. **No offsite copy.** Both backups are on this PC (`C:` and `D:`). Copy backup B (`.dump.enc` and `.sha256`) to a USB drive (`G:`/`H:` are present) or cloud storage; then repeat the recovery test on another machine for full verification.
2. Replacement-PC recovery has only been rehearsed on this installation.
3. Backups are manual; there is no schedule, retention or periodic restore drill for the development stack (the Windows production tooling in `scripts\windows\helpdesk.ps1` needs `docker\windows\.env`, which is not set up here).
4. The backup covers the database only; object storage (`helpdesk_objectdata`; attachments off) is not included.
5. The DPAPI key file protects only this account; the password-manager copy is the real recovery path. Keep that entry exactly the 64-character passphrase.
6. Data Protection keys are unencrypted in the database and backups (known gate).
7. Audit rows for the removed E2E test departments remain (orphans); harmless.
