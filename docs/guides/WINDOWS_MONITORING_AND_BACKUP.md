# Windows monitoring, scheduled backups and recovery

Applies to a Helpdesk Production stack on **Windows 11 Pro with Docker Desktop** (the supported hosting environment). Nothing here is specific to one organisation: every path, destination, account and URL is configuration supplied by the customer's IT administrator.

Status: implemented and tested with mocks and disposable infrastructure. **No scheduled task is registered by the repository and no alert account exists.** Section 7 lists exactly what must be in place before activation.

## 1. Customer responsibilities and prerequisites

Helpdesk does not include, redistribute or enforce licences for third-party software. The customer is responsible for obtaining and complying with the licences for:
- Windows 11 Pro and its updates,
- Docker Desktop (check Docker's current subscription terms for your organisation size; the installer and these scripts never bundle or download it),
- Microsoft 365 (Entra ID, Exchange Online, Microsoft Graph) and any other service they connect,
- the alert service and the heartbeat service they choose (section 3),
- Git for Windows (the backup scripts run through its `bash`).

## 2. What runs

| Task (Task Scheduler) | When | Identity | What it does |
|---|---|---|---|
| Helpdesk Production Backup | hourly | dedicated service account, "run whether logged on or not" | `backup-production.ps1`: encrypted dump, verification, optional object and configuration bundles, verified copies, retention |
| Helpdesk Production Watchdog | every 5 minutes | the same service account | `watchdog-production.ps1`: read-only checks, deduplicated alerts, recovery notices, heartbeat |
| Helpdesk Production Recovery | at sign-in | the person whose sign-in starts Docker Desktop (interactive) | `recover-production.ps1`: start Docker Desktop if needed, then the containers in dependency order (never the Cloudflare tunnel) |

Install and remove with `scripts\windows\install-production-tasks.ps1` and `scripts\windows\uninstall-production-tasks.ps1`. Both support `-WhatIf` (nothing is created or removed, no password is requested). Uninstalling removes only the three tasks, never backups, state, configuration, containers or volumes.

## 3. Configuration

Copy `docker\windows\monitor.env.example` to a folder only the service account and administrators can read (for example `C:\ProgramData\Helpdesk\monitor.env`) and fill it in. The file may contain a secret URL; the watchdog reports a configuration, passphrase or token file that is readable by Everyone, Users or Authenticated Users.

- **Alerts** (`ALERT_WEBHOOK_URL`, required): an HTTPS endpoint. `ALERT_WEBHOOK_FORMAT=ntfy` (default) sends a text body with `Title`, `Priority` and `Tags` headers, compatible with ntfy; `json` sends `{title,message,priority,tags,status,key,host}` for any other HTTPS receiver. An optional bearer token is read from `ALERT_WEBHOOK_TOKEN_FILE`. Plain `http` is refused. The transport is one function (`Send-WebhookAlert`) so another can be added without touching the checks.
- **Dead-man heartbeat** (`HEARTBEAT_URL`, optional but recommended): pinged with a GET after every completed run (Healthchecks-compatible). The external service alerts when pings *stop*, which is the only signal that works when the whole PC is off. Without it, nothing can tell you the PC itself is down.
- **Backups**: `BACKUP_DIR`, `BACKUP_PASSPHRASE_FILE`, `BACKUP_REQUIRED_DIRS` (required off-host destinations), `BACKUP_COPY_DIRS` (optional local copies such as a USB disk), `BACKUP_PRUNE_DESTINATIONS`.
- **Stack**: `PG_CONTAINER`, `APP_CONTAINER`, `OBJECTSTORE_CONTAINER` (defaults match the standard Compose project).
- **Expiry**: `ENTRA_SECRET_EXPIRES` (date of the sign-in client secret), `CERT_EXPIRY_FILES`.
- **Thresholds** (defaults are the agreed service levels): section 4.

## 4. What is checked, and when you are told

All external commands run with an explicit time limit (`MONITOR_COMMAND_TIMEOUT_SECONDS`, 20 s) and are killed if they hang; network paths are probed with a limit too.

| Check | Fails when | Notes |
|---|---|---|
| Docker engine | `docker info` fails or does not answer | Also detects "restarted and nobody has signed in": the detail says Docker Desktop is not running. Ignored for `MONITOR_BOOT_GRACE_MINUTES` (10) after a restart. |
| Containers | database, application or object store not running or unhealthy | Skipped while Docker is down so one outage is one alert. |
| Database / Application readiness | `pg_isready` fails; `/health/ready` is not 200 | |
| Inbound mail check | an active mailbox's last successful check is older than 15 minutes | Reads database state only; no call to Microsoft. |
| Backup | no verified backup newer than 2 hours, or the last run failed | |
| Off-host backup | a required destination is unreachable or lacks the newest backup (size and checksum record must match); none configured | |
| Backup disk space | below `MONITOR_MIN_FREE_DISK_GB` (10) | Local drives only. |
| Credential expiry | Entra secret or a certificate has 30, 14 or 7 days left, or is expired; unset secret date | Each tier is a new notification; 7 days and expired are urgent. |
| Secret file permissions | passphrase, token or configuration file readable by broad groups | |

Alert behaviour: a failure must repeat for `MONITOR_CONFIRM_RUNS` (2) consecutive runs before it is sent, so an application or database failure is reported within 10 minutes of a 5-minute schedule (expiry warnings are immediate). A firing alert is not repeated except as a reminder every `MONITOR_REMINDER_MINUTES` (360) or when its level changes. Recovery sends exactly one notice. A notification that cannot be delivered is retried on the next run. Alert text is built from fixed templates and passed through a redactor (URLs, credentials, long tokens, e-mail addresses); the log (`watchdog.log`, rotated at 1 MB) never contains the webhook URL, tokens or customer content.

Useful commands: `watchdog-production.ps1 -ConfigFile <file> -DryRun` prints every check and sends nothing; `-TestAlert` sends one test notification.

## 5. Backups, off-host copies and retention

`backup-production.ps1` reuses `scripts\ops\backup-all.sh` unchanged (encrypted `pg_dump`, verification, checksum re-verified after every copy).

- **Passphrase**: for the unattended task use `BACKUP_PASSPHRASE_FILE` (an ACL-restricted file readable by the service account and administrators; it must not be inside `BACKUP_DIR` or the repository). The existing DPAPI key file keeps working for manual runs by its own Windows user; nothing is migrated or rewritten.
- **Required destinations** (`BACKUP_REQUIRED_DIRS`, a local drive or a UNC share such as `\\nas\helpdesk-backups\prod`): the drive or the share must already exist (only a folder below it is created). If one is unavailable or its copy cannot be verified, the run is marked **failed** (exit code 1, `last-backup.json` status `failed`) even though the local backup was taken, and the watchdog alerts.
- **Optional destinations** (`BACKUP_COPY_DIRS`): a missing one only warns.
- **Retention** after every fully successful run: every backup set younger than 14 days **and** the 24 newest verified sets are kept; older files matching the strict backup file-name pattern in `BACKUP_DIR` are removed. A set counts as successful only when its checksum file matches. Nothing is removed if no verified set exists. Destinations are pruned only when `BACKUP_PRUNE_DESTINATIONS=true`. `backup-production.ps1 -WhatIf` takes no backup and shows availability and what retention would remove.
- The task account needs: membership of the `docker-users` group, "Log on as a batch job", read access to the passphrase file and configuration, write access to `BACKUP_DIR` and the monitor folder, and rights on the NAS share. Credentials for the share are held by Windows (Credential Manager for that account), never by these scripts.

Restore is unchanged: `docs\guides\PORTABLE_BACKUP_RECOVERY.md`. Restores are proven only by actually performing them: run `scripts\ops\restore-test-isolated.sh` (through Git bash) against a recent backup after activation and record the result.

## 6. Docker Desktop after a Windows restart

Docker Desktop is a per-user application and cannot be relied on to start before anyone signs in; it is not started as a service at boot. This design does **not** assume it does and does not use automatic sign-in:
- The watchdog runs without a sign-in and alerts ("Docker Desktop is not running ... sign in") within about 15 minutes of a restart nobody completed.
- When someone signs in, Docker Desktop starts (if "Start Docker Desktop when you sign in" is enabled for that user), Docker restarts the containers that have a restart policy, and the Recovery task starts anything left in dependency order and runs the health check.
- Not attempted: unsupported pre-login Docker startup, auto sign-in, rebooting or restarting Windows or Docker Desktop.

**Proposed Windows Update maintenance window (documentation only; no policy has been applied):** Sunday 03:00 to 05:00 local time, with a bounded restart deadline, so restarts happen when someone can sign in to complete recovery. Applying it (group policy or Windows Update settings) is a separate decision for the customer's administrator.

## 7. Prerequisites for activation (exact)

1. A dedicated local service account (for example `hd-svc`) with the rights in section 5, a strong password held by the customer, and membership of `docker-users`.
2. `monitor.env` created from the example, ACL-restricted; `BACKUP_DIR` (absolute path), `BACKUP_PASSPHRASE_FILE` (created by the customer; never generated or migrated by these scripts), `BACKUP_REQUIRED_DIRS` (a NAS/SMB share that exists), `ENTRA_SECRET_EXPIRES`, and optionally `CERT_EXPIRY_FILES`.
3. An alert receiver (for example ntfy) and, optionally, a heartbeat service; their URLs go only in `monitor.env`. No external account is created by this repository.
4. Git for Windows installed; Docker Desktop running for the first backup.
5. `watchdog-production.ps1 -DryRun`, then `-TestAlert`, then `backup-production.ps1 -WhatIf`, then one manual backup run showing exit code 0 and the off-host copy.
6. `install-production-tasks.ps1 -WhatIf`, then the real run (asks for the service account password interactively; the scripts never store it).
7. A restore drill after the first scheduled backup.

## 8. Limitations

- Positive access to a real UNC share was not exercised in the test environment (no share could be created without administrator rights); path conversion, availability probing and every failure path were tested, and the share path is passed to the same copy-and-verify logic as a local folder.
- The watchdog and the backup run on the host they protect: only the external heartbeat covers a dead PC or network.
- A third-party alert or heartbeat service receives only state text (check name, status, host name); choose it accordingly.
- Entra sign-in secret expiry comes from the configured date, not from a live Entra query; update `ENTRA_SECRET_EXPIRES` on every rotation.
