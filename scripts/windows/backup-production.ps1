<#
.SYNOPSIS
  Windows convenience wrapper around the portable backup scripts in scripts/ops (backup-all.sh): encrypted database backup of the Production PostgreSQL, verification,
  object-store and configuration bundles, and verified copies to extra folders. All real work is done by the bash scripts (Git for Windows' bash).
.DESCRIPTION
  Runs scripts/ops/backup-all.sh through Git for Windows' bash. The encryption passphrase is read from -PassphraseFile (preferred for a scheduled task that runs as a
  dedicated service account: an ACL-restricted file) or, when that is not given, from the Windows DPAPI-protected key file used so far (only that Windows user on this PC
  can read it). A DPAPI-derived passphrase is written to a temporary file readable only by you for the duration of the run and removed afterwards; it is never printed or
  passed on a command line. The backups themselves do NOT depend on DPAPI: they decrypt anywhere with `openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000` and the passphrase.

  Destinations: -CopyDirs are optional extra copies (a missing one only warns). -RequiredCopyDirs are REQUIRED off-host copies (local drive or UNC share such as
  \\nas\helpdesk-backups): if one is unavailable, or its copy cannot be verified, the run is marked failed (status file, exit code 1) even though the local backup exists.
  A required destination's server/share/drive is never created; only a folder below an existing share is.

  Retention (after a fully successful run): every set younger than -RetentionDays and the newest -RetentionMinSets verified sets are kept; older files of the strict backup
  name pattern in the local backup folder are removed. Destinations are pruned only with -PruneDestinations. -WhatIf takes no backup: it shows the resolved destinations,
  their availability and what retention would remove.

  Settings can come from a monitor configuration file (-ConfigFile: BACKUP_DIR, BACKUP_PASSPHRASE_FILE, BACKUP_COPY_DIRS, BACKUP_REQUIRED_DIRS, BACKUP_PRUNE_DESTINATIONS);
  parameters given on the command line win. Exit code: 0 ok, 1 failed, 75 another backup is running.
.EXAMPLE
  .\scripts\windows\backup-production.ps1 -IncludeConfig
  .\scripts\windows\backup-production.ps1 -ConfigFile C:\ProgramData\Helpdesk\monitor.env -IncludeConfig -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ConfigFile = '',
    [string]$BackupDir = (Join-Path $env:USERPROFILE 'HelpdeskBackups\prod'),
    [string[]]$CopyDirs = @('I:\HelpdeskBackups\prod'),
    [string[]]$RequiredCopyDirs = @(),
    [string]$PassphraseFile = '',
    [string]$DpapiKeyFile = (Join-Path $env:USERPROFILE 'HelpdeskBackups\keys\dev-backup-key.dpapi.txt'),
    [string]$PgContainer = 'helpdesk-postgres-prod-1',
    [string]$ObjectVolume = 'helpdesk_objectdata_prod',
    [switch]$IncludeConfig,
    [string]$NamePrefix = 'helpdesk-prod',
    [int]$RetentionDays = 14,
    [int]$RetentionMinSets = 24,
    [switch]$PruneDestinations,
    [switch]$SkipRetention
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'lib\Monitoring.ps1')
. (Join-Path $PSScriptRoot 'lib\BackupRetention.ps1')

if ($ConfigFile) {
    $cfg = Read-MonitorConfig $ConfigFile
    $c = Get-MonitorSettings $cfg
    if (-not $PSBoundParameters.ContainsKey('BackupDir') -and $c.BackupDir) { $BackupDir = $c.BackupDir }
    if (-not $PSBoundParameters.ContainsKey('PassphraseFile') -and $c.PassphraseFile) { $PassphraseFile = $c.PassphraseFile }
    if (-not $PSBoundParameters.ContainsKey('CopyDirs') -and $cfg.ContainsKey('BACKUP_COPY_DIRS')) { $CopyDirs = $c.OptionalBackupDirs }
    if (-not $PSBoundParameters.ContainsKey('RequiredCopyDirs') -and $c.RequiredBackupDirs.Count -gt 0) { $RequiredCopyDirs = $c.RequiredBackupDirs }
    if (-not $PSBoundParameters.ContainsKey('PruneDestinations') -and $c.PruneDestinations) { $PruneDestinations = [switch]$true }
}
$CopyDirs = @($CopyDirs | Where-Object { $_ })
$RequiredCopyDirs = @($RequiredCopyDirs | Where-Object { $_ })

$bash = @('C:\Program Files\Git\bin\bash.exe', 'C:\Program Files (x86)\Git\bin\bash.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $bash) { throw 'Git for Windows bash was not found (install Git for Windows, or run scripts/ops/backup-all.sh in WSL directly).' }

$statusFile = Join-Path $BackupDir 'last-backup.json'

# Destination availability (bounded: a missing NAS must not hang the run).
$optionalOk = @(); foreach ($d in $CopyDirs) { if (Test-BackupDestination $d) { $optionalOk += $d } else { Write-Warning "optional copy destination is not available and is skipped: $(Split-Path $d -Leaf)" } }
$requiredOk = @(); $requiredMissing = @(); $n = 0
foreach ($d in $RequiredCopyDirs) { $n++; if (Test-BackupDestination $d) { $requiredOk += $d } else { $requiredMissing += "required destination $n" } }

if ($WhatIfPreference) {
    Write-Host ("WhatIf: optional destinations available {0}/{1}; required destinations available {2}/{3}" -f $optionalOk.Count, $CopyDirs.Count, $requiredOk.Count, $RequiredCopyDirs.Count)
    if ($requiredMissing.Count -gt 0) { Write-Host ('WhatIf: the run would FAIL: ' + ($requiredMissing -join ', ') + ' unavailable') }
    if (Test-Path -LiteralPath $BackupDir) {
        $r = Invoke-BackupRetention -Directory $BackupDir -Days $RetentionDays -MinSets $RetentionMinSets -WhatIf
        Write-Host ("WhatIf: retention would keep {0} set(s) and remove {1} file(s) in the local folder" -f $r.Kept, @($r.Removed).Count)
    }
    exit 0
}

New-Item -ItemType Directory -Force $BackupDir | Out-Null
$tmp = $null
$code = 1
try {
    if ($PassphraseFile) {
        if (-not (Test-Path $PassphraseFile)) { throw "passphrase file not found: $PassphraseFile" }
        $passPath = (Resolve-Path $PassphraseFile).Path
    } else {
        if (-not (Test-Path $DpapiKeyFile)) { throw "no -PassphraseFile given and no DPAPI key file at $DpapiKeyFile" }
        $blob = (Get-Content $DpapiKeyFile -Raw).Trim()
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR((ConvertTo-SecureString $blob))
        try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        $tmp = [IO.Path]::GetTempFileName()
        & icacls $tmp /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null
        [IO.File]::WriteAllText($tmp, $plain)
        $plain = $null
        $passPath = $tmp
    }

    $env:BACKUP_PASSPHRASE_FILE = ConvertTo-GitBashPath $passPath
    $env:BACKUP_DIR = ConvertTo-GitBashPath $BackupDir
    $env:BACKUP_NAME_PREFIX = $NamePrefix
    $env:PG_CONTAINER = $PgContainer
    if ($ObjectVolume) { $env:OBJECT_VOLUME = $ObjectVolume }
    $env:BACKUP_COPY_DIRS = (@($optionalOk) + @($requiredOk) | ForEach-Object { ConvertTo-GitBashPath $_ }) -join ';'
    if ($IncludeConfig) {
        $cfgPaths = New-Object System.Collections.Generic.List[string]
        foreach ($f in '.env', '.tunnel.env') { if (Test-Path (Join-Path $repo $f)) { $cfgPaths.Add((ConvertTo-GitBashPath (Join-Path $repo $f))) } }
        $envFile = Join-Path $repo '.env'
        $line = if (Test-Path $envFile) { Get-Content $envFile | Where-Object { $_ -match '^SECRETS_DIR=' } | Select-Object -First 1 }
        if ($line) { $dir = ($line -split '=', 2)[1].Trim(); if ($dir -and (Test-Path $dir)) { $cfgPaths.Add((ConvertTo-GitBashPath $dir)) } }
        $env:CONFIG_PATHS = ($cfgPaths -join ';')
    }

    & $bash (ConvertTo-GitBashPath (Join-Path $repo 'scripts\ops\backup-all.sh'))
    $code = $LASTEXITCODE

    if ($code -eq 0 -and $requiredMissing.Count -gt 0) {
        # The local backup exists and is verified, but a REQUIRED off-host copy could not be made: the run as a whole failed.
        Set-BackupStatusFailed $statusFile ('required off-host destination unavailable: ' + ($requiredMissing -join ', ') + '; the local backup was taken but is not copied off this PC')
        Write-Error ('BACKUP FAILED: ' + ($requiredMissing -join ', ') + ' unavailable') -ErrorAction Continue
        $code = 1
    }

    if ($code -eq 0 -and -not $SkipRetention) {
        $targets = @($BackupDir)
        if ($PruneDestinations) { $targets += @($optionalOk) + @($requiredOk) }
        foreach ($t in $targets) {
            try {
                $r = Invoke-BackupRetention -Directory $t -Days $RetentionDays -MinSets $RetentionMinSets
                if (@($r.Removed).Count -gt 0) { Write-Host ("retention: removed {0} expired backup file(s) from {1}" -f @($r.Removed).Count, (Split-Path $t -Leaf)) }
            } catch { Write-Warning "retention skipped for $(Split-Path $t -Leaf): $(Protect-LogText $_.Exception.Message 120)" }
        }
    }
}
finally {
    if ($tmp -and (Test-Path $tmp)) {
        try { [IO.File]::WriteAllBytes($tmp, (New-Object byte[] 128)) } catch { }
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
    foreach ($v in 'BACKUP_PASSPHRASE_FILE', 'BACKUP_DIR', 'BACKUP_NAME_PREFIX', 'PG_CONTAINER', 'OBJECT_VOLUME', 'BACKUP_COPY_DIRS', 'CONFIG_PATHS') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
}
exit $code
