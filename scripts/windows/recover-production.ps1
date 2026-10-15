<#
.SYNOPSIS
  Brings the local Production stack back after Docker Desktop, WSL or Windows was restarted. Starts, in order: Docker Desktop (if it is not running),
  postgres-prod, objectstore-prod, the application. Then runs the health check.
.DESCRIPTION
  Safe by construction: it only STARTS existing containers (never creates, recreates or removes anything, never touches volumes or databases), refuses while the
  Development application is running, and never starts the Cloudflare tunnel or the Development stack. The tunnel stays an explicit decision:
  dev.ps1 tunnel-start -ConfirmPublic. Use -WhatIf to see what it would do.

  Why order matters: the application fails closed when it cannot read its key ring from PostgreSQL, so it must not start before the database is healthy.
  (Docker's own restart policy also retries the application until the database answers, so a wrong order only costs time, not data.)
.EXAMPLE
  .\scripts\windows\recover-production.ps1
  .\scripts\windows\recover-production.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param([int]$DockerWaitSeconds = 240, [int]$HealthWaitSeconds = 180)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $PSScriptRoot 'lib\ModeGuard.ps1')

function Test-Docker { $null = & docker info --format '{{.ServerVersion}}' 2>&1; return ($LASTEXITCODE -eq 0) }
function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { if (& $Condition) { return $true }; Start-Sleep 3 }
    Write-Warning "timed out waiting for $What"
    return $false
}
function Get-State([string]$Name) {
    $s = & docker inspect $Name --format '{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return "$s"
}
function Start-Existing([string]$Name, [bool]$WaitHealthy) {
    $state = Get-State $Name
    if ($null -eq $state) { throw "container $Name does not exist; create the stack once with: .\scripts\windows\dev.ps1 prod-start" }
    if ($state -like 'running|*' -and (-not $WaitHealthy -or $state -like '*|healthy' -or $state -like '*|none')) { Write-Host "ok       $Name ($state)"; return }
    if ($PSCmdlet.ShouldProcess($Name, 'docker start')) {
        if (-not $state.StartsWith('running')) { & docker start $Name | Out-Null; if ($LASTEXITCODE -ne 0) { throw "docker start $Name failed" } }
        if ($WaitHealthy) {
            if (-not (Wait-Until { (Get-State $Name) -like 'running|healthy' } $HealthWaitSeconds "$Name to be healthy")) { throw "$Name is not healthy; stopping here (nothing else was started)" }
        }
        Write-Host "started  $Name ($(Get-State $Name))"
    }
}

# 1. Docker Desktop
if (-not (Test-Docker)) {
    $exe = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (-not (Test-Path $exe)) { throw 'Docker Desktop is not installed at the default location; start Docker by hand and run this script again' }
    if ($PSCmdlet.ShouldProcess('Docker Desktop', 'start')) {
        Write-Host 'starting Docker Desktop ...'
        Start-Process -FilePath $exe
        if (-not (Wait-Until { Test-Docker } $DockerWaitSeconds 'the Docker engine')) { throw 'Docker did not become ready; check Docker Desktop (it may ask for an update or a licence prompt)' }
    }
}
if (-not (Test-Docker)) { if ($WhatIfPreference) { Write-Host 'WhatIf: Docker is not running; the remaining steps would follow'; exit 0 }; throw 'Docker is not running' }

# 2. never beside Development
$rows = Get-ComposeServiceRows
Assert-ModeAllowed $rows 'Production'

# 3. in dependency order
Start-Existing 'helpdesk-postgres-prod-1' $true
Start-Existing 'helpdesk-objectstore-prod-1' $false
Start-Existing 'helpdesk-backend' $true

Write-Host ''
Write-Host 'Cloudflare tunnel is NOT started by this script (dev.ps1 tunnel-start -ConfirmPublic, with approval).'
if (-not $WhatIfPreference) { & (Join-Path $PSScriptRoot 'health-check-production.ps1'); exit $LASTEXITCODE }
