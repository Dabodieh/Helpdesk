# Development/Production mode guard for scripts/windows/dev.ps1. Decides from the Docker Compose project and service LABELS and the actual container state,
# never from container names (a container created before a rename keeps its old name). Pure functions: the docker call is isolated in Get-ComposeServiceRows.
Set-StrictMode -Version 2.0

$script:DevelopmentServices = @('host', 'web')
$script:ProductionServices = @('app', 'cloudflared')
$script:ActiveStates = @('running', 'restarting', 'paused')

# One "service|state" row per container of the Compose project (stopped ones included). Fails closed: if Docker cannot answer, the mode is unknown and nothing may start.
function Get-ComposeServiceRows([string]$Project = 'helpdesk') {
    # JSON output needs no embedded quotes, so it behaves the same under Windows PowerShell 5.1 and PowerShell 7 (legacy argument passing strips them).
    $lines = & docker ps -a --filter "label=com.docker.compose.project=$Project" --format '{{json .}}' 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'cannot read the Docker container state; refusing to continue because the running mode is unknown' }
    $rows = foreach ($l in @($lines | Where-Object { $_ })) {
        $c = $l | ConvertFrom-Json
        $service = ($c.Labels -split ',' | Where-Object { $_ -like 'com.docker.compose.service=*' } | Select-Object -First 1)
        if ($service) { '{0}|{1}' -f $service.Substring('com.docker.compose.service='.Length), $c.State }
    }
    return , @($rows | Where-Object { $_ })
}

# Names of the services of one mode that are active in the given rows.
function Get-ActiveServices([string[]]$Rows, [ValidateSet('Development', 'Production')][string]$Mode) {
    $names = if ($Mode -eq 'Development') { $script:DevelopmentServices } else { $script:ProductionServices }
    $found = foreach ($r in $Rows) {
        $p = $r.Split('|')
        if ($p.Count -eq 2 -and $names -contains $p[0] -and $script:ActiveStates -contains $p[1]) { $p[0] }
    }
    return , @($found | Sort-Object -Unique)
}

# Throws when starting $Wanted would put it beside the other mode. Never stops or removes anything.
function Assert-ModeAllowed([string[]]$Rows, [ValidateSet('Development', 'Production')][string]$Wanted) {
    $other = if ($Wanted -eq 'Development') { 'Production' } else { 'Development' }
    $active = Get-ActiveServices $Rows $other
    if ($active.Count -gt 0) {
        $stop = if ($other -eq 'Production') { 'tunnel-stop (if the tunnel runs) and prod-stop' } else { 'stop' }
        throw "$other is running ($($active -join ', ')); Development and Production must not run together. Run dev.ps1 $stop first."
    }
}
