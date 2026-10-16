# Run: Invoke-Pester tests/Helpdesk.Scripts.Tests/Watchdog.Tests.ps1   (Pester 3.4; Windows PowerShell 5.1)
# End to end: the real watchdog-production.ps1 runs as its own process against a fake docker.exe (compiled here, controlled by FAKE_DOCKER_MODE) and a loopback
# HTTP listener that plays the alert service and the heartbeat service. Nothing touches Docker, the network or real credentials.
$script = (Resolve-Path (Join-Path $PSScriptRoot '..\..\scripts\windows\watchdog-production.ps1')).Path

Describe 'watchdog-production.ps1 end to end' {
$fakeDir = Join-Path $TestDrive 'fakebin'
New-Item -ItemType Directory -Force -Path $fakeDir | Out-Null
Add-Type -OutputAssembly (Join-Path $fakeDir 'docker.exe') -OutputType ConsoleApplication -TypeDefinition @'
using System;
public static class FakeDocker {
  public static int Main(string[] a) {
    string mode = Environment.GetEnvironmentVariable("FAKE_DOCKER_MODE") ?? "up";
    string joined = string.Join(" ", a);
    if (mode == "down") { return 1; }
    if (a.Length > 0 && a[0] == "info") { Console.WriteLine("29.0.0"); return 0; }
    if (a.Length > 0 && a[0] == "inspect") { Console.WriteLine("running|healthy"); return 0; }
    if (joined.Contains("pg_isready")) { return 0; }
    if (joined.Contains("psql")) { Console.In.ReadToEnd(); Console.WriteLine("t|1.0"); return 0; }
    if (joined.Contains("bash")) { Console.In.ReadToEnd(); Console.WriteLine("HTTP/1.0 200 OK"); return 0; }
    return 1;
  }
}
'@

# A recent verified backup, copied to the "NAS", so only the Docker state varies between runs.
$backups = Join-Path $TestDrive 'backups'; $nas = Join-Path $TestDrive 'nas'; $state = Join-Path $TestDrive 'state'
New-Item -ItemType Directory -Force -Path $backups, $nas | Out-Null
$name = 'helpdesk-prod-20270301T110000Z.dump.enc'
foreach ($d in $backups, $nas) { Set-Content -LiteralPath (Join-Path $d $name) -Value 'dump'; Set-Content -LiteralPath (Join-Path $d "$name.sha256") -Value 'abc  x' }
function Write-Status { Set-Content -LiteralPath (Join-Path $backups 'last-backup.json') -Value ('{"status":"ok","finishedAt":"' + [datetime]::UtcNow.AddMinutes(-5).ToString('yyyy-MM-ddTHH:mm:ssZ') + '","file":"' + $name + '","verified":true}') }
Write-Status

$port = Get-Random -Minimum 20000 -Maximum 40000
$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$port/")
$listener.Start()
$script:pending = $null
$script:requests = New-Object System.Collections.Generic.List[object]

function New-Config([hashtable]$Extra = @{}) {
    $c = [ordered]@{
        ALERT_WEBHOOK_URL = "http://127.0.0.1:$port/topic"; HEARTBEAT_URL = "http://127.0.0.1:$port/hb"; MONITOR_TEST_ALLOW_LOOPBACK_HTTP = '1'
        BACKUP_DIR = $backups; BACKUP_REQUIRED_DIRS = $nas; MONITOR_STATE_DIR = $state; ENTRA_SECRET_EXPIRES = '2099-12-31'
        MONITOR_BOOT_GRACE_MINUTES = '0'; DOCKER_EXE = (Join-Path $fakeDir 'docker.exe'); MONITOR_MIN_FREE_DISK_GB = '1'; MONITOR_COMMAND_TIMEOUT_SECONDS = '10'
    }
    foreach ($k in $Extra.Keys) { if ($null -eq $Extra[$k]) { $c.Remove($k) } else { $c[$k] = $Extra[$k] } }
    $f = Join-Path $TestDrive ('monitor-' + [guid]::NewGuid().ToString('N') + '.env')
    Set-Content -LiteralPath $f -Value (@($c.Keys | ForEach-Object { "$_=$($c[$_])" }))
    return $f
}

# Runs the watchdog and serves the listener until it exits. Returns exit code and the requests received during this run.
function Invoke-Watchdog([string]$Config, [string]$Mode, [string[]]$Extra = @()) {
    $env:FAKE_DOCKER_MODE = $Mode
    $oldPath = $env:PATH
    $env:PATH = "$fakeDir;$oldPath"
    $out = Join-Path $TestDrive 'out.txt'
    try {
        $p = Start-Process powershell.exe -ArgumentList (@('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$script`"", '-ConfigFile', "`"$Config`"") + $Extra) -PassThru -WindowStyle Hidden -RedirectStandardOutput $out
    $null = $p.Handle
    } finally { $env:PATH = $oldPath }
    $before = $script:requests.Count
    $deadline = [datetime]::UtcNow.AddSeconds(120)
    $exitSeen = $null
    while ([datetime]::UtcNow -lt $deadline) {
        if (-not $script:pending) { $script:pending = $listener.BeginGetContext($null, $null) }
        if ($script:pending.AsyncWaitHandle.WaitOne(200)) {
            $ctx = $listener.EndGetContext($script:pending); $script:pending = $null
            $body = (New-Object IO.StreamReader($ctx.Request.InputStream)).ReadToEnd()
            $script:requests.Add([pscustomobject]@{ Path = $ctx.Request.Url.AbsolutePath; Title = $ctx.Request.Headers['Title']; Body = $body })
            $ctx.Response.StatusCode = 200; $ctx.Response.Close()
        }
        if ($p.HasExited) { if (-not $exitSeen) { $exitSeen = [datetime]::UtcNow } elseif (([datetime]::UtcNow - $exitSeen).TotalMilliseconds -gt 600) { break } }
    }
    $p.WaitForExit()
    [pscustomobject]@{ ExitCode = $p.ExitCode; Output = (Get-Content -LiteralPath $out -Raw); Requests = @($script:requests | Select-Object -Skip $before) }
}
function Alerts($r, [string]$Match) { @($r.Requests | Where-Object { $_.Path -eq '/topic' -and $_.Title -match $Match }) }
function Beats($r) { @($r.Requests | Where-Object { $_.Path -eq '/hb' }) }

    $cfg = New-Config

    It 'does not alert on the first failed run (Docker down) but still sends the heartbeat' {
        $r = Invoke-Watchdog $cfg 'down'
        $r.ExitCode | Should Be 0
        @(Alerts $r 'Docker engine').Count | Should Be 0
        @(Beats $r).Count | Should Be 1
    }
    It 'alerts once when the failure repeats (within 10 minutes at a 5-minute schedule)' {
        $r = Invoke-Watchdog $cfg 'down'
        $n = @(Alerts $r 'ALERT: Docker engine').Count
        if ($n -ne 1) { $diag = Invoke-Watchdog $cfg 'down' @('-DryRun'); throw ("expected 1 Docker alert, got $n. Dry run: " + $diag.Output + " Log: " + (Get-Content -LiteralPath (Join-Path $state 'watchdog.log') -Raw)) }
        ($r.Requests | Where-Object { $_.Title -match 'Docker engine' } | Select-Object -First 1).Body | Should Match 'FIRING'
    }
    It 'does not repeat the alert while the failure continues (deduplication)' {
        $r = Invoke-Watchdog $cfg 'down'
        @(Alerts $r 'Docker engine').Count | Should Be 0
        @(Beats $r).Count | Should Be 1
    }
    It 'sends exactly one recovery notice when Docker is back, then stays quiet' {
        $r = Invoke-Watchdog $cfg 'up'
        @(Alerts $r 'recovered: Docker engine').Count | Should Be 1
        $r2 = Invoke-Watchdog $cfg 'up'
        @(Alerts $r2 'Docker engine').Count | Should Be 0
    }
    It 'never puts the alert URL, paths or credentials in the log' {
        $log = Get-Content -LiteralPath (Join-Path $state 'watchdog.log') -Raw
        $log | Should Not Match "127\.0\.0\.1|$port|topic|http"
        $log | Should Match 'run complete'
    }
    It 'alerts at once for a sign-in secret that is about to expire' {
        $soon = New-Config @{ ENTRA_SECRET_EXPIRES = [datetime]::UtcNow.AddDays(5).ToString('yyyy-MM-dd'); MONITOR_STATE_DIR = (Join-Path $TestDrive 'state-expiry') }
        $r = Invoke-Watchdog $soon 'up'
        @(Alerts $r 'Sign-in secret expiry').Count | Should Be 1
    }
    It 'alerts when the backup is older than two hours' {
        Set-Content -LiteralPath (Join-Path $backups 'last-backup.json') -Value ('{"status":"ok","finishedAt":"' + [datetime]::UtcNow.AddHours(-3).ToString('yyyy-MM-ddTHH:mm:ssZ') + '","file":"' + $name + '","verified":true}')
        $c = New-Config @{ MONITOR_STATE_DIR = (Join-Path $TestDrive 'state-backup') }
        $null = Invoke-Watchdog $c 'up'
        $r = Invoke-Watchdog $c 'up'
        @(Alerts $r 'ALERT: Backup').Count | Should Be 1
        Write-Status
    }
    It 'alerts when the required off-host destination is unavailable' {
        $c = New-Config @{ BACKUP_REQUIRED_DIRS = 'Q:\no-such-nas\prod'; MONITOR_STATE_DIR = (Join-Path $TestDrive 'state-nas') }
        $null = Invoke-Watchdog $c 'up'
        $r = Invoke-Watchdog $c 'up'
        @(Alerts $r 'Off-host backup').Count | Should Be 1
    }
    It '-DryRun prints the checks and sends nothing' {
        $r = Invoke-Watchdog (New-Config @{ MONITOR_STATE_DIR = (Join-Path $TestDrive 'state-dry') }) 'down' @('-DryRun')
        $r.Output | Should Match 'Docker engine'
        @($r.Requests).Count | Should Be 0
    }
    It '-TestAlert sends one test notification' {
        $r = Invoke-Watchdog $cfg 'up' @('-TestAlert')
        $r.ExitCode | Should Be 0
        @(Alerts $r 'Test notification').Count | Should Be 1
    }
    It 'refuses to run without an alert channel and sends no heartbeat' {
        $r = Invoke-Watchdog (New-Config @{ ALERT_WEBHOOK_URL = $null; MONITOR_STATE_DIR = (Join-Path $TestDrive 'state-nochannel') }) 'up'
        $r.ExitCode | Should Be 2
        $r.Output | Should Match 'ALERT_WEBHOOK_URL is required'
        @(Beats $r).Count | Should Be 0
    }
    It 'refuses a plain-http alert URL when the test switch is off' {
        $r = Invoke-Watchdog (New-Config @{ MONITOR_TEST_ALLOW_LOOPBACK_HTTP = '0'; MONITOR_STATE_DIR = (Join-Path $TestDrive 'state-http') }) 'up'
        $r.ExitCode | Should Be 2
        @($r.Requests).Count | Should Be 0
    }

    $listener.Stop()
}
