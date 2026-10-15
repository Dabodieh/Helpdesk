<#
.SYNOPSIS
  Windows convenience wrapper around the portable backup scripts in scripts/ops (backup-all.sh): encrypted database backup of the Production PostgreSQL, verification,
  object-store and configuration bundles, and verified copies to extra folders. All real work is done by the bash scripts, which run unchanged on Linux/Unraid.
.DESCRIPTION
  Runs scripts/ops/backup-all.sh through Git for Windows' bash. The encryption passphrase is read from -PassphraseFile (portable, preferred) or, when that is not
  given, from the Windows DPAPI-protected key file used so far (only this account on this PC can read it). It is written to a temporary file readable only by you for
  the duration of the run and removed afterwards; it is never printed or passed on a command line.
  The backups themselves do NOT depend on DPAPI: they decrypt anywhere with `openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000` and the passphrase (keep a copy in the
  password manager). Exit code: 0 ok, 1 failed, 75 another backup is running.
.EXAMPLE
  .\scripts\windows\backup-production.ps1
  .\scripts\windows\backup-production.ps1 -CopyDirs 'I:\HelpdeskBackups\prod' -IncludeConfig
#>
param(
    [string]$BackupDir = (Join-Path $env:USERPROFILE 'HelpdeskBackups\prod'),
    [string[]]$CopyDirs = @('I:\HelpdeskBackups\prod'),
    [string]$PassphraseFile = '',
    [string]$DpapiKeyFile = (Join-Path $env:USERPROFILE 'HelpdeskBackups\keys\dev-backup-key.dpapi.txt'),
    [string]$PgContainer = 'helpdesk-postgres-prod-1',
    [string]$ObjectVolume = 'helpdesk_objectdata_prod',
    [switch]$IncludeConfig,
    [string]$NamePrefix = 'helpdesk-prod'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function ConvertTo-GitPath([string]$p) {
    $full = [IO.Path]::GetFullPath($p)
    if ($full -match '^([A-Za-z]):\\(.*)$') { return '/' + $Matches[1].ToLower() + '/' + ($Matches[2] -replace '\\', '/') }
    return ($full -replace '\\', '/')
}

$bash = @('C:\Program Files\Git\bin\bash.exe', 'C:\Program Files (x86)\Git\bin\bash.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $bash) { throw 'Git for Windows bash was not found (install Git for Windows, or run scripts/ops/backup-all.sh in WSL/Linux directly).' }

New-Item -ItemType Directory -Force $BackupDir | Out-Null
$tmp = $null
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

    $env:BACKUP_PASSPHRASE_FILE = ConvertTo-GitPath $passPath
    $env:BACKUP_DIR = ConvertTo-GitPath $BackupDir
    $env:BACKUP_NAME_PREFIX = $NamePrefix
    $env:PG_CONTAINER = $PgContainer
    if ($ObjectVolume) { $env:OBJECT_VOLUME = $ObjectVolume }
    $copy = @($CopyDirs | Where-Object { $_ -and (Test-Path (Split-Path $_ -Qualifier -ErrorAction SilentlyContinue)) } | ForEach-Object { ConvertTo-GitPath $_ })
    if ($CopyDirs.Count -gt 0 -and $copy.Count -ne @($CopyDirs | Where-Object { $_ }).Count) { Write-Warning 'a copy destination drive is not available; that destination is skipped and the run reports it' }
    $env:BACKUP_COPY_DIRS = ($copy -join ';')
    if ($IncludeConfig) {
        $cfg = New-Object System.Collections.Generic.List[string]
        foreach ($f in '.env', '.tunnel.env') { if (Test-Path (Join-Path $repo $f)) { $cfg.Add((ConvertTo-GitPath (Join-Path $repo $f))) } }
        $envFile = Join-Path $repo '.env'
        $line = if (Test-Path $envFile) { Get-Content $envFile | Where-Object { $_ -match '^SECRETS_DIR=' } | Select-Object -First 1 }
        if ($line) { $dir = ($line -split '=', 2)[1].Trim(); if ($dir -and (Test-Path $dir)) { $cfg.Add((ConvertTo-GitPath $dir)) } }
        $env:CONFIG_PATHS = ($cfg -join ';')
    }

    & $bash (ConvertTo-GitPath (Join-Path $repo 'scripts\ops\backup-all.sh'))
    $code = $LASTEXITCODE
}
finally {
    if ($tmp -and (Test-Path $tmp)) {
        try { [IO.File]::WriteAllBytes($tmp, (New-Object byte[] 128)) } catch { }
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
    foreach ($v in 'BACKUP_PASSPHRASE_FILE', 'BACKUP_DIR', 'BACKUP_NAME_PREFIX', 'PG_CONTAINER', 'OBJECT_VOLUME', 'BACKUP_COPY_DIRS', 'CONFIG_PATHS') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
}
exit $code
