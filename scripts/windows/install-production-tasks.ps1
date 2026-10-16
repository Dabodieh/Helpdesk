<#
.SYNOPSIS
  Registers the three Windows Task Scheduler jobs for Production operations: hourly backup, 5-minute watchdog, and the sign-in recovery task. Use -WhatIf first.
.DESCRIPTION
  Nothing is created without confirmation, and -WhatIf creates and asks for nothing. Before registering it validates the monitor configuration, checks that the scripts
  exist, and warns when the passphrase file is readable by broad groups.

  Identities (prerequisites, documented in docs\guides\WINDOWS_MONITORING_AND_BACKUP.md):
    -ServiceUser  a dedicated local account for backup and watchdog. Needs: "Log on as a batch job", membership of the docker-users group, read access to the passphrase file
                  and the monitor configuration, write access to the backup and monitor folders. Its password is requested interactively and is never stored by these scripts
                  (Windows keeps it for the task). Docker Desktop must be running for the backup and the container checks; the watchdog reports when it is not.
    -DesktopUser  the person whose sign-in starts Docker Desktop. The recovery task runs only in that person's interactive session.
  This script does not enable automatic sign-in, change Windows Update policy, start Docker Desktop, or contact any alert service.
.EXAMPLE
  .\scripts\windows\install-production-tasks.ps1 -ConfigFile C:\ProgramData\Helpdesk\monitor.env -ServiceUser .\hd-svc -DesktopUser DOMAIN\admin -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$ConfigFile,
    [Parameter(Mandatory)][string]$ServiceUser,
    [Parameter(Mandatory)][string]$DesktopUser,
    [switch]$IncludeConfig,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'lib\Monitoring.ps1')
. (Join-Path $PSScriptRoot 'lib\ProductionTasks.ps1')

$ConfigFile = (Resolve-Path -LiteralPath $ConfigFile).Path
$S = Get-MonitorSettings (Read-MonitorConfig $ConfigFile)
$problems = @(Test-MonitorSettings $S)
if (-not $S.AlertUrl) { $problems += 'ALERT_WEBHOOK_URL is required' }
if (-not $S.PassphraseFile) { $problems += 'BACKUP_PASSPHRASE_FILE is required (an ACL-restricted file readable by the service account)' }
elseif (-not (Test-Path -LiteralPath $S.PassphraseFile)) { $problems += 'BACKUP_PASSPHRASE_FILE does not exist (this script never creates it)' }
if ($S.RequiredBackupDirs.Count -eq 0) { $problems += 'BACKUP_REQUIRED_DIRS is required (at least one off-host destination)' }
foreach ($f in 'backup-production.ps1', 'watchdog-production.ps1', 'recover-production.ps1') { if (-not (Test-Path (Join-Path $PSScriptRoot $f))) { $problems += "script missing: $f" } }
if ($problems.Count -gt 0) { throw ('cannot install: ' + ($problems -join '; ')) }
if ($S.PassphraseFile -and (Test-BroadlyReadable $S.PassphraseFile)) { Write-Warning 'the passphrase file is readable by broad groups (Everyone/Users); restrict it to the service account and administrators' }

$defs = Get-ProductionTaskDefinitions -RepoRoot $repo -ConfigFile $ConfigFile -ServiceUser $ServiceUser -DesktopUser $DesktopUser -PassphraseFile $S.PassphraseFile -IncludeConfig:$IncludeConfig
$defs | ForEach-Object { Write-Host ("{0}: {1}; as {2} ({3}); limit {4} min" -f $_.Name, $(if ($_.Trigger -eq 'Repeat') { "every $($_.RepeatMinutes) min" } else { 'at sign-in' }), $_.RunAs, $_.LogonType, $_.TimeLimitMinutes) }

$pw = $null
if (-not $WhatIfPreference -and ($defs | Where-Object { $_.LogonType -eq 'Password' }) -and $PSCmdlet.ShouldContinue("Register the three tasks (backup and watchdog as $ServiceUser)?", 'Helpdesk Production tasks')) {
    $pw = Read-Host -AsSecureString -Prompt "Password of $ServiceUser (used only to register the tasks; not stored by this script)"
}
Register-ProductionTasks -Definitions $defs -ServicePassword $pw -Force:$Force
if ($WhatIfPreference) { Write-Host 'WhatIf: nothing was registered and no password was requested.' } else { Write-Host 'Tasks registered. Run the watchdog once with -DryRun, then -TestAlert, before relying on it.' }
