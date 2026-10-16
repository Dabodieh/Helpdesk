# Watchdog checks for the Windows Docker Desktop Production stack. Every external call goes through Invoke-ExternalWithTimeout / Test-PathWithTimeout
# (explicit time limits) and every check returns New-CheckResult objects. Requires lib\Monitoring.ps1. Read-only: nothing is started, changed or sent.

function Get-BootAgeMinutes { return ((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalMinutes }

function Get-DockerDesktopRunning { return [bool](Get-Process -Name 'Docker Desktop' -ErrorAction SilentlyContinue) }

function Get-StatusFile([string]$BackupDir) {
    $f = Join-Path $BackupDir 'last-backup.json'
    if (-not (Test-Path -LiteralPath $f)) { return $null }
    try { return (Get-Content -LiteralPath $f -Raw | ConvertFrom-Json) } catch { return $null }
}

# Docker engine reachable. Before sign-in Docker Desktop does not run (a Windows/Docker limitation), so this is also the "restarted, nobody signed in" detector.
function Test-DockerEngine($S) {
    $r = Invoke-ExternalWithTimeout 'docker' @('info', '--format', '{{.ServerVersion}}') $S.CommandTimeoutSec
    if (-not $r.TimedOut -and $r.ExitCode -eq 0 -and $r.Output.Trim()) { return New-CheckResult 'docker-engine' 'Docker engine' 'OK' 'engine responding' }
    $age = Get-BootAgeMinutes
    if ($age -lt $S.BootGraceMinutes) { return New-CheckResult 'docker-engine' 'Docker engine' 'SKIP' 'still starting after a restart' }
    $why = if ($r.TimedOut) { "no answer within $($S.CommandTimeoutSec) s" } else { "not available (exit $($r.ExitCode))" }
    if (-not (Get-DockerDesktopRunning)) { $why += '; Docker Desktop is not running (it starts only when a user signs in: sign in to the PC)' }
    return New-CheckResult 'docker-engine' 'Docker engine' 'FAIL' $why
}

function Test-StackContainers($S) {
    $out = @()
    foreach ($c in @(@('postgres', 'Database container', $S.PgContainer), @('app', 'Application container', $S.AppContainer), @('objectstore', 'Object store container', $S.ObjectContainer))) {
        $r = Invoke-ExternalWithTimeout 'docker' @('inspect', $c[2], '--format', '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}') $S.CommandTimeoutSec
        $key = "container-$($c[0])"
        if ($r.TimedOut -or $r.ExitCode -ne 0) { $out += New-CheckResult $key $c[1] 'FAIL' $(if ($r.TimedOut) { 'docker did not answer in time' } else { 'container not found' }); continue }
        $state, $health = ($r.Output.Trim()).Split('|')
        if ($state -ne 'running') { $out += New-CheckResult $key $c[1] 'FAIL' "state is $state" }
        elseif ($health -notin 'healthy', 'none') { $out += New-CheckResult $key $c[1] 'FAIL' "health is $health" }
        else { $out += New-CheckResult $key $c[1] 'OK' "running ($health)" }
    }
    return $out
}

function Test-DatabaseReady($S) {
    $r = Invoke-ExternalWithTimeout 'docker' @('exec', $S.PgContainer, 'sh', '-c', 'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB"') $S.CommandTimeoutSec
    if (-not $r.TimedOut -and $r.ExitCode -eq 0) { return New-CheckResult 'database' 'Database' 'OK' 'accepting connections' }
    return New-CheckResult 'database' 'Database' 'FAIL' $(if ($r.TimedOut) { "no answer within $($S.CommandTimeoutSec) s" } else { 'not accepting connections' })
}

# The application's own /health/ready (PostgreSQL, object store and migrations), asked from inside its container with the host name it is configured to accept.
function Test-AppReady($S) {
    $script = @'
h="${AllowedHosts%%;*}"; [ -n "$h" ] || h=localhost
exec 3<>/dev/tcp/127.0.0.1/8080 || exit 2
printf 'GET /health/ready HTTP/1.0\r\nHost: %s\r\n\r\n' "$h" >&3
head -n 1 <&3
'@
    $r = Invoke-ExternalWithTimeout 'docker' @('exec', '-i', $S.AppContainer, 'bash') $S.CommandTimeoutSec $script
    if ($r.TimedOut) { return New-CheckResult 'app-ready' 'Application readiness' 'FAIL' "no answer within $($S.CommandTimeoutSec) s" }
    if ($r.Output -match ' 200 ') { return New-CheckResult 'app-ready' 'Application readiness' 'OK' 'ready (200)' }
    $code = if ($r.Output -match 'HTTP/1\.[01] (\d{3})') { $Matches[1] } else { 'no response' }
    return New-CheckResult 'app-ready' 'Application readiness' 'FAIL' "/health/ready answered: $code"
}

# Inbound mail (delta synchronisation): an active mailbox whose last successful check is older than the limit.
function Test-MailSync($S) {
    $sql = "select m.is_active, coalesce(extract(epoch from (now() - s.last_sync_at)) / 60, -1) from mail.mailboxes m left join mail.mailbox_sync s on s.mailbox_id = m.id;"
    $r = Invoke-ExternalWithTimeout 'docker' @('exec', '-i', $S.PgContainer, 'sh', '-c', 'psql -X -At -F"|" -U "$POSTGRES_USER" -d "$POSTGRES_DB"') $S.CommandTimeoutSec $sql
    if ($r.TimedOut -or $r.ExitCode -ne 0) { return New-CheckResult 'mail-sync' 'Inbound mail check' 'FAIL' 'could not read the mail check state' }
    $stale = @()
    $n = 0
    foreach ($line in ($r.Output -split "`n" | Where-Object { $_.Trim() })) {
        $act, $mins = $line.Trim().Split('|')
        if ($act -ne 't') { continue }
        $n++
        $m = [double]::Parse($mins, [Globalization.CultureInfo]::InvariantCulture)
        if ($m -lt 0) { $stale += 'never completed' } elseif ($m -gt $S.MailSyncStaleMinutes) { $stale += ('last check {0:n0} min ago' -f $m) }
    }
    if ($stale.Count -gt 0) { return New-CheckResult 'mail-sync' 'Inbound mail check' 'FAIL' ("stale (limit $($S.MailSyncStaleMinutes) min): " + ($stale -join '; ')) }
    return New-CheckResult 'mail-sync' 'Inbound mail check' 'OK' $(if ($n -eq 0) { 'no active mailbox' } else { "$n active mailbox(es) checked recently" })
}

# A verified backup must exist and be recent. The wrapper writes last-backup.json only after verification (and marks it failed when a required copy is missing).
function Test-BackupFreshness($S, [datetime]$Now = [datetime]::UtcNow) {
    $st = Get-StatusFile $S.BackupDir
    if (-not $st) { return New-CheckResult 'backup-fresh' 'Backup' 'FAIL' 'no backup status found' }
    if ($st.status -ne 'ok') { return New-CheckResult 'backup-fresh' 'Backup' 'FAIL' ('last backup failed: ' + (Protect-LogText ([string]$st.error) 200)) }
    $done = [datetime]::Parse([string]$st.finishedAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal -bor [Globalization.DateTimeStyles]::AssumeUniversal)
    $age = ($Now - $done).TotalMinutes
    if (-not $st.verified) { return New-CheckResult 'backup-fresh' 'Backup' 'FAIL' 'last backup is not marked verified' }
    if ($age -gt $S.BackupMaxAgeMinutes) { return New-CheckResult 'backup-fresh' 'Backup' 'FAIL' ('no verified backup for {0:n0} min (limit {1})' -f $age, $S.BackupMaxAgeMinutes) }
    return New-CheckResult 'backup-fresh' 'Backup' 'OK' ('verified backup {0:n0} min ago' -f $age)
}

# Required off-host destinations: reachable, and hold the newest dump with the same size and checksum record as the local one. Missing configuration is a failure.
function Test-OffHostBackup($S) {
    if ($S.RequiredBackupDirs.Count -eq 0) { return @(New-CheckResult 'backup-offhost' 'Off-host backup' 'FAIL' 'no required off-host destination is configured (BACKUP_REQUIRED_DIRS)') }
    $st = Get-StatusFile $S.BackupDir
    if (-not $st -or -not $st.file) { return @(New-CheckResult 'backup-offhost' 'Off-host backup' 'FAIL' 'no backup status to compare with') }
    $local = Join-Path $S.BackupDir ([string]$st.file)
    $problems = @()
    $i = 0
    foreach ($d in $S.RequiredBackupDirs) {
        $i++
        if (-not (Test-PathWithTimeout $d $S.CommandTimeoutSec)) { $problems += "destination $i unavailable"; continue }
        $copy = Join-Path $d ([string]$st.file)
        $side = "$copy.sha256"
        $r = Invoke-WithTimeout {
            param($c, $s, $l)
            if (-not ((Test-Path -LiteralPath $c) -and (Test-Path -LiteralPath $s))) { return 'missing' }
            if ((Get-Item -LiteralPath $c).Length -ne (Get-Item -LiteralPath $l).Length) { return 'size' }
            $a = ((Get-Content -LiteralPath $s -Raw) -split '\s+')[0]
            $b = ((Get-Content -LiteralPath "$l.sha256" -Raw) -split '\s+')[0]
            if ($a -ine $b) { return 'checksum' }
            return 'ok'
        } @($copy, $side, $local) $S.CommandTimeoutSec
        if ($r.TimedOut) { $problems += "destination $i did not answer in time" }
        elseif ($r.Result -ne 'ok') { $problems += "destination $i copy $($r.Result)" }
    }
    if ($problems.Count -gt 0) { return @(New-CheckResult 'backup-offhost' 'Off-host backup' 'FAIL' ($problems -join '; ')) }
    return @(New-CheckResult 'backup-offhost' 'Off-host backup' 'OK' "newest backup present and matching at $($S.RequiredBackupDirs.Count) destination(s)")
}

function Test-DiskSpace($S) {
    try {
        $root = [IO.Path]::GetPathRoot($S.BackupDir)
        if ($root.StartsWith('\\')) { return New-CheckResult 'disk' 'Backup disk space' 'SKIP' 'network location' }
        $free = (New-Object IO.DriveInfo($root)).AvailableFreeSpace / 1GB
        if ($free -lt $S.MinFreeDiskGb) { return New-CheckResult 'disk' 'Backup disk space' 'FAIL' ('{0:n1} GB free (limit {1} GB)' -f $free, $S.MinFreeDiskGb) }
        return New-CheckResult 'disk' 'Backup disk space' 'OK' ('{0:n1} GB free' -f $free)
    } catch { return New-CheckResult 'disk' 'Backup disk space' 'FAIL' 'could not read the disk' }
}

# Sign-in secret (configured date) and certificate files: warnings at the configured day counts (30, 14, 7), then expired. The level changes the alert, so each tier notifies once.
function Test-CredentialExpiry($S, [datetime]$Now = [datetime]::UtcNow) {
    $out = @()
    if ($S.EntraSecretExpires) {
        $d = [datetime]::ParseExact($S.EntraSecretExpires, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        $left = ($d.Date.AddDays(1) - $Now).TotalDays
        $lvl = Get-ExpiryLevel $left $S.ExpiryWarnDays
        if ($lvl) { $out += New-CheckResult 'expiry-entra-secret' 'Sign-in secret expiry' 'FAIL' $(if ($lvl -eq 'expired') { 'the Microsoft Entra sign-in secret has EXPIRED' } else { 'the Microsoft Entra sign-in secret expires in {0:n0} day(s)' -f [math]::Max(0, [math]::Floor($left)) }) $lvl $true }
        else { $out += New-CheckResult 'expiry-entra-secret' 'Sign-in secret expiry' 'OK' ('{0:n0} days left' -f $left) }
    } else {
        $out += New-CheckResult 'expiry-entra-secret' 'Sign-in secret expiry' 'FAIL' 'ENTRA_SECRET_EXPIRES is not configured, so the sign-in secret expiry is not monitored' 'unset'
    }
    $i = 0
    foreach ($spec in $S.CertFiles) {
        $i++
        $key = "expiry-cert-$i"
        $name = "Certificate $i expiry"
        try {
            $na = Get-CertificateExpiry $spec
            $left = ($na - $Now).TotalDays
            $lvl = Get-ExpiryLevel $left $S.ExpiryWarnDays
            if ($lvl) { $out += New-CheckResult $key $name 'FAIL' $(if ($lvl -eq 'expired') { 'certificate has EXPIRED' } else { 'certificate expires in {0:n0} day(s)' -f [math]::Max(0, [math]::Floor($left)) }) $lvl $true }
            else { $out += New-CheckResult $key $name 'OK' ('{0:n0} days left' -f $left) }
        } catch { $out += New-CheckResult $key $name 'FAIL' 'certificate file could not be read' 'unreadable' }
    }
    return $out
}

# The passphrase, token and configuration files must not be readable by broad groups.
function Test-SecretFileAccess($S, [string[]]$Paths) {
    $bad = @()
    $i = 0
    foreach ($p in @($Paths | Where-Object { $_ })) {
        $i++
        try { if ((Test-Path -LiteralPath $p) -and (Test-BroadlyReadable $p)) { $bad += "file $i" } } catch { }
    }
    if ($bad.Count -gt 0) { return New-CheckResult 'secret-file-access' 'Secret file permissions' 'FAIL' ('readable by broad groups: ' + ($bad -join ', ') + '; restrict to the service account and administrators') }
    return New-CheckResult 'secret-file-access' 'Secret file permissions' 'OK' 'restricted'
}

# Runs every check. A check that throws becomes a FAIL (never a crash), dependants are skipped while their dependency is down so one outage is one alert.
function Invoke-WatchdogChecks($S, [string[]]$SecretFiles = @(), [datetime]$Now = [datetime]::UtcNow) {
    $results = New-Object System.Collections.Generic.List[object]
    $safe = {
        param([scriptblock]$Block, [string]$Key, [string]$Name)
        try { & $Block } catch { New-CheckResult $Key $Name 'FAIL' ('check failed: ' + (Protect-LogText $_.Exception.Message 120)) }
    }
    $docker = & $safe { Test-DockerEngine $S } 'docker-engine' 'Docker engine'
    $results.Add($docker)
    $dockerUp = ($docker.Status -eq 'OK')
    if ($dockerUp) {
        foreach ($c in (& $safe { Test-StackContainers $S } 'containers' 'Containers')) { $results.Add($c) }
        $db = & $safe { Test-DatabaseReady $S } 'database' 'Database'
        $results.Add($db)
        $results.Add((& $safe { Test-AppReady $S } 'app-ready' 'Application readiness'))
        if ($db.Status -eq 'OK') { $results.Add((& $safe { Test-MailSync $S } 'mail-sync' 'Inbound mail check')) }
    }
    $results.Add((& $safe { Test-BackupFreshness $S $Now } 'backup-fresh' 'Backup'))
    foreach ($o in (& $safe { Test-OffHostBackup $S } 'backup-offhost' 'Off-host backup')) { $results.Add($o) }
    $results.Add((& $safe { Test-DiskSpace $S } 'disk' 'Backup disk space'))
    foreach ($e in (& $safe { Test-CredentialExpiry $S $Now } 'expiry' 'Credential expiry')) { $results.Add($e) }
    $results.Add((& $safe { Test-SecretFileAccess $S $SecretFiles } 'secret-file-access' 'Secret file permissions'))
    return $results.ToArray()
}
