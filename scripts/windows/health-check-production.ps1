<#
.SYNOPSIS
  Read-only health check of the local Production stack: containers and restart policies, PostgreSQL, application, object store, Hangfire, Microsoft Graph
  subscription and backup freshness. Prints PASS / WARN / FAIL per check, never prints secrets, changes nothing, sends nothing.
.DESCRIPTION
  Exit code 0 when nothing FAILED (warnings allowed), 1 when at least one check FAILED. Run it by hand or from a scheduled task you approve yourself;
  it configures no notification. It talks to Docker only (docker exec), never to Microsoft or Cloudflare.
.EXAMPLE
  .\scripts\windows\health-check-production.ps1
  .\scripts\windows\health-check-production.ps1 -MaxBackupAgeHours 8 -OffPcDir 'I:\HelpdeskBackups\prod'
#>
param(
    [string]$BackupDir = (Join-Path $env:USERPROFILE 'HelpdeskBackups\prod'),
    [string]$OffPcDir = 'I:\HelpdeskBackups\prod',
    [int]$MaxBackupAgeHours = 26,
    [int]$MaxSyncAgeMinutes = 15,
    [int]$MinSubscriptionHours = 24,
    [int]$MinFreeDiskGb = 10
)
$ErrorActionPreference = 'Continue'
$script:results = New-Object System.Collections.Generic.List[object]
function Add-Check([string]$Name, [ValidateSet('PASS', 'WARN', 'FAIL')][string]$Status, [string]$Detail) {
    $script:results.Add([pscustomobject]@{ Check = $Name; Status = $Status; Detail = $Detail })
}

$pg = 'helpdesk-postgres-prod-1'
$app = 'helpdesk-backend'
$obj = 'helpdesk-objectstore-prod-1'

$script:pgUser = (& docker exec $pg printenv POSTGRES_USER 2>$null)
function Invoke-ProdSql([string]$Sql) {
    $out = $Sql | & docker exec -i $pg psql -U $script:pgUser -d helpdesk_prod -X -At -F '|' -v ON_ERROR_STOP=1 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($out -join ' ') }
    return @($out | Where-Object { $_ -ne '' })
}
function Get-AppHttp([string]$Path) {
    $cmd = "exec 3<>/dev/tcp/127.0.0.1/8080; printf 'GET $Path HTTP/1.0\r\nHost: helpdesk.amb-it.co.uk\r\n\r\n' >&3; head -n1 <&3"
    $line = & docker exec $app bash -c $cmd 2>&1
    if ($LASTEXITCODE -ne 0) { return $null }
    if ("$line" -match 'HTTP/1\.[01] (\d{3})') { return [int]$Matches[1] }
    return $null
}

# ---- Docker engine -----------------------------------------------------------------------------------------------------------------------------------
$null = & docker info --format '{{.ServerVersion}}' 2>&1
if ($LASTEXITCODE -ne 0) {
    Add-Check 'docker engine' 'FAIL' 'Docker is not reachable (Docker Desktop not started?). Run scripts\windows\recover-production.ps1 after it is up.'
    $script:results | Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Host
    exit 1
}
Add-Check 'docker engine' 'PASS' 'reachable'

# ---- containers, restart policy, health --------------------------------------------------------------------------------------------------------------
foreach ($c in @(@($pg, $true), @($app, $true), @($obj, $false))) {
    $name = $c[0]; $hasHealth = $c[1]
    $j = & docker inspect $name --format '{{.State.Status}}|{{.HostConfig.RestartPolicy.Name}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}|{{.RestartCount}}' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $j) { Add-Check "container $name" 'FAIL' 'not found'; continue }
    $state, $policy, $health, $restarts = "$j".Split('|')
    $problems = @()
    if ($state -ne 'running') { $problems += "state=$state" }
    if ($policy -ne 'unless-stopped') { $problems += "restart policy=$policy (expected unless-stopped)" }
    if ($hasHealth -and $health -ne 'healthy') { $problems += "health=$health" }
    if ([int]$restarts -gt 5) { $problems += "restarts=$restarts" }
    if ($problems.Count -eq 0) { Add-Check "container $name" 'PASS' "running, $policy, health=$health, restarts=$restarts" }
    else { Add-Check "container $name" 'FAIL' ($problems -join '; ') }
}
$tunnel = & docker inspect helpdesk-cloudflared-1 --format '{{.State.Status}}' 2>$null
Add-Check 'cloudflare tunnel (informational)' 'PASS' $(if ($tunnel -eq 'running') { 'RUNNING (production is publicly reachable behind Access)' } else { "not running ($tunnel): Production is private" })

# ---- PostgreSQL -------------------------------------------------------------------------------------------------------------------------------------
$null = & docker exec $pg pg_isready -U $script:pgUser -d helpdesk_prod 2>&1
if ($LASTEXITCODE -eq 0) {
    try {
        $r = @(Invoke-ProdSql "select pg_size_pretty(pg_database_size(current_database())), (select count(*) from pg_stat_activity where datname = current_database() and usename <> current_user), (select count(*) from hangfire.job where statename = 'Failed')")
        $size, $sessions, $failedAll = $r[0].Split('|')
        Add-Check 'postgres-prod' 'PASS' "accepting connections, helpdesk_prod $size, application sessions $sessions"
    } catch { Add-Check 'postgres-prod' 'FAIL' "connected but query failed: $($_.Exception.Message)" }
} else { Add-Check 'postgres-prod' 'FAIL' 'not accepting connections' }

# ---- application ------------------------------------------------------------------------------------------------------------------------------------
$live = Get-AppHttp '/health/live'; $ready = Get-AppHttp '/health/ready'
Add-Check 'app /health/live' $(if ($live -eq 200) { 'PASS' } else { 'FAIL' }) "HTTP $live"
Add-Check 'app /health/ready' $(if ($ready -eq 200) { 'PASS' } else { 'FAIL' }) "HTTP $ready (includes PostgreSQL, object store and migration state)"
$devLogin = Get-AppHttp '/api/auth/dev-login?subject=x'
Add-Check 'app dev sign-in disabled' $(if ($devLogin -eq 404) { 'PASS' } else { 'FAIL' }) "dev-login HTTP $devLogin (404 expected)"
$conn = & docker inspect $app --format '{{range .Config.Env}}{{println .}}{{end}}' 2>$null | Where-Object { $_ -like 'Database__ConnectionString=*' }
Add-Check 'app uses postgres-prod' $(if ("$conn" -match 'Host=postgres-prod;') { 'PASS' } else { 'FAIL' }) 'connection host checked (credentials not shown)'

# ---- object store -----------------------------------------------------------------------------------------------------------------------------------
$tcp = & docker exec $app bash -c 'exec 3<>/dev/tcp/objectstore-prod/8333 && echo open' 2>&1
Add-Check 'object store S3 port' $(if ("$tcp" -match 'open') { 'PASS' } else { 'FAIL' }) $(if ("$tcp" -match 'open') { 'reachable from the application network' } else { 'not reachable from the application container' })

# ---- Hangfire ---------------------------------------------------------------------------------------------------------------------------------------
try {
    $rows = @(Invoke-ProdSql @"
select substr(key, 15), (extract(epoch from now()) * 1000 - max(case when field = 'NextExecution' then value::bigint end)) / 1000,
       (max(case when field = 'NextExecution' then value::bigint end) - max(case when field = 'LastExecution' then value::bigint end)) / 1000
from hangfire.hash where key like 'recurring-job:%' and field in ('NextExecution', 'LastExecution') group by key order by key
"@)
    if ($rows.Count -eq 0) { Add-Check 'hangfire recurring jobs' 'FAIL' 'no recurring jobs registered' }
    else {
        $late = @()
        foreach ($row in $rows) {
            $n, $overdue, $interval = $row.Split('|')
            $allowed = [math]::Max(180, 2 * [double]$interval)
            if ([double]$overdue -gt $allowed) { $late += ("{0} overdue {1:n0}s" -f $n, [double]$overdue) }
        }
        if ($late.Count -eq 0) { Add-Check 'hangfire recurring jobs' 'PASS' "$($rows.Count) jobs on schedule" } else { Add-Check 'hangfire recurring jobs' 'FAIL' ($late -join '; ') }
    }
    $f = @(Invoke-ProdSql "select count(*) filter (where statename = 'Failed' and createdat > now() - interval '60 minutes'), count(*) filter (where statename = 'Succeeded' and createdat > now() - interval '10 minutes') from hangfire.job")
    $failed, $ok = $f[0].Split('|')
    Add-Check 'hangfire failures (60 min)' $(if ([int]$failed -eq 0) { 'PASS' } else { 'FAIL' }) "$failed failed"
    Add-Check 'hangfire activity (10 min)' $(if ([int]$ok -gt 0) { 'PASS' } else { 'FAIL' }) "$ok succeeded"
} catch { Add-Check 'hangfire' 'FAIL' "query failed: $($_.Exception.Message)" }

# ---- Microsoft Graph subscription (database state only; no call to Microsoft) -----------------------------------------------------------------------
try {
    $rows = @(Invoke-ProdSql "select m.is_active, s.subscription_state, extract(epoch from (s.subscription_expires_at - now())) / 3600, extract(epoch from (now() - s.last_sync_at)) / 60, s.consecutive_failures, coalesce(s.last_sync_error, '') from mail.mailboxes m left join mail.mailbox_sync s on s.mailbox_id = m.id")
    $i = 0
    foreach ($row in $rows) {
        $i++
        $act, $state, $hours, $mins, $fails, $err = $row.Split('|')
        if ($act -ne 't') { Add-Check "mailbox $i" 'PASS' 'inactive (not monitored)'; continue }
        $p = @()
        if ($state -ne 'Active') { $p += "subscription=$state" }
        if ($hours -eq '' -or [double]$hours -lt $MinSubscriptionHours) { $p += "expires in $([math]::Round([double]('0' + $hours), 1)) h" }
        if ($mins -eq '' -or [double]$mins -gt $MaxSyncAgeMinutes) { $p += "last sync $([math]::Round([double]('0' + $mins), 0)) min ago" }
        if ([int]$fails -gt 0) { $p += "consecutive failures=$fails" }
        if ($err) { $p += "last error: $err" }
        if ($p.Count -eq 0) { Add-Check "mailbox $i subscription" 'PASS' ("Active, expires in {0:n0} h, last sync {1:n0} min ago" -f [double]$hours, [double]$mins) }
        else { Add-Check "mailbox $i subscription" 'FAIL' ($p -join '; ') }
    }
    if ($i -eq 0) { Add-Check 'mailboxes' 'WARN' 'no mailbox configured' }
} catch { Add-Check 'graph subscription' 'FAIL' "query failed: $($_.Exception.Message)" }

# ---- backup freshness -------------------------------------------------------------------------------------------------------------------------------
$newest = Get-ChildItem -LiteralPath $BackupDir -Filter 'helpdesk-prod-*.dump.enc' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $newest) { Add-Check 'backup (local)' 'FAIL' "no helpdesk-prod-*.dump.enc in $BackupDir" }
else {
    $age = [math]::Round(((Get-Date).ToUniversalTime() - $newest.LastWriteTimeUtc).TotalHours, 1)
    $side = "$($newest.FullName).sha256"
    $hashOk = (Test-Path $side) -and ((Get-FileHash $newest.FullName -Algorithm SHA256).Hash -ieq ((Get-Content $side -Raw) -split '\s+')[0])
    $st = if (-not $hashOk) { 'FAIL' } elseif ($age -gt $MaxBackupAgeHours) { 'FAIL' } else { 'PASS' }
    Add-Check 'backup (local)' $st "$($newest.Name): $age h old, checksum $(if ($hashOk) { 'matches' } else { 'MISSING OR WRONG' })"
    $copy = Join-Path $OffPcDir $newest.Name
    if (-not (Test-Path -LiteralPath $OffPcDir)) { Add-Check 'backup (second medium)' 'FAIL' "$OffPcDir not available" }
    elseif (-not (Test-Path -LiteralPath $copy)) { Add-Check 'backup (second medium)' 'FAIL' 'latest backup not copied' }
    else {
        $same = (Get-FileHash $copy -Algorithm SHA256).Hash -ieq (Get-FileHash $newest.FullName -Algorithm SHA256).Hash
        Add-Check 'backup (second medium)' $(if ($same) { 'PASS' } else { 'FAIL' }) $(if ($same) { 'copy present, hash equal' } else { 'copy differs' })
    }
    Add-Check 'backup restore test' 'WARN' 'not automated: this script checks freshness and integrity only; restore tests are run by hand'
}

# ---- disk -------------------------------------------------------------------------------------------------------------------------------------------
$drive = (Get-Item $BackupDir -ErrorAction SilentlyContinue).PSDrive
if ($drive) {
    $free = [math]::Round($drive.Free / 1GB, 1)
    Add-Check "disk $($drive.Name):" $(if ($free -lt $MinFreeDiskGb) { 'FAIL' } else { 'PASS' }) "$free GB free"
}

$script:results | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
$fail = @($script:results | Where-Object { $_.Status -eq 'FAIL' }).Count
$warn = @($script:results | Where-Object { $_.Status -eq 'WARN' }).Count
Write-Host ("Summary: {0} checks, {1} FAIL, {2} WARN ({3:u})" -f $script:results.Count, $fail, $warn, (Get-Date).ToUniversalTime())
if ($fail -gt 0) { exit 1 } else { exit 0 }
