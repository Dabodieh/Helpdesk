# Helpdesk on Windows Docker Desktop

For a Windows PC running Docker Desktop with Linux containers (today a single PC; later possibly the PC that already runs the SAMH Asset Management Suite). No Linux server and no public domain are needed. The Linux/systemd deployment (`PRODUCTION_DEPLOYMENT.md`, `STAGING_DEPLOYMENT.md`) is unchanged and uses the same compose file.

Everything is driven by one script: `scripts\windows\helpdesk.ps1` (Windows PowerShell 5.1 or PowerShell 7). Run `.\scripts\windows\helpdesk.ps1 help` for the command list.

## What this setup guarantees
| Concern | How |
|---------|-----|
| Coexists with other Docker apps | Own compose project (`helpdesk-win` by default: volumes `helpdesk-win_pgdata`, network `helpdesk-win_internal`), own published ports (default 8088 and 8443), own private subnet. `check` refuses a project name that is the development stack's (`helpdesk`), a subnet that overlaps any existing Docker network, and ports already in use on the PC. |
| No accidental data loss | The script never runs `down -v`, `--volumes`, `--rmi` or `prune`; `stop` keeps all volumes. Restores only go into an empty database. |
| Backups | Hourly encrypted PostgreSQL dumps by Windows Task Scheduler, verified after each run, with retention. |
| Backup key separate from backups | The passphrase is generated once, shown once, and stored only in a Windows DPAPI-protected file in your user profile (outside the backup folder). Keep your own offline copy: it is the only way to restore on another PC. |
| Linux support | Unchanged. The compose file only gained optional overrides (project name, ports, proxy address, subnet, TLS folder, `PUBLIC_PORT_SUFFIX`) whose defaults equal the old values. Backups made here use the same file format as `scripts/ops/backup-database.sh`, so either side can verify and restore the other's. |

## What you need
- Windows 10/11 with Docker Desktop (Linux containers, WSL2 backend), signed in as the user who will run Helpdesk. Docker Desktop and the scheduled backup both run in that user's session: enable "Start Docker Desktop when you sign in" and keep the PC signed in and awake (or accept that backups catch up when you return).
- Git, and this repository checked out at the commit you want to run.
- A **Microsoft Entra ID** app registration (single tenant) for sign-in: redirect URI `https://<PUBLIC_HOST>:<HTTPS_PORT>/signin-oidc`. Production mode does not allow the development sign-in. A non-public host name is fine; Entra only needs the redirect URI to be https.
- A **Microsoft Graph** app registration for the shared mailboxes (`GRAPH_TENANT_VALIDATION.md` section 1).
- A drive or network share for backups that is **not** the Docker disk, ideally another physical disk or a share (`BACKUP_DIR`).
- A name other PCs can use for this PC (its Windows name, or a DNS/hosts entry) for `PUBLIC_HOST`.

## First setup (about 30 minutes)
1. **Configuration.** `Copy-Item docker\windows\.env.example docker\windows\.env`, then edit it. Defaults are safe for coexistence; change `INTERNAL_SUBNET`/ports only if `check` reports a conflict. Generate `POSTGRES_PASSWORD` and `S3_*` keys yourself (long random values; never reuse another system's).
2. **Certificate.** `.\scripts\windows\helpdesk.ps1 new-certificate` writes a self-signed certificate for `PUBLIC_HOST` into `docker\windows\tls`. Browsers warn until you trust `fullchain.pem` on each PC that uses Helpdesk (Trusted Root Certification Authorities) or replace both files with a certificate from your own CA.
3. **Check.** `.\scripts\windows\helpdesk.ps1 check` must print `configuration ready`. It lists problems by name and never prints values.
4. **Backup key.** `.\scripts\windows\helpdesk.ps1 backup-init`. Copy the passphrase it prints into your password manager (or print and lock it away) **now**; it is not shown again.
5. **Build and start.**
   ```powershell
   .\scripts\windows\helpdesk.ps1 build
   .\scripts\windows\helpdesk.ps1 migrate
   .\scripts\windows\helpdesk.ps1 start
   ```
   With `LOCAL_OBJECT_STORE=true` (default) a private object store runs inside the project because Helpdesk needs an S3-compatible endpoint even though attachments are disabled; nothing else is required.
6. **Verify.** `.\scripts\windows\helpdesk.ps1 verify`. Every line must be PASS (containers, redirect, readiness, API sign-in, no dev sign-in, headers, certificate, the five background jobs, no recent errors, backup freshness; the backup line passes after the first backup).
7. **Schedule backups.** `.\scripts\windows\helpdesk.ps1 backup-schedule` registers the Task Scheduler job "Helpdesk Backup" (hourly, catches up after sleep, runs while you are signed in). Run one now to see it work: `.\scripts\windows\helpdesk.ps1 backup`, then `.\scripts\windows\helpdesk.ps1 backup-status`.
8. Browse to `https://<PUBLIC_HOST>:<HTTPS_PORT>` and sign in with the Entra identity named in `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT`.

If the HTTPS port is not 443, `PUBLIC_PORT_SUFFIX` must be `:<port>` (the check enforces it). The plain-HTTP redirect only lands correctly on port 443; with another port, always use the full `https://host:port` address.

## Everyday commands
| Do | Command |
|----|---------|
| Start / stop (keeps all data) | `helpdesk.ps1 start` / `helpdesk.ps1 stop` |
| Is it healthy? | `helpdesk.ps1 status` (containers + last backup), `helpdesk.ps1 verify` (full checks) |
| Logs | `helpdesk.ps1 logs [helpdesk\|nginx\|postgres]` |
| Back up now | `helpdesk.ps1 backup` |
| Stop scheduled backups | `helpdesk.ps1 backup-unschedule` (backups and key are untouched) |

Windows can restart the PC or Docker Desktop at any time: containers have `restart: unless-stopped` and come back when Docker Desktop starts.

## Updating
1. `git fetch`, then `git checkout <commit>` (a clean tree; the script refuses uncommitted changes unless you pass `-Force`).
2. `.\scripts\windows\helpdesk.ps1 update`: it runs a **verified backup first** (an update stops if the backup fails), then build, migrate, start, and `verify`.
3. Note the commit you deployed (`git rev-parse --short HEAD`).

### Rollback
Rollback is restore, not down-migration. If `verify` fails or the new version is bad:
1. `.\scripts\windows\helpdesk.ps1 stop`
2. `git checkout <previous commit>`
3. Restore the backup the update just took into a **new** database and swap it in:
   ```powershell
   .\scripts\windows\helpdesk.ps1 restore -BackupFile D:\HelpdeskBackups\helpdesk-<timestamp>.dump.enc -Database helpdesk_restored
   .\scripts\windows\helpdesk.ps1 swap-database -Database helpdesk_restored
   ```
   `swap-database` (application stopped) renames the failed database to `helpdesk_replaced_<time>` and never drops it.
4. `build`, `start`, `verify`. Keep the `helpdesk_replaced_*` database until the cause is understood.

## Backup details
- **What:** `pg_dump` of the `helpdesk` database (tickets, users, configuration, audit, and the Data Protection key ring), streamed straight into AES-256 encryption (no plaintext file ever touches disk), plus a SHA-256 file. Files are named `helpdesk-<UTC timestamp>.dump.enc`.
- **Verification after every backup:** checksum, decryption, and a readable archive with data for the `identity` and `ticketing` schemas. A failed backup leaves no partial file, writes `last-backup.json` with `"status":"failed"` and the reason, exits non-zero (Task Scheduler shows the failure), and calls `BACKUP_ALERT_COMMAND` if you set one.
- **Retention:** every backup newer than `BACKUP_KEEP_DAYS` (default 14) is kept, and never fewer than the `BACKUP_MIN_KEEP` (default 24) newest. Only `helpdesk-*.dump.enc` files and their `.sha256` are ever deleted, and only after a good backup.
- **Where the key is:** `%LOCALAPPDATA%\HelpdeskWin\backup-passphrase.dpapi`, readable only by your Windows user on this PC, never inside `BACKUP_DIR`. If you lose the offline copy **and** this PC, the backups are unreadable.
- **Attachments:** disabled in this setup (no malware scanner yet), so there is no attachment data to back up.
- **A backup on the same PC does not protect against losing the PC.** Point `BACKUP_DIR` at another disk or a share, and also copy the folder somewhere else regularly.

## Restore, and moving Helpdesk to another Windows PC
**Practise this once** with `helpdesk.ps1 restore-test -BackupFile <file>` (restores into a scratch database, checks it, times it against the 4 hour RTO and 1 hour RPO, drops it; result in `restore-test-result.json`). Record the date and result.

To move to another PC (for example the SAMH PC) or recover after a disk failure:
1. **On the old PC (if it still works):** `helpdesk.ps1 backup`, then `helpdesk.ps1 stop`. Copy to the new PC: the newest `helpdesk-*.dump.enc` **and its `.sha256`**, and `docker\windows\.env` (it holds the secrets: move it securely) and, if you use your own certificate, `docker\windows\tls`. Have the **offline passphrase** ready.
2. **On the new PC:** install Docker Desktop and Git, check out the same commit, place `.env` (and `tls`), and edit what differs there: `PUBLIC_HOST`, and **ports/subnet/project name if other Docker apps already use them** (`check` finds conflicts with the SAMH suite's containers). If the host name changes, update the Entra redirect URI and run `new-certificate` for the new name.
3. `helpdesk.ps1 check`, then `helpdesk.ps1 backup-init` (creates a new key for the **new** PC's future backups; old backups still need the old passphrase).
4. `helpdesk.ps1 build`.
5. Restore the data: `helpdesk.ps1 restore -BackupFile <path>\helpdesk-<timestamp>.dump.enc -PromptPassphrase` (type the offline passphrase when asked). It starts only the database, requires an empty target, and fails safely otherwise.
6. `helpdesk.ps1 migrate` (applies any newer migrations; nothing if the code is the same), `helpdesk.ps1 start`, `helpdesk.ps1 verify`.
7. Sign in and open a recent ticket. Sessions survive the move because the Data Protection keys are part of the database. Mail catches up from its last position by itself (see `OPERATIONS_RUNBOOK.md`). Then `helpdesk.ps1 backup-schedule`, and decommission the old PC only after the new one has run a few days.

Unrecoverable Docker Desktop (reset or reinstall) is the same procedure on the same PC: the volumes may be gone, the encrypted backups and your offline passphrase are what you restore from.

## Limits of this setup
- Single instance, single PC. No high availability.
- Entra and Graph configuration must exist; nothing here replaces them. Live Graph validation is still outstanding (`GRAPH_TENANT_VALIDATION.md`).
- Attachments are off until a malware scanner exists; the UI still shows attachment controls (recorded in `docs/plans/BACKLOG.md`).
- Backups run only while the configured Windows user is signed in and Docker Desktop is running.
- Self-signed certificates need to be trusted on each client PC.
- Validated with placeholder configuration on a development PC (see the readiness report); not yet run with real Entra/Graph credentials or on the SAMH PC.
