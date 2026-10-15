<#
.SYNOPSIS
  Local Helpdesk on Windows Docker Desktop: ONE Compose project ("helpdesk"), Development and Production modes that cannot run together.
.DESCRIPTION
  Development (default):  web (Vite, 127.0.0.1:5173) + host (backend, 127.0.0.1:5080) + postgres (127.0.0.1:5433), all in Docker.
  Production (prod-*):    app (ASPNETCORE_ENVIRONMENT=Production, compiled SPA, Entra, no published ports) + objectstore, using its own
                          database (PROD_DB_NAME, default helpdesk_prod) on the same PostgreSQL server. Reached only through the optional
                          cloudflared tunnel (tunnel-start), which is never started by any other command.
  Nothing here removes containers, volumes or images (no `down`, no prune).
.EXAMPLE
  .\scripts\windows\dev.ps1 start
  .\scripts\windows\dev.ps1 prod-check          # lists missing production settings by NAME only
#>
param(
    [Parameter(Position = 0)][ValidateSet('start', 'stop', 'restart', 'update', 'status', 'logs',
        'prod-check', 'prod-migrate', 'prod-start', 'prod-stop', 'tunnel-start', 'tunnel-stop', 'help')][string]$Command = 'help',
    [Parameter(Position = 1)][ValidateSet('web', 'host', 'app', 'postgres', 'postgres-prod', 'objectstore-prod', 'cloudflared', '')][string]$Service = '',
    [switch]$Force,
    [switch]$ConfirmPublic
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$backupDir = if ($env:HELPDESK_DEV_BACKUP_DIR) { $env:HELPDESK_DEV_BACKUP_DIR } else { 'D:\DB Backup' }
$backendName = 'helpdesk-backend'   # container_name of the Production app (and of a recreated Development host); only used to wait for health
. (Join-Path $PSScriptRoot 'lib\ModeGuard.ps1')

function Invoke-Compose([string[]]$Profiles, [string[]]$ComposeArgs) {
    $p = foreach ($x in $Profiles) { '--profile'; $x }
    & docker compose --project-directory $root -p helpdesk @p @ComposeArgs
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($ComposeArgs -join ' ') failed" }
}
function Dev([string[]]$a) { Invoke-Compose @('development') $a }
function Get-EnvFile {
    $m = @{}
    $f = Join-Path $root '.env'
    if (Test-Path $f) { foreach ($l in Get-Content $f) { if ($l -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $m[$Matches[1]] = $Matches[2].Trim() } } }
    $m
}
function Assert-NoOtherMode([string]$Wanted) { Assert-ModeAllowed (Get-ComposeServiceRows) $Wanted }
function Wait-Healthy([string[]]$Containers) {
    for ($i = 0; $i -lt 60; $i++) {
        $s = @(& docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' @Containers 2>$null)
        if ($s.Count -eq $Containers.Count -and @($s | Where-Object { $_ -notin 'healthy', 'running' }).Count -eq 0) { return }
        Start-Sleep 3
    }
    Write-Warning 'not healthy yet; see: dev.ps1 status'
}
function Show-Status {
    & docker ps -a --filter 'label=com.docker.compose.project=helpdesk' --format 'table {{.Names}}\t{{.Label "com.docker.compose.service"}}\t{{.Status}}\t{{.Ports}}'
    Write-Host 'Development: web http://localhost:5173/  host http://localhost:5080/  postgres 127.0.0.1:5433'
}
function Assert-RecentBackup {
    $f = Get-ChildItem -LiteralPath $backupDir -Filter 'helpdesk-*.dump.enc' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($f -and ((Get-Date) - $f.LastWriteTime).TotalHours -lt 24 -and (Test-Path "$($f.FullName).sha256")) {
        Write-Host "safety check: backup $($f.Name) ($([int]((Get-Date) - $f.LastWriteTime).TotalHours) h old)"; return
    }
    if ($Force) { Write-Warning 'no backup from the last 24 hours; continuing because -Force was given'; return }
    throw "no database backup newer than 24 hours with a .sha256 in $backupDir. Take one first (see docs/guides/BACKUP_AND_RESTORE.md), or pass -Force."
}
# Names of unset production settings (never values). Returns the list; the caller decides.
function Get-ProductionGaps {
    $e = Get-EnvFile
    $gaps = New-Object System.Collections.Generic.List[string]
    foreach ($k in 'PROD_PG_ADMIN_PASSWORD', 'PROD_APP_DB_PASSWORD', 'PROD_S3_ACCESS_KEY', 'PROD_S3_SECRET_KEY', 'ENTRA_TENANT_ID', 'ENTRA_CLIENT_ID', 'ENTRA_CLIENT_SECRET', 'BOOTSTRAP_PLATFORM_ADMIN_SUBJECT',
        'GRAPH_TENANT_ID', 'GRAPH_CLIENT_ID', 'SECRETS_DIR', 'DP_CERT_PATH', 'DP_CERT_PASSWORD_FILE') {
        if (-not $e[$k]) { $gaps.Add($k) }
    }
    if (-not $e['GRAPH_CLIENT_SECRET'] -and -not $e['GRAPH_CERT_PATH']) { $gaps.Add('GRAPH_CERT_PATH (or GRAPH_CLIENT_SECRET)') }
    if ($e['SECRETS_DIR'] -and -not (Test-Path -LiteralPath $e['SECRETS_DIR'])) { $gaps.Add('SECRETS_DIR (folder does not exist)') }
    if ($e['SECRETS_DIR'] -and ($e['SECRETS_DIR'] -like "$root*")) { $gaps.Add('SECRETS_DIR (must be OUTSIDE the repository)') }
    $dev = if ($e['POSTGRES_DB']) { $e['POSTGRES_DB'] } else { 'helpdesk' }
    $prod = if ($e['PROD_DB_NAME']) { $e['PROD_DB_NAME'] } else { 'helpdesk_prod' }
    if ($prod -eq $dev) { $gaps.Add('PROD_DB_NAME (must differ from the development database)') }
    if ($e['PUBLIC_HOST'] -and $e['PUBLIC_HOST'] -ne 'helpdesk.amb-it.co.uk') { $gaps.Add('PUBLIC_HOST (differs from the Access-protected helpdesk.amb-it.co.uk)') }
    , $gaps
}
function Assert-ProductionReady {
    $g = Get-ProductionGaps
    if ($g.Count -gt 0) { throw "production is not configured. Missing/invalid: $($g -join ', '). See .env.example and docs/guides/CLOUDFLARE_PUBLIC_ACCESS.md." }
}

switch ($Command) {
    'start'   { Assert-NoOtherMode 'Development'; Dev @('up', '-d', '--no-build', 'postgres', 'host', 'web'); Wait-Healthy 'helpdesk-web-1', 'helpdesk-postgres-1'; Show-Status }
    'stop'    { Dev @('stop', 'web', 'host', 'postgres') }
    'restart' { Assert-NoOtherMode 'Development'; Dev @('restart', 'postgres', 'host', 'web'); Wait-Healthy 'helpdesk-web-1', 'helpdesk-postgres-1'; Show-Status }
    'update'  {
        # Development backend update: applies pending migrations on start, so a recent backup is mandatory. postgres is never recreated.
        Assert-NoOtherMode 'Development'
        Assert-RecentBackup
        $tag = 'pre-' + (Get-Date -Format 'yyyyMMdd-HHmm')
        & docker image tag helpdesk-host:local "helpdesk-host:$tag" 2>$null
        if ($LASTEXITCODE -eq 0) { Write-Host "rollback image: helpdesk-host:$tag" }
        Dev @('build', 'host')
        Dev @('up', '-d', '--no-deps', 'host', 'web')
        Wait-Healthy 'helpdesk-web-1', 'helpdesk-postgres-1'; Show-Status
    }
    'status'  { Show-Status }
    'logs'    {
        $prof = if ($Service -in 'app', 'postgres-prod', 'objectstore-prod') { 'production' } elseif ($Service -eq 'cloudflared') { @('production', 'tunnel') } else { 'development' }
        if ($Service) { Invoke-Compose $prof @('logs', '--tail', '100', $Service) } else { Dev @('logs', '--tail', '60') }
    }
    'prod-check' {
        $g = Get-ProductionGaps
        if ($g.Count -eq 0) { Write-Host 'production settings present (values not shown)' -ForegroundColor Green }
        else { Write-Host 'production NOT ready. Missing/invalid: ' ($g -join ', ') -ForegroundColor Yellow }
        Invoke-Compose @('production') @('config', '-q'); Write-Host 'compose (production profile) valid'
    }
    'prod-migrate' {
        # Explicit, one-off. Runs the module migrations as the Production administrator role against the Production-only PostgreSQL (postgres-prod, own volume).
        # The database itself is created by that container on first start; the Development database server is never involved.
        # Only what the migration needs (database credentials, secrets folder, key-ring certificate); sign-in and mail values are not required to migrate.
        $g = @(Get-ProductionGaps | Where-Object { $_ -match '^(PROD_PG_ADMIN_PASSWORD|PROD_APP_DB_PASSWORD|SECRETS_DIR|DP_CERT|PROD_DB_NAME|PROD_S3)' })
        if ($g.Count -gt 0) { throw "migration prerequisites missing/invalid: $($g -join ', ')" }
        Invoke-Compose @('production') @('up', '-d', '--no-deps', '--wait', 'postgres-prod')
        Invoke-Compose @('production-migrate') @('run', '--rm', '--no-deps', 'migrate')
    }
    'prod-start' {
        Assert-ProductionReady
        Assert-NoOtherMode 'Production'
        # postgres-prod first (own container and volume), then the application alone: --no-deps never recreates the database.
        Invoke-Compose @('production') @('up', '-d', '--no-build', '--wait', 'postgres-prod')
        Invoke-Compose @('production') @('up', '-d', '--no-build', '--no-deps', 'app')
        Wait-Healthy $backendName
        Show-Status
        Write-Host 'Production app is up with NO published port. Public access needs tunnel-start (separately approved).'
    }
    'prod-stop' { Invoke-Compose @('production') @('stop', 'app', 'objectstore-prod') }
    'tunnel-start' {
        # The only way the tunnel starts. It publishes whatever route the Cloudflare tunnel has, so every precondition is checked here.
        $rows = Get-ComposeServiceRows
        if ((Get-ActiveServices $rows 'Production') -notcontains 'app') { throw 'refusing: the Production app is not running (Development must never be published).' }
        Assert-ModeAllowed $rows 'Production'
        Wait-Healthy $backendName
        if (-not $ConfirmPublic) { throw 'refusing: pass -ConfirmPublic only after Cloudflare Access (allow-list, no bypass) and the tunnel route to http://app:8080 are verified and approved.' }
        Invoke-Compose @('production', 'tunnel') @('up', '-d', 'cloudflared')
    }
    'tunnel-stop' { Invoke-Compose @('production', 'tunnel') @('stop', 'cloudflared') }
    default {
        @'
dev.ps1 <command> [service] [-Force] [-ConfirmPublic]
Development: start | stop | restart | update | status | logs [web|host|postgres]
             update needs a backup < 24 h old (-Force skips); tags the current image as a rollback point, rebuilds host, recreates host and web
Production:  prod-check | prod-migrate | prod-start | prod-stop | logs app
             Production and Development cannot run together (checked from Compose labels and container state); production uses its own database PROD_DB_NAME
Tunnel:      tunnel-start -ConfirmPublic | tunnel-stop   (tunnel-start refuses unless the Production app is running)
'@ | Write-Host
    }
}
