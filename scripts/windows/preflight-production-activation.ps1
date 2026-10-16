<#
.SYNOPSIS
  Activation preflight for the Windows Production operations (scheduled backup, watchdog, recovery): checks, for the account it runs as, everything that must be true
  before the scheduled tasks are registered. Read-only unless -WriteTests is given.
.DESCRIPTION
  Checks: account (docker-users, batch-logon right, least privilege), Docker named-pipe access and a precise reason when it fails, Docker CLI and Git Bash, read access to the
  operational scripts and that ordinary users cannot change them, configuration / passphrase / token file permissions, backup folder and required off-host destination
  (with -WriteTests: a marker file is written, read back and removed), SMB client policy, alert and heartbeat configuration (not contacted), verified-recovery evidence for
  off-host pruning, disk space, BitLocker, and (when the folders exist) the installation layout against the ACL templates. Secrets, URLs and file contents are never printed.

  Run it as the service account for results about that account. -OneShotTask does that for you without knowing the password in advance: it registers a temporary task as the
  service account (password asked interactively, never stored), runs this preflight with -WriteTests under that account's own network logon, collects the result and removes
  the task. It needs an elevated session and the explicit -AuthoriseOneShotTask switch, and supports -WhatIf.
  Exit code: 0 no FAIL (warnings allowed), 1 at least one FAIL, 2 configuration unusable.
.EXAMPLE
  .\scripts\windows\preflight-production-activation.ps1 -ConfigFile C:\ProgramData\Helpdesk\config\monitor.env -ServiceUser .\hd-svc
  .\scripts\windows\preflight-production-activation.ps1 -ConfigFile ... -ServiceUser .\hd-svc -OneShotTask -AuthoriseOneShotTask -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$ConfigFile,
    [string]$ServiceUser = '',
    [string]$InstallRoot = '',
    [string]$DataRoot = '',
    [string]$BackupRoot = '',
    [switch]$WriteTests,
    [string]$ResultFile = '',
    [switch]$OneShotTask,
    [switch]$AuthoriseOneShotTask,
    [switch]$Quiet
)
$ErrorActionPreference = 'Stop'
$scriptsRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
foreach ($l in 'Monitoring', 'WatchdogChecks', 'BackupRetention', 'AclTemplates', 'ProductionTasks', 'Preflight') { . (Join-Path $PSScriptRoot "lib\$l.ps1") }

$S = Get-MonitorSettings (Read-MonitorConfig $ConfigFile)
$problems = @(Test-MonitorSettings $S)
if ($problems.Count -gt 0) { Write-Host ('configuration invalid: ' + ($problems -join '; ')); exit 2 }

$layout = $null
if ($InstallRoot -or $DataRoot -or $BackupRoot) {
    $args2 = @{}
    if ($InstallRoot) { $args2.InstallRoot = $InstallRoot }
    if ($DataRoot) { $args2.DataRoot = $DataRoot }
    if ($BackupRoot) { $args2.BackupRoot = $BackupRoot }
    $layout = Get-HelpdeskLayout @args2
}

$results = @()
if ($OneShotTask) {
    if (-not $ServiceUser) { Write-Host '-ServiceUser is required for -OneShotTask'; exit 2 }
    $pw = $null
    if (-not $WhatIfPreference -and $AuthoriseOneShotTask) { $pw = Read-Host -AsSecureString -Prompt "Password of $ServiceUser (used only to register the temporary task; not stored)" }
    $resultPath = if ($ResultFile) { $ResultFile } else { Join-Path $S.StateDir 'preflight-result.json' }
    $results = @(Invoke-OneShotPreflight -RepoRoot $repo -ConfigFile $ConfigFile -ServiceUser $ServiceUser -ServicePassword $pw -ResultFile $resultPath -Authorise:$AuthoriseOneShotTask)
    if ($WhatIfPreference) { Write-Host 'WhatIf: no temporary task was registered and no password was requested.'; exit 0 }
} else {
    $results = @(Invoke-ActivationPreflight -Settings $S -ScriptRoot $scriptsRoot -ServiceUser $ServiceUser -ConfigFile $ConfigFile -Layout $layout -WriteTests ([bool]$WriteTests))
}

if ($ResultFile -and -not $OneShotTask) {
    [IO.File]::WriteAllText($ResultFile, (@($results) | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))
}
if (-not $Quiet) {
    foreach ($r in $results) {
        '{0,-5} {1,-9} {2,-40} {3}' -f $r.Status, $r.Area, $r.Name, (Protect-LogText $r.Detail 260) | Write-Host
        if ($r.Fix -and $r.Status -in 'FAIL', 'WARN') { Write-Host ('        fix: ' + $r.Fix) }
    }
    Write-Host ('{0} checks: {1} FAIL, {2} WARN, {3} PASS, {4} SKIP' -f $results.Count, @($results | Where-Object { $_.Status -eq 'FAIL' }).Count, @($results | Where-Object { $_.Status -eq 'WARN' }).Count, @($results | Where-Object { $_.Status -eq 'PASS' }).Count, @($results | Where-Object { $_.Status -eq 'SKIP' }).Count)
}
exit $(if (@($results | Where-Object { $_.Status -eq 'FAIL' }).Count -gt 0) { 1 } else { 0 })
