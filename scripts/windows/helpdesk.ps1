<#
.SYNOPSIS
  Helpdesk on Windows Docker Desktop: start, stop, update, verify, back up and restore. Works in Windows PowerShell 5.1 and PowerShell 7.
.DESCRIPTION
  Runs the production compose file under its own compose project (default helpdesk-win), ports, subnet and volumes, so it coexists with other Docker
  applications. Configuration: docker\windows\.env (copy docker\windows\.env.example). Nothing here deletes Docker volumes. Guide: docs\guides\WINDOWS_DOCKER_DESKTOP.md
.EXAMPLE
  .\scripts\windows\helpdesk.ps1 check
  .\scripts\windows\helpdesk.ps1 backup-init
  .\scripts\windows\helpdesk.ps1 restore -BackupFile D:\HelpdeskBackups\helpdesk-20261008T160000Z.dump.enc -PromptPassphrase
#>
param(
    [Parameter(Position = 0)][string]$Command = 'help',
    [string]$BackupFile,
    [string]$Database = 'helpdesk',
    [string]$HostName,
    [string]$TaskName = 'Helpdesk Backup',
    [string]$ResultFile,
    [string]$EnvFile,
    [switch]$PromptPassphrase,
    [switch]$Force,
    [switch]$Keep,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Extra
)
$ErrorActionPreference = 'Stop'
if ($EnvFile) { $env:HELPDESK_ENV_FILE = (Resolve-Path -LiteralPath $EnvFile).Path }
. (Join-Path $PSScriptRoot 'lib\Common.ps1')
. (Join-Path $PSScriptRoot 'lib\Backup.ps1')
. (Join-Path $PSScriptRoot 'lib\Operate.ps1')

function Assert-Docker {
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) { Stop-Helpdesk 'Docker Desktop is not running (start it and wait for "Engine running")' }
}

function Show-Help {
    @'
Helpdesk (Windows Docker Desktop)   scripts\windows\helpdesk.ps1 <command>

Setup        check                 validate docker\windows\.env, TLS files, subnet/port conflicts with other Docker apps
             new-certificate       self-signed LAN certificate for PUBLIC_HOST (-HostName to override)
             backup-init           create the backup passphrase (shown once; stored protected for this user, outside BACKUP_DIR)
             backup-schedule       hourly Task Scheduler job (-TaskName)      backup-unschedule   remove it
Run          build | migrate | start | stop | restart | status | logs [service] | verify
             update                backup, build, migrate, start, verify (check out the wanted commit first)
Backup       backup                encrypted, verified backup now (this is what the scheduled task runs)
             backup-verify -BackupFile <file> [-PromptPassphrase]
             backup-status         is the last backup recent and good?
Recovery     restore -BackupFile <file> [-Database helpdesk] [-PromptPassphrase]   into an EMPTY database; stop the app first
             swap-database -Database <restored name>   rollback: rename the live database aside, restored one becomes helpdesk (app stopped)
             restore-test -BackupFile <file> [-PromptPassphrase] [-Keep]            scratch database, timings vs RTO/RPO, then dropped
'@ | Write-Host
}

try {
switch ($Command.ToLowerInvariant()) {
    'help' { Show-Help }
    'check' { Assert-Docker; Test-HelpdeskConfig }
    'new-certificate' {
        $s = Read-EnvFile
        $name = if ($HostName) { $HostName } else { Get-Setting $s 'PUBLIC_HOST' }
        if (-not $name) { Stop-Helpdesk 'set PUBLIC_HOST in the environment file or pass -HostName' }
        $dir = Get-Setting $s 'TLS_DIR'
        if (-not [System.IO.Path]::IsPathRooted($dir)) { $dir = Join-Path (Split-Path $script:ComposeFile) $dir }
        New-LanCertificate $name $dir
    }
    'config' { Assert-Docker; Test-HelpdeskConfig -ForRunning; Invoke-Compose @('config', '-q'); Write-Host 'compose file valid' -ForegroundColor Green }
    'build' { Assert-Docker; Test-HelpdeskConfig -ForRunning; Invoke-Compose @('build') }
    'migrate' { Assert-Docker; Test-HelpdeskConfig -ForRunning; Invoke-Compose @('run', '--rm', 'migrate') }
    'start' { Assert-Docker; Test-HelpdeskConfig; Invoke-Compose @('up', '-d') }
    'stop' { Assert-Docker; Invoke-Compose @('stop') }
    'restart' { Assert-Docker; Test-HelpdeskConfig -ForRunning; Invoke-Compose @('restart', 'helpdesk', 'nginx') }
    'status' {
        Assert-Docker
        Invoke-Compose @('ps')
        $f = Test-BackupFresh
        Write-Host ("backup: " + $(if ($f.Ok) { "OK, $($f.Reason)" } else { "ATTENTION, $($f.Reason)" }))
    }
    'logs' { Assert-Docker; $svc = if ($Extra -and $Extra.Count -gt 0) { $Extra[0] } else { 'helpdesk' }; Invoke-Compose @('logs', '--tail', '200', $svc) }
    'verify' { Assert-Docker; Invoke-Verify }
    'update' {
        Assert-Docker
        Test-HelpdeskConfig -ForRunning
        $commit = (& git -C $script:Root rev-parse --short HEAD 2>$null)
        $dirty = (& git -C $script:Root status --porcelain 2>$null)
        Write-Host "updating to commit $commit" -ForegroundColor Cyan
        if ($dirty -and -not $Force) { Stop-Helpdesk 'the working tree has uncommitted changes; deploy from a clean checkout (or pass -Force)' }
        Invoke-Compose @('up', '-d', 'postgres')
        Invoke-ScheduledBackup            # verified backup first; a failure stops the update (rollback point)
        Invoke-Compose @('build')
        Invoke-Compose @('run', '--rm', 'migrate')
        Invoke-Compose @('up', '-d')
        Start-Sleep -Seconds 15
        Invoke-Verify
        Write-Host "updated. If something is wrong, roll back with the backup just taken: see 'Rollback' in docs\guides\WINDOWS_DOCKER_DESKTOP.md" -ForegroundColor Cyan
    }
    'backup-init' { Initialize-BackupSecret -Force:$Force }
    'backup' { Assert-Docker; Invoke-ScheduledBackup }
    'backup-status' { $f = Test-BackupFresh; Write-Host $f.Reason; if (-not $f.Ok) { exit 1 } }
    'backup-schedule' { Register-BackupTask $TaskName }
    'backup-unschedule' { Unregister-BackupTask $TaskName }
    'backup-verify' {
        Assert-Docker
        if (-not $BackupFile) { Stop-Helpdesk 'pass -BackupFile <file>' }
        $n = Test-BackupFile $BackupFile (Resolve-Passphrase -Prompt:$PromptPassphrase)
        Write-Host "backup verified: $BackupFile ($n tables with data)" -ForegroundColor Green
    }
    'restore' {
        Assert-Docker
        if (-not $BackupFile) { Stop-Helpdesk 'pass -BackupFile <file>' }
        Invoke-Compose @('up', '-d', 'postgres')
        Restore-DatabaseBackup $BackupFile $Database (Resolve-Passphrase -Prompt:$PromptPassphrase)
        Write-Host "next: helpdesk.ps1 migrate   then   helpdesk.ps1 start   then   helpdesk.ps1 verify" -ForegroundColor Cyan
    }
    'swap-database' { Assert-Docker; Switch-RestoredDatabase $Database }
    'restore-test' {
        Assert-Docker
        if (-not $BackupFile) { Stop-Helpdesk 'pass -BackupFile <file>' }
        Invoke-Compose @('up', '-d', 'postgres')
        $result = if ($ResultFile) { $ResultFile } else { Join-Path (Get-BackupDir) 'restore-test-result.json' }
        Invoke-RestoreTest $BackupFile (Resolve-Passphrase -Prompt:$PromptPassphrase) $result -Keep:$Keep
    }
    default { Show-Help; Stop-Helpdesk "unknown command '$Command'" }
}
} catch {
    Write-Host "helpdesk: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
