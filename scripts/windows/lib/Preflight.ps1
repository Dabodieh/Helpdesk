# Activation preflight for unattended Windows operations (scheduled backup, watchdog, recovery). Every check describes the account it runs as: run the script as
# the service account (or let the authorised one-shot task do it) for the result to be about that account. Read-only unless -WriteTests is given, which creates and
# removes one small marker file per backup destination. Requires lib\Monitoring.ps1, lib\WatchdogChecks.ps1, lib\BackupRetention.ps1, lib\AclTemplates.ps1 and
# lib\ProductionTasks.ps1. Results never contain secrets, URLs or file contents.

function New-PreflightResult([string]$Area, [string]$Name, [string]$Status, [string]$Detail = '', [string]$Fix = '') {
    [pscustomobject]@{ Area = $Area; Name = $Name; Status = $Status; Detail = $Detail; Fix = $Fix }
}

# ---- seams (mocked in tests) ---------------------------------------------------------------------------------------------------------------------

function Get-CurrentIdentityInfo {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $groups = @()
    foreach ($g in $id.Groups) { try { $groups += $g.Translate([Security.Principal.NTAccount]).Value } catch { } }
    [pscustomobject]@{ Name = $id.Name; IsAdmin = ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); Groups = $groups; Sid = $id.User.Value }
}

# Local security policy (needs an elevated session); $null when it cannot be read.
function Get-SecurityPolicyText {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('hd-secpol-' + [guid]::NewGuid().ToString('N') + '.inf')
    try {
        $null = & secedit.exe /export /cfg $tmp /areas USER_RIGHTS 2>&1
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tmp)) { return $null }
        return (Get-Content -LiteralPath $tmp -Raw)
    } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

function Resolve-AccountSid([string]$Name) {
    try { return (New-Object Security.Principal.NTAccount($Name)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { return $null }
}

# SIDs of the local groups the account belongs to.
function Get-LocalGroupSidsOfAccount([string]$AccountSid) {
    $out = @()
    try {
        foreach ($g in Get-LocalGroup) {
            foreach ($m in @(Get-LocalGroupMember -Group $g.Name -ErrorAction SilentlyContinue)) { if ($m.SID.Value -eq $AccountSid) { $out += $g.SID.Value } }
        }
    } catch { }
    return $out
}

function Get-BitLockerState([string]$Drive) {
    try { $v = Get-BitLockerVolume -MountPoint $Drive -ErrorAction Stop; return [string]$v.ProtectionStatus } catch { return $null }
}

# Reading a pipe's ACL opens the pipe, which fails while every pipe instance is busy (Windows error 231), so it is retried briefly.
function Get-PipeAcl([string]$Name) {
    for ($i = 0; $i -lt 6; $i++) {
        try { return (Get-Acl -LiteralPath "\\.\pipe\$Name" -ErrorAction Stop) } catch { Start-Sleep -Milliseconds 300 }
    }
    return $null
}

# ---- security policy parsing ---------------------------------------------------------------------------------------------------------------------

function ConvertFrom-SeceditPrivilege([string]$Text, [string]$Privilege) {
    if (-not $Text) { return @() }
    $m = [regex]::Match($Text, "(?m)^\s*$Privilege\s*=\s*(.*)$")
    if (-not $m.Success) { return @() }
    return @($m.Groups[1].Value -split ',' | ForEach-Object { $_.Trim().TrimStart('*') } | Where-Object { $_ })
}

# 'granted' when the account or one of its groups holds the batch-logon right and none of them is denied it; otherwise 'missing' or 'denied'.
function Get-BatchLogonState([string[]]$Holders, [string[]]$DenyHolders, [string]$AccountSid, [string[]]$GroupSids) {
    $mine = @(@($AccountSid) + @($GroupSids) | Where-Object { $_ })
    if (@(@($DenyHolders) | Where-Object { $_ -and ($mine -contains $_) }).Count -gt 0) { return 'denied' }
    if (@(@($Holders) | Where-Object { $_ -and ($mine -contains $_) }).Count -gt 0) { return 'granted' }
    return 'missing'
}

# ---- individual checks ----------------------------------------------------------------------------------------------------------------------------

function Test-PfIdentity($Info, [string]$ServiceUser) {
    $r = @()
    $short = if ($ServiceUser) { ($ServiceUser -split '\\')[-1] } else { '' }
    if ($short -and ($Info.Name -split '\\')[-1] -ine $short) {
        $r += New-PreflightResult 'Account' 'Identity' 'WARN' "running as a different account than the service account; results describe this account. Run as the service account, or use the one-shot task test." ''
    } else { $r += New-PreflightResult 'Account' 'Identity' 'PASS' "running as $($Info.Name)" '' }
    $inDocker = @($Info.Groups | Where-Object { $_ -match '(^|\\)docker-users$' }).Count -gt 0
    if ($inDocker) { $r += New-PreflightResult 'Account' 'docker-users membership' 'PASS' 'member (needed to open the Docker engine pipe)' '' }
    elseif ($Info.IsAdmin) { $r += New-PreflightResult 'Account' 'docker-users membership' 'WARN' 'not a member, but this account is an administrator (which the pipe also allows). The service account should be a standard user in docker-users, not an administrator.' 'net localgroup docker-users <account> /add   (administrator)' }
    else { $r += New-PreflightResult 'Account' 'docker-users membership' 'FAIL' 'not a member of docker-users, so the Docker engine pipe cannot be opened' 'net localgroup docker-users <account> /add   (administrator)' }
    if ($Info.IsAdmin) { $r += New-PreflightResult 'Account' 'Least privilege' 'WARN' 'this account is an administrator; a scheduled-task account should be a standard user' 'Remove the account from Administrators.' }
    return $r
}

function Test-PfBatchLogon($Info, [string]$ServiceUser, [string]$PolicyText) {
    if (-not $ServiceUser) { return New-PreflightResult 'Account' 'Batch-logon right' 'SKIP' 'no service account named (-ServiceUser)' '' }
    $sid = Resolve-AccountSid $ServiceUser
    if (-not $sid) { return New-PreflightResult 'Account' 'Batch-logon right' 'FAIL' 'the service account does not exist on this PC' 'Create the dedicated local account first.' }
    if (-not $Info.IsAdmin -or -not $PolicyText) {
        return New-PreflightResult 'Account' 'Batch-logon right' 'WARN' 'cannot be read without an elevated session; it is proven by the one-shot task test, or by Task Scheduler rejecting the registration' 'Run elevated, or grant "Log on as a batch job" in Local Security Policy > User Rights Assignment.'
    }
    $state = Get-BatchLogonState (ConvertFrom-SeceditPrivilege $PolicyText 'SeBatchLogonRight') (ConvertFrom-SeceditPrivilege $PolicyText 'SeDenyBatchLogonRight') $sid (Get-LocalGroupSidsOfAccount $sid)
    switch ($state) {
        'granted' { return New-PreflightResult 'Account' 'Batch-logon right' 'PASS' 'the service account may log on as a batch job' '' }
        'denied'  { return New-PreflightResult 'Account' 'Batch-logon right' 'FAIL' 'the account (or a group of it) is denied "Log on as a batch job"' 'Remove it from "Deny log on as a batch job".' }
        default   { return New-PreflightResult 'Account' 'Batch-logon right' 'FAIL' 'the account does not hold "Log on as a batch job"' 'Local Security Policy > Local Policies > User Rights Assignment > Log on as a batch job > add the account.' }
    }
}

function Test-PfDocker($S) {
    $out = @()
    $r = Invoke-ExternalWithTimeout $S.DockerExe @('--context', 'default', 'info', '--format', '{{.ServerVersion}}') $S.CommandTimeoutSec
    if (-not $r.TimedOut -and $r.ExitCode -eq 0 -and $r.Output.Trim()) {
        $out += New-PreflightResult 'Docker' 'Engine pipe access' 'PASS' 'the engine answers through the default context (the named pipe; no per-user Docker Desktop profile is needed)' ''
    } else {
        $kind = Get-DockerFailureKind $r
        $st = if ($kind -eq 'unavailable') { 'WARN' } else { 'FAIL' }
        $fix = if ($kind -eq 'access') { 'net localgroup docker-users <account> /add   (administrator), then run the check again' } elseif ($kind -eq 'unavailable') { 'Start Docker Desktop (sign in to the PC) and run the check again.' } else { '' }
        $out += New-PreflightResult 'Docker' 'Engine pipe access' $st (Get-DockerFailureGuidance $kind (Get-DockerDesktopRunning) $S.CommandTimeoutSec $r.ExitCode) $fix
    }
    $acl = Get-PipeAcl 'docker_engine'
    if (-not $acl) { $out += New-PreflightResult 'Docker' 'Pipe permissions' 'WARN' 'the docker_engine pipe permissions could not be read (the pipe was busy or the engine is not running); the engine test above is the authoritative result' '' }
    else {
        $granted = @($acl.Access | Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -match 'docker-users$' }).Count -gt 0
        $out += New-PreflightResult 'Docker' 'Pipe permissions' $(if ($granted) { 'PASS' } else { 'WARN' }) $(if ($granted) { 'docker-users may use the engine pipe' } else { 'docker-users is not listed on the engine pipe' }) ''
    }
    return $out
}

function Find-GitBash {
    return (@('C:\Program Files\Git\bin\bash.exe', 'C:\Program Files (x86)\Git\bin\bash.exe') | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
}

function Test-PfTools($S) {
    $out = @()
    $v = Invoke-ExternalWithTimeout $S.DockerExe @('--version') $S.CommandTimeoutSec
    $out += New-PreflightResult 'Tools' 'Docker CLI' $(if ($v.ExitCode -eq 0 -and -not $v.TimedOut) { 'PASS' } else { 'FAIL' }) $(if ($v.ExitCode -eq 0 -and -not $v.TimedOut) { 'docker.exe runs for this account' } else { 'docker.exe cannot be run (set DOCKER_EXE to its full path if it is not on the PATH of this account)' }) ''
    $bash = Find-GitBash
    if (-not $bash) { return $out + (New-PreflightResult 'Tools' 'Git Bash' 'FAIL' 'Git for Windows bash was not found; the backup scripts run through it' 'Install Git for Windows (customer-licensed, open source).') }
    $b = Invoke-ExternalWithTimeout $bash @('-c', 'command -v openssl sha256sum tar >/dev/null && echo ok') $S.CommandTimeoutSec
    $out += New-PreflightResult 'Tools' 'Git Bash and tools' $(if ($b.Output -match 'ok') { 'PASS' } else { 'FAIL' }) $(if ($b.Output -match 'ok') { 'bash, openssl, sha256sum and tar are available' } else { 'bash runs but openssl, sha256sum or tar is missing' }) ''
    return $out
}

function Test-PfScripts([string]$ScriptRoot) {
    $out = @()
    $need = @('windows\backup-production.ps1', 'windows\watchdog-production.ps1', 'windows\recover-production.ps1', 'windows\health-check-production.ps1', 'windows\lib\Monitoring.ps1',
        'windows\lib\WatchdogChecks.ps1', 'windows\lib\BackupRetention.ps1', 'windows\lib\ModeGuard.ps1', 'ops\backup-all.sh', 'ops\lib-backup.sh', 'ops\backup-database.sh', 'ops\verify-backup.sh')
    $missing = @()
    foreach ($f in $need) {
        $p = Join-Path $ScriptRoot $f
        try { $s = [IO.File]::OpenRead($p); $s.Dispose() } catch { $missing += $f }
    }
    $out += New-PreflightResult 'Scripts' 'Read access to operational scripts' $(if ($missing.Count -eq 0) { 'PASS' } else { 'FAIL' }) $(if ($missing.Count -eq 0) { "all $($need.Count) required scripts are readable" } else { "$($missing.Count) required script(s) missing or unreadable: " + ($missing -join ', ') }) ''
    $writable = @(Get-ScriptTreeWriteIssues $ScriptRoot)
    if ($writable.Count -eq 0) { $out += New-PreflightResult 'Scripts' 'Scripts not writable by ordinary users' 'PASS' 'only administrators and SYSTEM can change the scripts that scheduled tasks run' '' }
    else {
        $out += New-PreflightResult 'Scripts' 'Scripts not writable by ordinary users' 'FAIL' ("$($writable.Count) folder(s)/file(s) can be changed by ordinary users (Users, Authenticated Users or Everyone). A task account with Docker access would run whatever they put there.") 'Install the release under C:\Program Files\Helpdesk and apply the Program ACL template (see docs/architecture/WINDOWS_INSTALL_LAYOUT.md); never run privileged tasks from a shared or development folder.'
    }
    if (Test-Path -LiteralPath (Join-Path (Split-Path $ScriptRoot -Parent) '.git')) { $out += New-PreflightResult 'Scripts' 'Install location' 'WARN' 'the scripts are inside a git working tree (a development checkout), not an installed release' 'Install a release to the Program Files location.' }
    return $out
}

# The backup scripts pass the file to `openssl enc -pass file:`, which uses the first line. Measured with the OpenSSL that ships with Git for Windows (the one the scripts
# use): an LF or CRLF line ending and extra lines do NOT change the key, but a byte-order mark (Notepad adds one), UTF-16, a lone carriage return or any other
# extra character DOES. A CRLF would change the key on other OpenSSL builds (for example when a backup is restored elsewhere), so it is reported as a warning.
# Returns $null when the format is the portable one (text only, optional single LF), otherwise { Severity = FAIL|WARN; Reason }. The content is never returned.
function Test-PassphraseFileFormat([string]$Path) {
    $b = [IO.File]::ReadAllBytes($Path)
    $bad = { param($sev, $why) [pscustomobject]@{ Severity = $sev; Reason = $why } }
    if ($b.Length -eq 0) { return (& $bad 'FAIL' 'the file is empty') }
    if ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) { return (& $bad 'FAIL' 'the file starts with a UTF-8 byte-order mark (Notepad adds one); it changes the key') }
    if ($b.Length -ge 2 -and (($b[0] -eq 0xFF -and $b[1] -eq 0xFE) -or ($b[0] -eq 0xFE -and $b[1] -eq 0xFF))) { return (& $bad 'FAIL' 'the file is UTF-16 encoded; it changes the key') }
    $cr = @($b | Where-Object { $_ -eq 13 }).Count
    $lf = @($b | Where-Object { $_ -eq 10 }).Count
    if ($cr -gt 0 -and $lf -eq 0) { return (& $bad 'FAIL' 'the file contains a lone carriage return; it changes the key') }
    if ($cr -gt 0) { return (& $bad 'WARN' 'the file has Windows line endings (CRLF): fine with the Windows OpenSSL used here, but the key differs on other OpenSSL builds. Prefer a file with no line ending.') }
    if ($lf -gt 1 -or ($lf -eq 1 -and $b[$b.Length - 1] -ne 10)) { return (& $bad 'WARN' 'the file has more than one line; only the first line is used') }
    return $null
}

# Writes a passphrase file in the exact format the backup scripts expect (UTF-8, no BOM, no line ending). Never overwrites. The passphrase comes from a SecureString
# the operator typed; it is not generated, printed or logged here. Restricting the file's permissions is a separate step (Secrets ACL template).
function New-PassphraseFileFromSecureString([string]$Path, [securestring]$Passphrase) {
    if (Test-Path -LiteralPath $Path) { throw 'refusing to overwrite an existing passphrase file' }
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Passphrase)
    try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr); [IO.File]::WriteAllText($Path, $plain, (New-Object Text.UTF8Encoding $false)) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $plain = $null }
}

# A secret-bearing file: present, readable by this account, non-empty, not readable or writable by ordinary users, and not stored inside a backup or script folder.
function Test-PfSecretFile([string]$Path, [string]$Label, [string[]]$KeepOutOf = @(), [bool]$Required = $true, [bool]$IsPassphrase = $false) {
    if (-not $Path) { return New-PreflightResult 'Secrets' $Label $(if ($Required) { 'FAIL' } else { 'SKIP' }) $(if ($Required) { 'not configured' } else { 'not configured (optional)' }) '' }
    if (-not (Test-Path -LiteralPath $Path)) { return New-PreflightResult 'Secrets' $Label 'FAIL' 'the file does not exist (this tool never creates it)' 'Create it yourself; do not generate or paste secrets into scripts.' }
    try { $fs = [IO.File]::OpenRead($Path); $len = $fs.Length; $fs.Dispose() } catch { return New-PreflightResult 'Secrets' $Label 'FAIL' 'this account cannot read the file' 'Grant the service account read access (Secrets/Config ACL template).' }
    if ($len -eq 0) { return New-PreflightResult 'Secrets' $Label 'FAIL' 'the file is empty' '' }
    $problems = @()
    $warn = @()
    if ($IsPassphrase) { $fmt = Test-PassphraseFileFormat $Path; if ($fmt) { if ($fmt.Severity -eq 'FAIL') { $problems += $fmt.Reason } else { $warn += $fmt.Reason } } }
    $acl = Get-PathAcl $Path
    if (@(Get-OrdinaryUserGrants $acl 'Read').Count -gt 0) { $problems += 'readable by ordinary users' }
    if (@(Get-OrdinaryUserGrants $acl 'Write').Count -gt 0) { $problems += 'writable by ordinary users' }
    $full = [IO.Path]::GetFullPath($Path)
    foreach ($k in @($KeepOutOf | Where-Object { $_ })) { $kf = [IO.Path]::GetFullPath($k).TrimEnd('\') + '\'; if ($full.StartsWith($kf, [StringComparison]::OrdinalIgnoreCase)) { $problems += 'stored inside a folder it must stay out of (backups or scripts)' } }
    if ($problems.Count -gt 0) { return New-PreflightResult 'Secrets' $Label 'FAIL' ($problems -join '; ') 'Apply the Secrets/Config ACL template: administrators and SYSTEM full, the service account read only, inheritance off.' }
    if ($warn.Count -gt 0) { return New-PreflightResult 'Secrets' $Label 'WARN' ($warn -join '; ') 'Write the file with scripts\windows\new-backup-passphrase-file.ps1.' }
    return New-PreflightResult 'Secrets' $Label 'PASS' 'present, readable by this account, protected from ordinary users' ''
}

# Creates one marker file in the destination, reads it back, compares it and removes exactly that file. Returns 'ok' or a short reason.
function Invoke-DestinationMarkerTest([string]$Dir, [int]$TimeoutSec = 30) {
    $r = Invoke-WithTimeout {
        param($d)
        $name = '.helpdesk-preflight-' + [guid]::NewGuid().ToString('N') + '.tmp'
        $p = Join-Path $d $name
        $text = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
        try { [IO.File]::WriteAllText($p, $text) } catch { return 'cannot write' }
        $res = 'ok'
        try { if ([IO.File]::ReadAllText($p) -ne $text) { $res = 'read-back differs' } } catch { $res = 'cannot read back' }
        try { Remove-Item -LiteralPath $p -Force } catch { $res = if ($res -eq 'ok') { 'cannot remove the marker file' } else { $res } }
        return $res
    } @($Dir) $TimeoutSec
    if ($r.TimedOut) { return 'no answer in time' }
    return [string]$r.Result
}

function Test-PfBackupDestinations($S, [bool]$WriteTests) {
    $out = @()
    if (-not $S.BackupDir) { return @(New-PreflightResult 'Backup' 'Backup folder' 'FAIL' 'BACKUP_DIR is not configured' '') }
    if (-not (Test-Path -LiteralPath $S.BackupDir)) { $out += New-PreflightResult 'Backup' 'Backup folder' 'FAIL' 'BACKUP_DIR does not exist (create it with the Backups ACL template)' '' }
    else {
        $acl = Get-PathAcl $S.BackupDir
        $bad = @(); if (@(Get-OrdinaryUserGrants $acl 'Write').Count -gt 0) { $bad += 'writable by ordinary users' }; if (@(Get-OrdinaryUserGrants $acl 'Read').Count -gt 0) { $bad += 'readable by ordinary users' }
        $out += New-PreflightResult 'Backup' 'Backup folder permissions' $(if ($bad.Count -eq 0) { 'PASS' } else { 'FAIL' }) $(if ($bad.Count -eq 0) { 'not accessible to ordinary users' } else { $bad -join '; ' }) 'Apply the Backups ACL template.'
        if ($WriteTests) { $m = Invoke-DestinationMarkerTest $S.BackupDir; $out += New-PreflightResult 'Backup' 'Backup folder write test' $(if ($m -eq 'ok') { 'PASS' } else { 'FAIL' }) $(if ($m -eq 'ok') { 'marker file written, read back and removed' } else { $m }) '' }
        else { $out += New-PreflightResult 'Backup' 'Backup folder write test' 'SKIP' 'not run (use -WriteTests)' '' }
    }
    if ($S.RequiredBackupDirs.Count -eq 0) { $out += New-PreflightResult 'Backup' 'Required off-host destination' 'FAIL' 'BACKUP_REQUIRED_DIRS is not configured' '' }
    $i = 0
    foreach ($d in $S.RequiredBackupDirs) {
        $i++
        if (-not (Test-BackupDestination $d $S.CommandTimeoutSec)) { $out += New-PreflightResult 'Backup' "Required destination $i" 'FAIL' 'the drive or share is not reachable by this account (the share must already exist)' 'Check the share, the account permissions on it, and SMB signing support on the NAS.'; continue }
        if ($WriteTests) { $m = Invoke-DestinationMarkerTest $d; $out += New-PreflightResult 'Backup' "Required destination $i read/write" $(if ($m -eq 'ok') { 'PASS' } else { 'FAIL' }) $(if ($m -eq 'ok') { 'marker file written, read back and removed with this account''s network identity' } else { $m }) $(if ($m -eq 'ok') { '' } else { 'Grant this account modify rights on the share and its folder.' }) }
        else { $out += New-PreflightResult 'Backup' "Required destination $i" 'PASS' 'reachable; read/write not tested (use -WriteTests or the one-shot task)' '' }
    }
    return $out
}

function Test-PfSmbClient {
    try {
        $c = Get-SmbClientConfiguration
        $d = 'signing required: ' + $c.RequireSecuritySignature + '; insecure guest logons: ' + $c.EnableInsecureGuestLogons
        $st = if ($c.EnableInsecureGuestLogons) { 'WARN' } else { 'PASS' }
        return New-PreflightResult 'Backup' 'SMB client policy' $st ($d + '. The NAS must support SMB signing and authenticate the account; guest access will not work.') ''
    } catch { return New-PreflightResult 'Backup' 'SMB client policy' 'WARN' 'could not be read' '' }
}

function Test-PfAlertConfig($S) {
    $out = @()
    if (-not $S.AlertUrl) { $out += New-PreflightResult 'Alerts' 'Alert endpoint' 'FAIL' 'ALERT_WEBHOOK_URL is not configured' '' }
    elseif (-not (Test-WebhookUrl $S.AlertUrl $false)) { $out += New-PreflightResult 'Alerts' 'Alert endpoint' 'FAIL' 'ALERT_WEBHOOK_URL must be an https URL' '' }
    else { $out += New-PreflightResult 'Alerts' 'Alert endpoint' 'PASS' "https endpoint configured, format $($S.AlertFormat) (not contacted; use watchdog -TestAlert)" '' }
    if (-not $S.HeartbeatUrl) { $out += New-PreflightResult 'Alerts' 'Heartbeat' 'WARN' 'HEARTBEAT_URL is not configured: nothing can report a dead PC or a locked service account' 'Configure an external dead-man heartbeat (Healthchecks-compatible).' }
    elseif (-not (Test-WebhookUrl $S.HeartbeatUrl $false)) { $out += New-PreflightResult 'Alerts' 'Heartbeat' 'FAIL' 'HEARTBEAT_URL must be an https URL' '' }
    else { $out += New-PreflightResult 'Alerts' 'Heartbeat' 'PASS' 'https heartbeat configured (not contacted)' '' }
    return $out
}

function Test-PfRecoveryEvidence($S) {
    if (-not $S.RestoreTestResultFile) { return New-PreflightResult 'Backup' 'Verified recovery test' 'WARN' 'RESTORE_TEST_RESULT_FILE is not configured; pruning of off-host destinations stays disabled' '' }
    $v = Test-RecoveryVerified $S.RestoreTestResultFile
    return New-PreflightResult 'Backup' 'Verified recovery test' $(if ($v.Verified) { 'PASS' } else { 'WARN' }) $(if ($v.Verified) { 'a successful isolated restore test is recent' } else { "$($v.Reason). Pruning of off-host destinations stays disabled until a restore test succeeds." }) 'Run scripts\ops\restore-test-isolated.sh against a recent backup with RESTORE_TEST_RESULT set to the configured file.'
}

function Test-PfDiskAndEncryption($S, $Info) {
    $out = @()
    if ($S.BackupDir) {
        try {
            $root = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($S.BackupDir))
            $free = (New-Object IO.DriveInfo($root)).AvailableFreeSpace / 1GB
            $newest = if (Test-Path -LiteralPath $S.BackupDir) { Get-ChildItem -LiteralPath $S.BackupDir -Filter '*.dump.enc' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1 } else { $null }
            $need = [math]::Max($S.MinFreeDiskGb, $(if ($newest) { 30 * $newest.Length / 1GB } else { 0 }))
            $out += New-PreflightResult 'Host' 'Backup disk space' $(if ($free -ge $need) { 'PASS' } else { 'FAIL' }) ('{0:n1} GB free; {1:n1} GB needed (minimum, or room for about 30 backups)' -f $free, $need) ''
            $drive = $root.TrimEnd('\')
        } catch { $out += New-PreflightResult 'Host' 'Backup disk space' 'WARN' 'could not be read' ''; $drive = $null }
    } else { $drive = $null }
    if (-not $Info.IsAdmin) { $out += New-PreflightResult 'Host' 'BitLocker' 'WARN' 'cannot be read without an elevated session' 'Run elevated, or confirm disk encryption by hand.' }
    else {
        foreach ($d in @($env:SystemDrive, $drive) | Where-Object { $_ } | Select-Object -Unique) {
            $s = Get-BitLockerState $d
            $out += New-PreflightResult 'Host' "BitLocker $d" $(if ($s -eq 'On') { 'PASS' } else { 'WARN' }) $(if ($s -eq 'On') { 'protection on' } elseif ($s) { "protection $s (the database, its backups and the Data Protection keys are unencrypted at rest)" } else { 'status could not be read' }) ''
        }
    }
    return $out
}

function Test-PfLayout($Layout, [string]$ServiceAccount) {
    $out = @()
    foreach ($pair in @(@('Program', $Layout.InstallRoot), @('Config', $Layout.Config), @('Secrets', $Layout.Secrets), @('State', $Layout.State), @('Backups', $Layout.BackupRoot))) {
        if (-not (Test-Path -LiteralPath $pair[1])) { $out += New-PreflightResult 'Layout' "$($pair[0]) folder" 'SKIP' 'not created yet' ''; continue }
        $issues = @(Test-PathAgainstTemplate $pair[1] (Get-HelpdeskAclTemplate -Kind $pair[0] -ServiceAccount $ServiceAccount))
        $fails = @($issues | Where-Object { $_.Severity -eq 'FAIL' })
        $out += New-PreflightResult 'Layout' "$($pair[0]) folder permissions" $(if ($fails.Count -gt 0) { 'FAIL' } elseif ($issues.Count -gt 0) { 'WARN' } else { 'PASS' }) $(if ($issues.Count -eq 0) { 'matches the template' } else { ($issues | ForEach-Object { $_.Message }) -join '; ' }) ''
    }
    return $out
}

# ---- orchestration --------------------------------------------------------------------------------------------------------------------------------

function Invoke-ActivationPreflight {
    param([Parameter(Mandatory)]$Settings, [Parameter(Mandatory)][string]$ScriptRoot, [string]$ServiceUser = '', [string]$ConfigFile = '', $Layout = $null, [bool]$WriteTests = $false)
    $info = Get-CurrentIdentityInfo
    $r = New-Object System.Collections.Generic.List[object]
    $add = { param($items) foreach ($i in @($items)) { if ($i) { $r.Add($i) } } }
    & $add (Test-PfIdentity $info $ServiceUser)
    & $add (Test-PfBatchLogon $info $ServiceUser $(if ($info.IsAdmin) { Get-SecurityPolicyText } else { $null }))
    & $add (Test-PfDocker $Settings)
    & $add (Test-PfTools $Settings)
    & $add (Test-PfScripts $ScriptRoot)
    $keepOut = @($Settings.BackupDir, $ScriptRoot)
    & $add (Test-PfSecretFile $ConfigFile 'Configuration file' $keepOut $true)
    & $add (Test-PfSecretFile $Settings.PassphraseFile 'Backup passphrase file' $keepOut $true $true)
    & $add (Test-PfSecretFile $Settings.AlertTokenFile 'Alert token file' $keepOut $false)
    & $add (Test-PfBackupDestinations $Settings $WriteTests)
    & $add (Test-PfSmbClient)
    & $add (Test-PfAlertConfig $Settings)
    & $add (Test-PfRecoveryEvidence $Settings)
    & $add (Test-PfDiskAndEncryption $Settings $info)
    if ($Layout) { & $add (Test-PfLayout $Layout $(if ($ServiceUser) { $ServiceUser } else { $info.Name })) }
    return $r.ToArray()
}

# The decisive test of "can the service account really do this unattended": a temporary one-shot task, registered with the account's password, runs this same preflight
# (with -WriteTests) under that account's own logon, writes the results to a file, and is always removed afterwards. Needs the explicit -Authorise switch and an elevated session.
function Invoke-OneShotPreflight {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$ConfigFile, [Parameter(Mandatory)][string]$ServiceUser, [securestring]$ServicePassword,
          [Parameter(Mandatory)][string]$ResultFile, [int]$TimeoutSec = 180, [switch]$Authorise)
    if (-not $Authorise) { throw 'refusing: the one-shot task test registers a temporary scheduled task and needs the explicit -AuthoriseOneShotTask switch' }
    $name = 'Helpdesk Preflight (temporary)'
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $script = Join-Path $RepoRoot 'scripts\windows\preflight-production-activation.ps1'
    $def = [pscustomobject]@{
        Name = $name; Description = 'Temporary Helpdesk activation preflight; removed automatically.'; Execute = $ps
        Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`" -ConfigFile `"$ConfigFile`" -ServiceUser `"$ServiceUser`" -WriteTests -ResultFile `"$ResultFile`" -Quiet"
        Trigger = 'Once'; RepeatMinutes = 0; DelayMinutes = 1; TimeLimitMinutes = 10; RunAs = $ServiceUser; LogonType = 'Password'
    }
    if (-not $PSCmdlet.ShouldProcess($name, "register, run once as $ServiceUser, collect the result and remove")) { return $null }
    if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { throw "a task named '$name' already exists; remove it first" }
    if (Test-Path -LiteralPath $ResultFile) { Remove-Item -LiteralPath $ResultFile -Force }
    try {
        Register-ProductionTasks -Definitions @($def) -ServicePassword $ServicePassword
        Start-ScheduledTask -TaskName $name
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
            $t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            if ($t -and $t.State -ne 'Running' -and (Test-Path -LiteralPath $ResultFile)) { break }
        }
        if (-not (Test-Path -LiteralPath $ResultFile)) {
            $info = Get-ScheduledTaskInfo -TaskName $name -ErrorAction SilentlyContinue
            return @(New-PreflightResult 'OneShot' 'Task run as the service account' 'FAIL' ("the task produced no result within $TimeoutSec s (last result code: " + $(if ($info) { '0x{0:X}' -f $info.LastTaskResult } else { 'unknown' }) + '). Typical causes: the batch-logon right is missing, the password is wrong, or the account cannot read the scripts or configuration.') 'Fix the cause and run the one-shot test again.')
        }
        return @((Get-Content -LiteralPath $ResultFile -Raw | ConvertFrom-Json) | ForEach-Object { New-PreflightResult $_.Area $_.Name $_.Status $_.Detail $_.Fix })
    }
    finally { Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue }
}
