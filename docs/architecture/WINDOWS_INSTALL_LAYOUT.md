# Windows installation layout and permission templates

Design for a commercial, self-hosted Helpdesk on **Windows 11 Pro with Docker Desktop**. Nothing here is specific to one organisation: paths, accounts and shares are chosen by the customer's IT administrator. Implemented as data and verification code in `scripts\windows\lib\AclTemplates.ps1`; nothing is applied by the repository.

## 1. Principle

Scheduled tasks run as a dedicated service account that must be in `docker-users` to reach the Docker engine. **Docker access is effectively root-equivalent on the host**, so anything that account executes (the operational scripts) must be changeable by Administrators and SYSTEM only. Secrets and backups must not be readable by ordinary users. A development checkout, a shared drive or a user profile is therefore not an acceptable place to run privileged tasks from. `install-production-tasks.ps1` refuses to register tasks when ordinary users can change the script tree, and the activation preflight reports it as a failure.

## 2. Layout (all three roots are configurable)

| Folder | Default | Contents | Service account | Ordinary users |
|---|---|---|---|---|
| Release (`InstallRoot`) | `C:\Program Files\Helpdesk` | `scripts\`, Compose files, release manifest | read and execute | read and execute |
| Configuration (`DataRoot\config`) | `C:\ProgramData\Helpdesk\config` | `monitor.env`, the Production `.env` | read | none |
| Secrets (`DataRoot\secrets`) | `C:\ProgramData\Helpdesk\secrets` | backup passphrase file, alert token file, certificates and password files | read | none |
| Monitor state (`DataRoot\monitor`) | `C:\ProgramData\Helpdesk\monitor` | alert state, `watchdog.log`, restore-test result, preflight result | modify | none |
| Backups (`BackupRoot`) | `C:\ProgramData\Helpdesk\backups` (customer should choose another disk) | encrypted dumps, bundles, `last-backup.json` | modify | none |
| Off-host copy | customer's NAS/SMB share | the same files, verified | modify on the share (own network identity) | per NAS policy |

Docker volumes (PostgreSQL, object store) stay inside Docker Desktop's own storage; the customer picks that location in Docker Desktop's settings. Backups, not volumes, are the recovery path.

## 3. ACL templates (inheritance off; entries by SID, so they are language independent)

| Template | SYSTEM | Administrators | Service account | Users | Notes |
|---|---|---|---|---|---|
| Program | Full | Full | Read and execute | Read and execute | nobody else may write |
| Config | Full | Full | Read | none | |
| Secrets | Full | Full | Read | none | |
| State | Full | Full | Modify | none | |
| Backups | Full | Full | Modify | none | |

Verification (`Test-AclAgainstTemplate`, `Test-PathAgainstTemplate`, and the preflight's layout check) fails on: inheritance still on, a missing or weaker required entry, the service account holding more than the template (for example Modify where Read is required), and Users, Authenticated Users or Everyone able to change anything, or to read anything except in the Program template. Unexpected extra identities are warnings. Deny rules and CREATOR OWNER entries are ignored.

Applying a template: `Get-IcaclsCommands` prints the exact `icacls` lines for review. `Set-HelpdeskAcl` applies one (elevated, `-WhatIf` supported) but refuses: drive roots, network paths, any path outside the three layout roots, a layout root that is a drive root, and any folder inside a git working tree. It was written and tested without changing any permission on the development PC.

## 4. Account and share requirements

- Service account: local standard user, strong password held by the customer, member of `docker-users`, granted "Log on as a batch job", ideally denied interactive and remote logon, never an administrator. Tasks use a password logon (not S4U) so the account has a network identity.
- NAS/SMB: the share must already exist; the account needs modify rights on it and the NAS must support SMB signing (Windows 11 requires it by default) and real authentication (guest access is blocked). No drive letters are mapped (they are per-session and invisible to tasks); UNC paths only.
- The passphrase file is written with `scripts\windows\new-backup-passphrase-file.ps1` (UTF-8, no byte-order mark, no line ending). Use the **same** passphrase as existing backups if they must remain restorable with it. Measured with the Windows OpenSSL the scripts use: LF or CRLF line endings do not change the key, but a byte-order mark, UTF-16 or a lone carriage return do; CRLF would change it on other OpenSSL builds, so it is only a warning.

## 5. What is deliberately not done

No service account, folder, ACL, task or credential is created by this repository. The installer/wizard that would create the layout is a later milestone; these templates and the preflight are its safety checks.
