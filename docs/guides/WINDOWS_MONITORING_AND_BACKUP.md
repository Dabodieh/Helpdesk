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
| Docker engine | `docker info` fails or does not answer; reported separately as "engine not running" (sign in), "access denied" (the account is not in `docker-users`; not hidden by the boot grace period) or "no answer" | Also detects "restarted and nobody has signed in": the detail says Docker Desktop is not running. Ignored for `MONITOR_BOOT_GRACE_MINUTES` (10) after a restart. |
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
- **Retention** after every fully successful run: every backup set younger than 14 days **and** the 24 newest verified sets are kept; older files matching the strict backup file-name pattern in `BACKUP_DIR` are removed. A set counts as successful only when its checksum file matches. Nothing is removed if no verified set exists. Destinations are pruned only when `BACKUP_PRUNE_DESTINATIONS=true` **and** `RESTORE_TEST_RESULT_FILE` records a successful isolated restore test no older than 35 days; otherwise the wrapper warns and leaves every destination alone (the default). `backup-production.ps1 -WhatIf` takes no backup and shows availability and what retention would remove.
- The task account needs: membership of the `docker-users` group, "Log on as a batch job", read access to the passphrase file and configuration, write access to `BACKUP_DIR` and the monitor folder, and rights on the NAS share. Credentials for the share are held by Windows (Credential Manager for that account), never by these scripts.

Restore is unchanged: `docs\guides\PORTABLE_BACKUP_RECOVERY.md`. Restores are proven only by actually performing them: run `scripts\ops\restore-test-isolated.sh` (through Git bash) against a recent backup after activation and record the result.

## 6. Docker Desktop after a Windows restart

Docker Desktop is a per-user application and cannot be relied on to start before anyone signs in; it is not started as a service at boot. This design does **not** assume it does and does not use automatic sign-in:
- The watchdog runs without a sign-in and alerts ("Docker Desktop is not running ... sign in") within about 15 minutes of a restart nobody completed.
- When someone signs in, Docker Desktop starts (if "Start Docker Desktop when you sign in" is enabled for that user), Docker restarts the containers that have a restart policy, and the Recovery task starts anything left in dependency order and runs the health check.
- Not attempted: unsupported pre-login Docker startup, auto sign-in, rebooting or restarting Windows or Docker Desktop.

**Proposed Windows Update maintenance window (documentation only; no policy has been applied):** Sunday 03:00 to 05:00 local time, with a bounded restart deadline, so restarts happen when someone can sign in to complete recovery. Applying it (group policy or Windows Update settings) is a separate decision for the customer's administrator.

## 7. Activation: prerequisites, preflight and sequence

Where things live and who may touch them: `docs\architecture\WINDOWS_INSTALL_LAYOUT.md` (Program Files for the release, ProgramData for configuration, secrets and state, a customer-chosen backup location). **Do not run privileged tasks from a development checkout or any folder ordinary users can write to:** the installer refuses, and the preflight fails, when Users, Authenticated Users or Everyone can change the script tree.

### 7.1 Prerequisites (all provided by the customer; nothing here is created by the repository)
1. An **elevated** PowerShell session (registering tasks for another account, reading the batch-logon right and BitLocker status need it).
2. A dedicated **local standard-user service account** (for example `hd-svc`): member of `docker-users`, granted "Log on as a batch job", not an administrator.
3. The release installed under the Program Files location, and the folders of the layout created with the Program, Config, Secrets, State and Backups ACL templates (`Get-IcaclsCommands` prints the commands; `Set-HelpdeskAcl -WhatIf` previews them).
4. `monitor.env` (from `docker\windows\monitor.env.example`) in the Config folder, and the **passphrase file** in the Secrets folder, written with `scripts\windows\new-backup-passphrase-file.ps1`. Use the same passphrase as the existing backups if they must stay restorable with it.
5. A NAS/SMB share that already exists, an account on it for the service account's network identity, and SMB signing support. Option A (recommended for a workgroup NAS): create a NAS user with the same name and password as the service account; the password-logon task presents its own credentials and nothing extra is stored. Option B: store a NAS credential once for the service account with `cmdkey`, typed interactively as that account (never on a command line); test it with the one-shot task, because Credential Manager entries and non-interactive logons interact subtly. Option C: a domain group-managed service account. Never: S4U logons (no network credentials), mapped drive letters, passwords in scripts, task arguments or `monitor.env`.
6. An alert receiver (for example ntfy) and, strongly recommended, a heartbeat check (Healthchecks-compatible: period 5 minutes, grace about 10). The heartbeat is the only thing that notices a locked service account or a dead PC.
7. Git for Windows and Docker Desktop (customer-licensed).

### 7.2 Preflight
`scripts\windows\preflight-production-activation.ps1 -ConfigFile <monitor.env> -ServiceUser .\hd-svc [-InstallRoot ... -DataRoot ... -BackupRoot ...]` checks, for the account it runs as: docker-users, the batch-logon right (elevated), Docker pipe access with a precise reason on failure (engine down vs access denied vs hang), Docker CLI, Git Bash/openssl/tar, readable scripts that ordinary users cannot change, configuration/passphrase/token file permissions and passphrase format, backup folder and required destination permissions, SMB client policy, alert and heartbeat configuration (not contacted), verified-recovery evidence, disk space, BitLocker, and the layout against the ACL templates. It prints no secret, URL or file content, and exits 1 on any FAIL.

- `-WriteTests` additionally writes, reads back and removes one marker file per backup folder and required destination (nothing else is written or deleted).
- `-OneShotTask -AuthoriseOneShotTask [-WhatIf]` is the decisive test: it registers a **temporary** task as the service account (password typed interactively, never stored), runs the preflight with `-WriteTests` under that account's own network logon, collects the result and removes the task, even on failure. It needs an elevated session and refuses without the explicit switch.

### 7.3 Controlled activation sequence
1. Record the current permissions of anything you will change (`icacls <path> /save`), so each step can be reversed.
2. Create the account, folders and ACLs; write `monitor.env` and the passphrase file; set up the NAS share and alert/heartbeat endpoints.
3. Run the preflight as yourself, fix every FAIL, then run it interactively as the service account (`runas`), then `-OneShotTask -AuthoriseOneShotTask -WhatIf`, then the real one-shot test. Do not continue while any FAIL remains.
4. `watchdog-production.ps1 -DryRun`, then `-TestAlert` (one notification must arrive).
5. `backup-production.ps1 -WhatIf`, then **one manual backup** as the service account: exit code 0, `last-backup.json` status `ok`, and the dump present on the NAS with an equal checksum record.
6. **Isolated restore** of that backup (`scripts\ops\restore-test-isolated.sh` with `RESTORE_TEST_RESULT` set to the configured `RESTORE_TEST_RESULT_FILE`): counts must match live and the result must say `ok`.
7. Alert path without touching Production: run the watchdog with a separate test configuration and state folder that names a missing required destination; expect exactly one alert, then one recovery notice once fixed.
8. `install-production-tasks.ps1 -WhatIf`, then the real run. Start each task once (`Start-ScheduledTask`) and check the results; wait for the first hourly backup and a few watchdog runs and heartbeats.
9. Isolated restore of the first **scheduled** backup.
10. Only after the restore tests succeed, optionally set `BACKUP_PRUNE_DESTINATIONS=true` (see 5). It stays inert until `RESTORE_TEST_RESULT_FILE` shows a successful restore test within 35 days.

**Rollback:** `uninstall-production-tasks.ps1` (removes only the three tasks), delete `monitor.env`, disable the service account, restore the saved ACLs. Backups, Production and Cloudflare are untouched at every step; the Cloudflare tunnel is never started by any of these scripts.

## 8. Limitations

- Positive access to a real UNC share was not exercised in the test environment (no share could be created without administrator rights); path conversion, availability probing and every failure path were tested, and the share path is passed to the same copy-and-verify logic as a local folder.
- The watchdog and the backup run on the host they protect: only the external heartbeat covers a dead PC or network.
- A third-party alert or heartbeat service receives only state text (check name, status, host name); choose it accordingly.
- Entra sign-in secret expiry comes from the configured date, not from a live Entra query; update `ENTRA_SECRET_EXPIRES` on every rotation.
