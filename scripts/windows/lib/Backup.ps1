# Encrypted PostgreSQL backups for the Windows Docker Desktop deployment. Same on-disk format as scripts/ops/backup-database.sh (openssl enc -aes-256-cbc -pbkdf2,
# "Salted__" header, sha256sum-style .sha256 file), so a backup made here can be verified and restored with the Linux scripts too and the other way round.
# The passphrase is NEVER stored next to the backups: it lives in a per-user DPAPI-protected file outside the backup directory, and the operator keeps a copy offline.
Set-StrictMode -Version 2.0
Add-Type -AssemblyName System.Security

$script:SecretFile = if ($env:HELPDESK_SECRET_FILE) { $env:HELPDESK_SECRET_FILE } else { Join-Path $env:LOCALAPPDATA 'HelpdeskWin\backup-passphrase.dpapi' }
$script:BackupPattern = 'helpdesk-*.dump.enc'

# ---------- passphrase handling ----------
function Protect-Passphrase([string]$Plain) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Plain)
    return [System.Security.Cryptography.ProtectedData]::Protect($bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
}

function Get-StoredPassphrase {
    if (-not (Test-Path -LiteralPath $script:SecretFile)) { return $null }
    $bytes = [System.Security.Cryptography.ProtectedData]::Unprotect([System.IO.File]::ReadAllBytes($script:SecretFile), $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

# Restore on another PC: the passphrase comes from the operator's offline copy, typed (never echoed, never written to disk).
function Read-PassphraseFromPrompt {
    $secure = Read-Host 'Backup passphrase (from your offline copy)' -AsSecureString
    $ptr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Resolve-Passphrase([switch]$Prompt) {
    if ($Prompt) { return Read-PassphraseFromPrompt }
    $p = Get-StoredPassphrase
    if (-not $p) { Stop-Helpdesk "no stored backup passphrase at $script:SecretFile. Run 'helpdesk.ps1 backup-init' on this PC, or add -PromptPassphrase to type the passphrase from your offline copy." }
    return $p
}

function Initialize-BackupSecret([switch]$Force) {
    $s = Read-EnvFile
    $dir = Get-Setting $s 'BACKUP_DIR'
    if ($dir -ne '' -and ($script:SecretFile.StartsWith($dir, [System.StringComparison]::OrdinalIgnoreCase))) { Stop-Helpdesk 'the passphrase file must not be inside BACKUP_DIR' }
    if ((Test-Path -LiteralPath $script:SecretFile) -and -not $Force) { Stop-Helpdesk "a passphrase already exists at $script:SecretFile. Overwriting it makes existing backups unreadable unless you keep the old one; use -Force only deliberately." }
    $buf = New-Object byte[] 36
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buf)
    $plain = [Convert]::ToBase64String($buf)
    New-Item -ItemType Directory -Force -Path (Split-Path $script:SecretFile) | Out-Null
    [System.IO.File]::WriteAllBytes($script:SecretFile, (Protect-Passphrase $plain))
    Write-Host ''
    Write-Host 'BACKUP PASSPHRASE (shown once). Store it in your password manager or print it and lock it away, NOT on this PC next to the backups:' -ForegroundColor Yellow
    Write-Host "    $plain" -ForegroundColor Cyan
    Write-Host ''
    Write-Host "A protected copy for scheduled backups is stored for user $([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) at $script:SecretFile (it cannot be read by other users or on another PC)."
    Write-Host 'Without the passphrase the backups cannot be restored, including after a disk failure or when moving to another PC.'
}

# ---------- openssl-compatible streaming encryption ----------
function New-KeyIv([string]$Passphrase, [byte[]]$Salt) {
    $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($Passphrase, $Salt, 10000, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $material = $kdf.GetBytes(48)
    return @{ Key = $material[0..31]; Iv = $material[32..47] }
}

function New-AesEncryptor([string]$Passphrase, [byte[]]$Salt) {
    $ki = New-KeyIv $Passphrase $Salt
    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'; $aes.KeySize = 256
    return @{ Aes = $aes; Transform = $aes.CreateEncryptor([byte[]]$ki.Key, [byte[]]$ki.Iv) }
}

function Open-DecryptStream([string]$Path, [string]$Passphrase) {
    $fs = [System.IO.File]::OpenRead($Path)
    $header = New-Object byte[] 16
    if ($fs.Read($header, 0, 16) -ne 16 -or [System.Text.Encoding]::ASCII.GetString($header, 0, 8) -ne 'Salted__') { $fs.Dispose(); Stop-Helpdesk 'not an encrypted Helpdesk backup (missing Salted__ header)' }
    $salt = New-Object byte[] 8
    [Array]::Copy($header, 8, $salt, 0, 8)
    $ki = New-KeyIv $Passphrase $salt
    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'; $aes.KeySize = 256
    return New-Object System.Security.Cryptography.CryptoStream($fs, $aes.CreateDecryptor([byte[]]$ki.Key, [byte[]]$ki.Iv), [System.Security.Cryptography.CryptoStreamMode]::Read)
}

# ---------- docker exec with binary-safe pipes (PowerShell's own pipeline would corrupt binary data) ----------
function Quote-Arg([string]$a) { if ($a -match '[\s"]') { return '"' + $a.Replace('"', '\"') + '"' } return $a }

function Start-ComposeProcess([string[]]$ComposeArgs, [bool]$RedirectIn, [bool]$RedirectOut) {
    $s = Read-EnvFile
    $project = Get-Project $s
    $all = (Get-ComposeBaseArgs $project $s) + $ComposeArgs
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'docker'
    $psi.Arguments = ($all | ForEach-Object { Quote-Arg $_ }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $RedirectIn
    $psi.RedirectStandardOutput = $RedirectOut
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['COMPOSE_PROJECT_NAME'] = $project
    return [System.Diagnostics.Process]::Start($psi)
}

function Get-BackupDir {
    $dir = Get-Setting (Read-EnvFile) 'BACKUP_DIR'
    if ($dir -eq '' -or -not [System.IO.Path]::IsPathRooted($dir)) { Stop-Helpdesk 'BACKUP_DIR must be set to an absolute path in the environment file' }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    return $dir
}

function Write-Status([string]$Dir, [hashtable]$Status) {
    $Status['finishedAt'] = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $tmp = Join-Path $Dir 'last-backup.json.tmp'
    ($Status | ConvertTo-Json -Compress) | Set-Content -LiteralPath $tmp -Encoding ASCII
    Move-Item -LiteralPath $tmp -Destination (Join-Path $Dir 'last-backup.json') -Force
}

# ---------- operations ----------
function Invoke-DatabaseBackup([string]$Dir, [string]$Passphrase) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
    $out = Join-Path $Dir "helpdesk-$stamp.dump.enc"
    $proc = Start-ComposeProcess @('exec', '-T', 'postgres', 'pg_dump', '-U', 'helpdesk', '-d', 'helpdesk', '--format=custom', '--no-owner') $false $true
    $errTask = $proc.StandardError.ReadToEndAsync()
    $ok = $false
    try {
        $salt = New-Object byte[] 8
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($salt)
        $enc = New-AesEncryptor $Passphrase $salt
        $fs = [System.IO.File]::Create($out)
        try {
            $fs.Write([System.Text.Encoding]::ASCII.GetBytes('Salted__'), 0, 8)
            $fs.Write($salt, 0, 8)
            $cs = New-Object System.Security.Cryptography.CryptoStream($fs, $enc.Transform, [System.Security.Cryptography.CryptoStreamMode]::Write)
            $proc.StandardOutput.BaseStream.CopyTo($cs)
            $cs.FlushFinalBlock()
        } finally { $fs.Dispose() }
        $proc.WaitForExit()
        if ($proc.ExitCode -ne 0) { Stop-Helpdesk ("pg_dump failed: " + ($errTask.Result.Trim() -split "`n" | Select-Object -Last 1)) }
        if ((Get-Item -LiteralPath $out).Length -le 1024) { Stop-Helpdesk 'backup is implausibly small; removed' }
        $hash = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant()
        [System.IO.File]::WriteAllText("$out.sha256", "$hash  $(Split-Path $out -Leaf)`n", [System.Text.Encoding]::ASCII)
        $ok = $true
        return $out
    } finally {
        if (-not $ok) { Remove-Item -LiteralPath $out, "$out.sha256" -Force -ErrorAction SilentlyContinue }
    }
}

function Test-BackupFile([string]$Path, [string]$Passphrase, [string[]]$ExpectSchemas = @('identity', 'ticketing')) {
    if (-not (Test-Path -LiteralPath $Path) -or (Get-Item -LiteralPath $Path).Length -eq 0) { Stop-Helpdesk 'backup verification FAILED: file is missing or empty' }
    $sum = "$Path.sha256"
    if (-not (Test-Path -LiteralPath $sum)) { Stop-Helpdesk "backup verification FAILED: checksum file $sum is missing" }
    $expected = ((Get-Content -LiteralPath $sum -TotalCount 1) -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { Stop-Helpdesk 'backup verification FAILED: checksum mismatch (file damaged or altered)' }
    $proc = Start-ComposeProcess @('exec', '-T', 'postgres', 'pg_restore', '--list') $true $true
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    try {
        $ds = Open-DecryptStream $Path $Passphrase
        $unreadable = $false
        try { $ds.CopyTo($proc.StandardInput.BaseStream) } catch { $unreadable = $true }
        try { $ds.Dispose() } catch { $unreadable = $true }   # a wrong passphrase surfaces here as "Padding is invalid"
        if ($unreadable) { try { $proc.StandardInput.Close() } catch { }; Stop-Helpdesk 'backup verification FAILED: cannot decrypt or read the archive (wrong passphrase or damaged file)' }
        $proc.StandardInput.Close()
        $proc.WaitForExit()
        if ($proc.ExitCode -ne 0) { Stop-Helpdesk 'backup verification FAILED: cannot decrypt or read the archive (wrong passphrase or damaged file)' }
        $toc = $outTask.Result
        $tables = ([regex]::Matches($toc, ' TABLE DATA ')).Count
        if ($tables -le 0) { Stop-Helpdesk 'backup verification FAILED: archive lists no table data' }
        foreach ($schema in $ExpectSchemas) { if ($toc -notmatch " TABLE DATA $schema ") { Stop-Helpdesk "backup verification FAILED: no table data for schema '$schema'" } }
        return $tables
    } finally { if (-not $proc.HasExited) { $proc.Kill() } }
}

# Keeps every backup newer than KeepDays and at least MinKeep of the newest ones; deletes only helpdesk-*.dump.enc (and its .sha256) older than that.
function Invoke-BackupRetention([string]$Dir, [int]$KeepDays, [int]$MinKeep) {
    $files = @(Get-ChildItem -LiteralPath $Dir -Filter $script:BackupPattern -File | Sort-Object Name -Descending)
    $cutoff = [DateTime]::UtcNow.AddDays(-$KeepDays)
    $removed = 0
    for ($i = $MinKeep; $i -lt $files.Count; $i++) {
        if ($files[$i].LastWriteTimeUtc -lt $cutoff) {
            Remove-Item -LiteralPath $files[$i].FullName, "$($files[$i].FullName).sha256" -Force -ErrorAction SilentlyContinue
            $removed++
        }
    }
    return $removed
}

# The scheduled entry point: backup, verify, retention, status file. Exit code 1 and a failed status file on any problem.
function Invoke-ScheduledBackup {
    $s = Read-EnvFile
    $dir = Get-BackupDir
    $started = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $lock = Join-Path $dir '.backup.lock'
    try { $lockStream = [System.IO.File]::Open($lock, 'OpenOrCreate', 'ReadWrite', 'None') } catch { Write-Host 'another backup is running; skipping'; return }
    try {
        $pass = Resolve-Passphrase
        $file = Invoke-DatabaseBackup $dir $pass
        $tables = Test-BackupFile $file $pass
        $removed = Invoke-BackupRetention $dir ([int](Get-Setting $s 'BACKUP_KEEP_DAYS' '14')) ([int](Get-Setting $s 'BACKUP_MIN_KEEP' '24'))
        Write-Status $dir @{ status = 'ok'; startedAt = $started; file = (Split-Path $file -Leaf); bytes = (Get-Item -LiteralPath $file).Length; verified = $true; tables = $tables; pruned = $removed }
        Write-Host "backup ok: $(Split-Path $file -Leaf) (verified, $tables tables, pruned $removed)"
    } catch {
        $msg = $_.Exception.Message
        Write-Status $dir @{ status = 'failed'; startedAt = $started; error = $msg }
        Write-Host "BACKUP FAILED: $msg" -ForegroundColor Red
        $alert = Get-Setting $s 'BACKUP_ALERT_COMMAND'
        if ($alert -ne '') { try { & $alert "helpdesk backup failed: $msg" } catch { Write-Host 'alert command failed' } }
        exit 1
    } finally { $lockStream.Dispose() }
}

function Test-BackupFresh([int]$MaxMinutes = 120) {
    $dir = Get-BackupDir
    $f = Join-Path $dir 'last-backup.json'
    if (-not (Test-Path -LiteralPath $f)) { return @{ Ok = $false; Reason = 'no backup status file' } }
    $j = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json
    if ($j.status -ne 'ok') { return @{ Ok = $false; Reason = "last run failed: $($j.error)" } }
    # PowerShell 7 turns ISO timestamps into DateTime while parsing JSON, Windows PowerShell 5.1 leaves a string.
    $finished = $j.finishedAt
    if ($finished -isnot [DateTime]) { $finished = [DateTime]::Parse([string]$finished, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal) }
    $age = ([DateTime]::UtcNow - $finished.ToUniversalTime()).TotalMinutes
    if ($age -gt $MaxMinutes) { return @{ Ok = $false; Reason = "last good backup is $([int]$age) minutes old (limit $MaxMinutes)" } }
    return @{ Ok = $true; Reason = "finished $([int]$age) minutes ago" }
}

function Restore-DatabaseBackup([string]$Path, [string]$Target, [string]$Passphrase) {
    if ($Target -notmatch '^[a-zA-Z0-9_]+$') { Stop-Helpdesk 'target database name must be letters, digits and underscore' }
    Test-BackupFile $Path $Passphrase | Out-Null
    if ($Target -eq 'helpdesk') {
        $app = (Invoke-ComposeText @('ps', '--status', 'running', '--services')).Output
        if ($app -match '(?m)^helpdesk$') { Stop-Helpdesk "stop the application first ('helpdesk.ps1 stop'); restoring into the live database while it runs is refused" }
    }
    $exists = Invoke-Psql 'postgres' "SELECT 1 FROM pg_database WHERE datname='$Target'"
    if ($exists -eq '1') {
        $objects = Invoke-Psql $Target "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema')"
        if ($objects -ne '0') { Stop-Helpdesk "database $Target is not empty; refusing to restore over it (restore into a new name, then swap)" }
    } else {
        $c = Invoke-ComposeText @('exec', '-T', 'postgres', 'psql', '-U', 'helpdesk', '-d', 'postgres', '-c', "CREATE DATABASE $Target")
        if ($c.ExitCode -ne 0) { Stop-Helpdesk "cannot create $Target`: $($c.Output)" }
    }
    $proc = Start-ComposeProcess @('exec', '-T', 'postgres', 'pg_restore', '-U', 'helpdesk', '-d', $Target, '--no-owner', '--exit-on-error') $true $false
    $errTask = $proc.StandardError.ReadToEndAsync()
    $ds = Open-DecryptStream $Path $Passphrase
    try { $ds.CopyTo($proc.StandardInput.BaseStream) } finally { $ds.Dispose(); $proc.StandardInput.Close() }
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) { Stop-Helpdesk ("pg_restore failed: " + ($errTask.Result.Trim() -split "`n" | Select-Object -Last 1)) }
    Write-Host "restored $(Split-Path $Path -Leaf) into database $Target"
}

# Restores into a generated scratch database, checks it, measures time against RTO/RPO, drops it. Cannot touch the live database
# (the target name is generated, and only a restore_test_ database is ever dropped).
function Invoke-RestoreTest([string]$Path, [string]$Passphrase, [string]$ResultFile, [switch]$Keep, [int]$RtoMinutes = 240, [int]$RpoMinutes = 60) {
    $scratch = 'restore_test_' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $result = 'failed'; $detail = ''
    try {
        Restore-DatabaseBackup $Path $scratch $Passphrase
        $tables = [int](Invoke-Psql $scratch "SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema')")
        if ($tables -le 0) { Stop-Helpdesk 'restored database has no tables' }
        foreach ($schema in 'identity', 'ticketing') {
            if ([int](Invoke-Psql $scratch "SELECT count(*) FROM pg_tables WHERE schemaname = '$schema'") -le 0) { Stop-Helpdesk "schema '$schema' is missing or empty after restore" }
        }
        $result = 'ok'; $detail = "$tables tables restored; row counts not read"
    } catch { $detail = $_.Exception.Message } finally {
        if (-not $Keep -and $scratch -like 'restore_test_*') { Invoke-ComposeText @('exec', '-T', 'postgres', 'psql', '-U', 'helpdesk', '-d', 'postgres', '-c', "DROP DATABASE IF EXISTS $scratch") | Out-Null }
    }
    $sw.Stop()
    $age = 0
    if ((Split-Path $Path -Leaf) -match '^helpdesk-(\d{8}T\d{6}Z)\.') {
        $made = [DateTime]::ParseExact($Matches[1], 'yyyyMMddTHHmmssZ', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
        $age = [int]([DateTime]::UtcNow - $made).TotalMinutes
    }
    $summary = [ordered]@{ result = $result; backup = (Split-Path $Path -Leaf); scratchDatabase = $scratch; restoreSeconds = [int]$sw.Elapsed.TotalSeconds; backupAgeMinutes = $age
        withinRto = ($sw.Elapsed.TotalMinutes -le $RtoMinutes); withinRpo = ($age -le $RpoMinutes); detail = $detail; testedAt = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') }
    ($summary | ConvertTo-Json -Compress) | Set-Content -LiteralPath $ResultFile -Encoding ASCII
    Write-Host ($summary | ConvertTo-Json -Compress)
    if ($result -ne 'ok') { Stop-Helpdesk "RESTORE TEST FAILED: $detail" }
}

# Rollback helper: with the application stopped, the live database is renamed aside (never dropped) and a restored database takes its name.
function Switch-RestoredDatabase([string]$Restored) {
    if ($Restored -notmatch '^[a-zA-Z0-9_]+$' -or $Restored -eq 'helpdesk' -or $Restored -eq 'postgres') { Stop-Helpdesk 'pass -Database <name of a restored database> (not helpdesk)' }
    $app = (Invoke-ComposeText @('ps', '--status', 'running', '--services')).Output
    if ($app -match '(?m)^(helpdesk|nginx)$') { Stop-Helpdesk "stop the application first ('helpdesk.ps1 stop')" }
    Invoke-ComposeText @('up', '-d', 'postgres') | Out-Null
    if ((Invoke-Psql 'postgres' "SELECT 1 FROM pg_database WHERE datname='$Restored'") -ne '1') { Stop-Helpdesk "database $Restored does not exist" }
    $aside = 'helpdesk_replaced_' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
    $r = Invoke-ComposeText @('exec', '-T', 'postgres', 'psql', '-U', 'helpdesk', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c', "ALTER DATABASE helpdesk RENAME TO $aside", '-c', "ALTER DATABASE $Restored RENAME TO helpdesk")
    if ($r.ExitCode -ne 0) { Stop-Helpdesk "rename failed (is something still connected?): $($r.Output)" }
    Write-Host "the previous database is kept as $aside; $Restored is now helpdesk. Next: build (if the code changed), start, verify."
}
