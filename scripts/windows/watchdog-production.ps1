<#
.SYNOPSIS
  Independent watchdog for the Windows Docker Desktop Production stack: runs read-only checks with explicit time limits, sends deduplicated alerts through an HTTPS
  webhook (ntfy-compatible or JSON), sends recovery notices, and pings an optional dead-man heartbeat (Healthchecks-compatible). Meant for Task Scheduler every 5 minutes.
.DESCRIPTION
  Checks: Docker engine (also "restarted and nobody signed in"), database / application / object-store containers, /health/ready, inbound mail check age, verified-backup
  freshness, required off-host backup copies, backup disk space, credential expiry (30/14/7 days) and secret-file permissions.
  An alert fires after the failure repeats for MONITOR_CONFIRM_RUNS consecutive runs (default 2 = within 10 minutes at a 5-minute schedule), is repeated only as a reminder
  or when its level changes, and is followed by exactly one recovery notice. A notification that cannot be delivered is retried on the next run.
  The heartbeat is sent at the end of every completed run, so a missing ping means the watchdog or the PC is down. No secret, URL or customer content is logged or sent.
  Configuration: a KEY=VALUE file (docker\windows\monitor.env.example). Exit code: 0 run completed; 2 configuration invalid (no heartbeat is sent then).
.EXAMPLE
  .\scripts\windows\watchdog-production.ps1 -ConfigFile C:\ProgramData\Helpdesk\monitor.env -DryRun
  .\scripts\windows\watchdog-production.ps1 -ConfigFile C:\ProgramData\Helpdesk\monitor.env -TestAlert
#>
param(
    [Parameter(Mandatory)][string]$ConfigFile,
    [switch]$DryRun,
    [switch]$TestAlert
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Monitoring.ps1')
. (Join-Path $PSScriptRoot 'lib\WatchdogChecks.ps1')

$cfg = Read-MonitorConfig $ConfigFile
$S = Get-MonitorSettings $cfg
$problems = @(Test-MonitorSettings $S)
if (-not $DryRun -and -not $S.AlertUrl) { $problems += 'ALERT_WEBHOOK_URL is required (without an alert channel the watchdog cannot tell anyone)' }
if ($problems.Count -gt 0) {
    Write-MonitorLog $S.StateDir ('configuration invalid: ' + ($problems -join '; '))
    Write-Host ('configuration invalid: ' + ($problems -join '; '))
    exit 2
}

if ($TestAlert) {
    if (-not $S.AlertUrl) { Write-Host 'ALERT_WEBHOOK_URL is not configured'; exit 2 }
    $evt = [pscustomobject]@{ Type = 'FIRING'; Key = 'test'; Name = 'Test notification'; Detail = 'This is a test of the Helpdesk alert channel. No action is needed.'; Level = ''; Since = $null }
    $ok = Send-WebhookAlert (New-AlertMessage $evt) $S.AlertUrl $S.AlertFormat $S.AlertTokenFile 15 $S.AllowLoopbackHttp
    Write-Host $(if ($ok) { 'test notification delivered' } else { 'test notification NOT delivered' })
    exit $(if ($ok) { 0 } else { 1 })
}

# One run at a time (a slow run must not overlap the next one).
$createdNew = $false
$mutex = New-Object System.Threading.Mutex($false, 'Global\HelpdeskProductionWatchdog', [ref]$createdNew)
if (-not $mutex.WaitOne(0)) { Write-MonitorLog $S.StateDir 'previous run still active; skipped'; exit 0 }
try {
    $secretFiles = @($ConfigFile, $S.PassphraseFile, $S.AlertTokenFile) + @($S.CertFiles | ForEach-Object { ($_ -split '\|')[1] })
    $now = [datetime]::UtcNow
    $results = @(Invoke-WatchdogChecks $S $secretFiles $now)

    if ($DryRun) {
        $results | ForEach-Object { '{0,-5} {1,-28} {2}' -f $_.Status, $_.Name, (Protect-LogText $_.Detail 200) } | Write-Host
        exit 0
    }

    $state = Read-AlertState $S.StateDir
    $events = @(Update-AlertState $state $results $now $S.ConfirmRuns $S.ReminderMinutes)
    $delivered = 0; $failed = 0
    foreach ($e in $events) {
        if (-not $S.AlertUrl) { Undo-AlertEvent $state $e; $failed++; continue }
        $sent = Send-WebhookAlert (New-AlertMessage $e) $S.AlertUrl $S.AlertFormat $S.AlertTokenFile 15 $S.AllowLoopbackHttp
        if ($sent) { $delivered++; Write-MonitorLog $S.StateDir "alert $($e.Type) $($e.Key) delivered" }
        else { $failed++; Undo-AlertEvent $state $e; Write-MonitorLog $S.StateDir "alert $($e.Type) $($e.Key) NOT delivered; will retry" }
    }
    Save-AlertState $S.StateDir $state

    $bad = @($results | Where-Object { $_.Status -eq 'FAIL' })
    Write-MonitorLog $S.StateDir ("run complete: {0} checks, {1} failing, {2} notification(s) sent, {3} not delivered" -f $results.Count, $bad.Count, $delivered, $failed)
    if ($S.HeartbeatUrl) {
        if (-not (Send-Heartbeat $S.HeartbeatUrl 10 $S.AllowLoopbackHttp)) { Write-MonitorLog $S.StateDir 'heartbeat NOT delivered' }
    }
    exit 0
}
finally { try { $mutex.ReleaseMutex() } catch { }; $mutex.Dispose() }
