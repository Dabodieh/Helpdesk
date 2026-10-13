<#
.SYNOPSIS
  Isolated local E2E stack for the Playwright suite. Loopback only; never touches the normal dev stack.
.DESCRIPTION
  up      PostgreSQL container helpdesk-e2e-postgres (own volume helpdesk_e2e_pgdata, 127.0.0.1:5434, database helpdesk_e2e),
          backend from current source on 127.0.0.1:5081 (fake mail provider, in-memory object storage, dev sign-in),
          Vite on 127.0.0.1:5174 proxying to 5081. Migrations are applied by the backend on start, to helpdesk_e2e only.
  run     Playwright suite once against 5174 (extra arguments are passed to Playwright).
  status  Which parts are listening.
  down    Stops the three parts. The volume is kept; use `down -DropData` to drop the database volume (E2E data only).
  Throwaway database password is generated once into %LOCALAPPDATA%\Helpdesk-E2E\pg.env (outside the repository).
#>
param(
    [Parameter(Position = 0)][ValidateSet('up', 'run', 'status', 'down')][string]$Command = 'status',
    [switch]$DropData,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Extra
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$state = Join-Path $env:LOCALAPPDATA 'Helpdesk-E2E'
$null = New-Item -ItemType Directory -Force $state
$container = 'helpdesk-e2e-postgres'; $volume = 'helpdesk_e2e_pgdata'
$pgPort = 5434; $apiPort = 5081; $webPort = 5174

function Get-Password {
    $f = Join-Path $state 'pg.env'
    if (-not (Test-Path $f)) {
        $b = New-Object byte[] 18; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
        [IO.File]::WriteAllText($f, [Convert]::ToBase64String($b).Replace('+', 'A').Replace('/', 'B').Replace('=', 'C'))
    }
    (Get-Content $f -Raw).Trim()
}
function Test-Port([int]$p) { [bool](Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue) }
function Stop-Port([int]$p) { Get-NetTCPConnection -State Listen -LocalPort $p -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force } }
function Wait-Url([string]$u, [int]$seconds) {
    for ($i = 0; $i -lt $seconds; $i++) { try { if ((Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { return } } catch { }; Start-Sleep 1 }
    throw "not ready: $u"
}

switch ($Command) {
    'up' {
        $pw = Get-Password
        if (-not (docker ps -a --format '{{.Names}}' | Select-String -SimpleMatch $container)) {
            docker run -d --name $container -p "127.0.0.1:${pgPort}:5432" -v "${volume}:/var/lib/postgresql" `
                -e POSTGRES_USER=helpdesk -e "POSTGRES_PASSWORD=$pw" -e POSTGRES_DB=helpdesk_e2e postgres:18.6 | Out-Null
        } elseif (-not (docker ps --format '{{.Names}}' | Select-String -SimpleMatch $container)) { docker start $container | Out-Null }
        for ($i = 0; $i -lt 60; $i++) { docker exec $container pg_isready -U helpdesk -d helpdesk_e2e 2>$null | Out-Null; if ($LASTEXITCODE -eq 0) { break }; Start-Sleep 1 }
        if (-not (Test-Port $apiPort)) {
            $env:ASPNETCORE_ENVIRONMENT = 'Development'
            $env:Urls = "http://127.0.0.1:$apiPort"
            $env:Database__ConnectionString = "Host=127.0.0.1;Port=$pgPort;Database=helpdesk_e2e;Username=helpdesk;Password=$pw"
            $env:Mail__Provider = 'Fake'; $env:Storage__Provider = 'InMemory'
            Start-Process dotnet -ArgumentList @('run', '--project', 'src/Helpdesk.Host', '--no-launch-profile') -WorkingDirectory $repo -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $state 'host.log') -RedirectStandardError (Join-Path $state 'host.err.log')
            Remove-Item Env:Database__ConnectionString
        }
        Wait-Url "http://127.0.0.1:$apiPort/health/ready" 180
        if (-not (Test-Port $webPort)) {
            $env:HELPDESK_API_PROXY = "http://127.0.0.1:$apiPort"
            Start-Process node -ArgumentList @('node_modules/vite/bin/vite.js', '--host', '127.0.0.1', '--port', $webPort, '--strictPort') `
                -WorkingDirectory (Join-Path $repo 'src\web') -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $state 'vite.log') -RedirectStandardError (Join-Path $state 'vite.err.log')
        }
        Wait-Url "http://127.0.0.1:$webPort/health/ready" 60
        Write-Host "e2e stack ready: web http://127.0.0.1:$webPort  api http://127.0.0.1:$apiPort  db 127.0.0.1:$pgPort/helpdesk_e2e"
    }
    'run' {
        $env:E2E_BASE_URL = "http://127.0.0.1:$webPort"
        if (-not $env:E2E_BROWSER_CHANNEL) { $env:E2E_BROWSER_CHANNEL = 'msedge' }
        npm --prefix (Join-Path $repo 'src\web') run e2e -- @Extra
    }
    'status' {
        "db container: " + ((docker ps --format '{{.Names}}' | Select-String -SimpleMatch $container) -ne $null)
        "api $apiPort : $(Test-Port $apiPort)   web $webPort : $(Test-Port $webPort)"
    }
    'down' {
        Stop-Port $webPort; Stop-Port $apiPort
        docker stop $container 2>$null | Out-Null
        if ($DropData) { docker rm $container | Out-Null; docker volume rm $volume | Out-Null }
    }
}
