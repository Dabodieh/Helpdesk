# Shared helpers for the Windows Docker Desktop tooling (scripts/windows/helpdesk.ps1). Works in Windows PowerShell 5.1 and PowerShell 7.
# The Windows deployment runs the production compose file (docker/production/compose.yml) under its OWN compose project, ports, subnet and volumes,
# so it can sit beside other Docker applications (for example the SAMH Asset Management Suite). Nothing here deletes Docker volumes.
Set-StrictMode -Version 2.0

$script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$script:ComposeFile = Join-Path $script:Root 'docker\production\compose.yml'
$script:ObjectStoreFile = Join-Path $script:Root 'docker\windows\compose.objectstore.yml'
$script:NoEgressFile = Join-Path $script:Root 'docker\windows\compose.no-egress.yml'
$script:EnvFile = if ($env:HELPDESK_ENV_FILE) { $env:HELPDESK_ENV_FILE } else { Join-Path $script:Root 'docker\windows\.env' }
$script:ReservedProjects = @('helpdesk')   # the local development stack's project name: its volumes hold development data

function Stop-Helpdesk([string]$Message) {
    throw $Message   # printed once by the command router (helpdesk.ps1)
}

function Read-EnvFile {
    if (-not (Test-Path -LiteralPath $script:EnvFile)) { Stop-Helpdesk "missing $script:EnvFile (copy docker\windows\.env.example and fill it in)" }
    $map = @{}
    foreach ($line in Get-Content -LiteralPath $script:EnvFile) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        $i = $trimmed.IndexOf('=')
        if ($i -lt 1) { continue }
        $map[$trimmed.Substring(0, $i).Trim()] = $trimmed.Substring($i + 1).Trim()
    }
    return $map
}

function Get-Setting([hashtable]$Settings, [string]$Name, [string]$Default = '') {
    if ($Settings.ContainsKey($Name) -and $Settings[$Name] -ne '') { return $Settings[$Name] }
    return $Default
}

function Get-Project([hashtable]$Settings) {
    $project = Get-Setting $Settings 'COMPOSE_PROJECT_NAME'
    if ($project -eq '') { Stop-Helpdesk 'COMPOSE_PROJECT_NAME is not set in the environment file' }
    if ($script:ReservedProjects -contains $project) { Stop-Helpdesk "COMPOSE_PROJECT_NAME '$project' is reserved for the local development stack; use another name such as helpdesk-win" }
    if ($project -notmatch '^[a-z0-9][a-z0-9_-]*$') { Stop-Helpdesk "COMPOSE_PROJECT_NAME '$project' must be lower-case letters, digits, '-' or '_'" }
    return $project
}

# The compose files for this deployment: the production file, plus the private object store add-on when LOCAL_OBJECT_STORE=true.
function Get-ComposeBaseArgs([string]$Project, [hashtable]$Settings) {
    $a = @('compose', '-p', $Project, '-f', $script:ComposeFile)
    if ((Get-Setting $Settings 'LOCAL_OBJECT_STORE') -eq 'true') { $a += @('-f', $script:ObjectStoreFile) }
    if ((Get-Setting $Settings 'NO_EGRESS') -eq 'true') { $a += @('-f', $script:NoEgressFile) }
    return $a + @('--env-file', $script:EnvFile)
}

# Runs docker compose for this deployment. Refuses anything that would delete volumes or images.
function Invoke-Compose {
    param([string[]]$ComposeArgs, [switch]$Quiet)
    $settings = Read-EnvFile
    $project = Get-Project $settings
    foreach ($a in $ComposeArgs) {
        if ($a -in @('-v', '--volumes', '--rmi', 'prune')) { Stop-Helpdesk "refusing '$a': this tooling never deletes volumes (database, Data Protection keys)" }
    }
    $env:COMPOSE_PROJECT_NAME = $project
    $all = (Get-ComposeBaseArgs $project $settings) + $ComposeArgs
    if ($Quiet) { $out = & docker @all 2>&1; $global:LASTEXITCODE | Out-Null; return $out }
    & docker @all
    if ($LASTEXITCODE -ne 0) { Stop-Helpdesk "docker compose $($ComposeArgs -join ' ') failed (exit $LASTEXITCODE)" }
}

function Invoke-ComposeText([string[]]$ComposeArgs) {
    $settings = Read-EnvFile
    $project = Get-Project $settings
    $env:COMPOSE_PROJECT_NAME = $project
    $all = (Get-ComposeBaseArgs $project $settings) + $ComposeArgs
    # docker writes progress and notices to stderr; under -ErrorAction Stop that would be raised as an error, so read both streams as plain text.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & docker @all 2>&1; $code = $LASTEXITCODE } finally { $ErrorActionPreference = $previous }
    return @{ ExitCode = $code; Output = (($out | ForEach-Object { "$_" }) -join "`n").Trim() }
}

function Invoke-Psql([string]$Database, [string]$Sql) {
    $r = Invoke-ComposeText @('exec', '-T', 'postgres', 'psql', '-U', 'helpdesk', '-d', $Database, '-tAc', $Sql)
    if ($r.ExitCode -ne 0) { Stop-Helpdesk "psql failed: $($r.Output)" }
    return $r.Output
}

# --- IPv4 helpers for the coexistence checks ---
function ConvertTo-CidrRange([string]$Cidr) {
    $parts = $Cidr.Split('/')
    if ($parts.Count -ne 2) { return $null }
    $bytes = ([System.Net.IPAddress]::Parse($parts[0])).GetAddressBytes()
    if ($bytes.Length -ne 4) { return $null }
    [uint64]$ip = 0
    foreach ($b in $bytes) { $ip = $ip * 256 + $b }
    $bits = [int]$parts[1]
    [uint64]$size = [math]::Pow(2, 32 - $bits)
    [uint64]$start = $ip - ($ip % $size)
    return @{ Start = $start; End = $start + $size - 1 }
}

function Test-CidrOverlap([string]$A, [string]$B) {
    $x = ConvertTo-CidrRange $A; $y = ConvertTo-CidrRange $B
    if ($null -eq $x -or $null -eq $y) { return $false }
    return ($x.Start -le $y.End) -and ($y.Start -le $x.End)
}

function Get-DockerSubnets([string]$ExceptProject) {
    $ids = & docker network ls -q 2>$null
    if (-not $ids) { return @() }
    $found = @()
    foreach ($id in $ids) {
        $json = & docker network inspect $id 2>$null | Out-String | ConvertFrom-Json
        foreach ($n in $json) {
            $label = if ($n.Labels) { $n.Labels.PSObject.Properties['com.docker.compose.project'] } else { $null }
            if ($label -and $label.Value -eq $ExceptProject) { continue }
            foreach ($c in @($n.IPAM.Config)) { if ($c -and $c.Subnet -and $c.Subnet -match '^\d+\.\d+\.\d+\.\d+/\d+$') { $found += [pscustomobject]@{ Network = $n.Name; Subnet = $c.Subnet } } }
        }
    }
    return $found
}

function Test-PortFree([int]$Port) {
    $listening = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
    return -not $listening
}

# Validates the environment file, the TLS files, the project name and coexistence with whatever else Docker already runs. Prints problem NAMES, never values.
function Test-HelpdeskConfig([switch]$ForRunning) {
    $s = Read-EnvFile
    $project = Get-Project $s
    $problems = @()
    foreach ($k in 'PUBLIC_HOST', 'POSTGRES_PASSWORD', 'ENTRA_TENANT_ID', 'ENTRA_CLIENT_ID', 'ENTRA_CLIENT_SECRET', 'BOOTSTRAP_PLATFORM_ADMIN_SUBJECT',
                   'GRAPH_TENANT_ID', 'GRAPH_CLIENT_ID', 'GRAPH_CLIENT_SECRET', 'S3_ENDPOINT', 'S3_BUCKET', 'S3_ACCESS_KEY', 'S3_SECRET_KEY',
                   'INTERNAL_SUBNET', 'PROXY_IP', 'HTTP_PORT', 'HTTPS_PORT', 'TLS_DIR', 'BACKUP_DIR') {
        if ((Get-Setting $s $k) -eq '') { $problems += "MISSING  $k" }
    }
    $bind = Get-Setting $s 'BIND_ADDRESS'
    $ip = $null
    if ($bind -ne '' -and -not [System.Net.IPAddress]::TryParse($bind, [ref]$ip)) { $problems += 'INVALID  BIND_ADDRESS must be an IP address (127.0.0.1 keeps the proxy private to this PC)' }
    foreach ($k in 'ENTRA_TENANT_ID', 'ENTRA_CLIENT_ID', 'GRAPH_TENANT_ID', 'GRAPH_CLIENT_ID') {
        $v = Get-Setting $s $k
        $g = [guid]::Empty
        if ($v -ne '' -and -not [guid]::TryParse($v, [ref]$g)) { $problems += "INVALID  $k must be a GUID (the application refuses to start otherwise)" }
    }
    if ((Get-Setting $s 'PUBLIC_HOST') -match 'example\.(org|com)') { $problems += 'PLACEHOLDER  PUBLIC_HOST still holds the example value' }
    if ((Get-Setting $s 'LOCAL_OBJECT_STORE') -eq 'true' -and (Get-Setting $s 'S3_ENDPOINT') -ne 'http://objectstore:8333') { $problems += 'MISMATCH  with LOCAL_OBJECT_STORE=true, S3_ENDPOINT must be http://objectstore:8333' }
    if ((Get-Setting $s 'S3_BUCKET') -eq 'helpdesk') { $problems += "UNSAFE  S3_BUCKET must be a bucket used by this deployment only, not the default 'helpdesk'" }
    $suffix = Get-Setting $s 'PUBLIC_PORT_SUFFIX'
    $https = Get-Setting $s 'HTTPS_PORT' '443'
    $expectedSuffix = if ($https -eq '443') { '' } else { ":$https" }
    if ($suffix -ne $expectedSuffix) { $problems += "MISMATCH  PUBLIC_PORT_SUFFIX must be '$expectedSuffix' for HTTPS_PORT=$https" }
    $tls = Get-Setting $s 'TLS_DIR'
    if ($tls -ne '' -and -not [System.IO.Path]::IsPathRooted($tls)) { $tls = Join-Path (Split-Path $script:ComposeFile) $tls }
    foreach ($f in 'fullchain.pem', 'privkey.pem') { if ($tls -ne '' -and -not (Test-Path -LiteralPath (Join-Path $tls $f))) { $problems += "MISSING  TLS file $f in TLS_DIR (run: helpdesk.ps1 new-certificate)" } }
    $backupDir = Get-Setting $s 'BACKUP_DIR'
    if ($backupDir -ne '' -and -not [System.IO.Path]::IsPathRooted($backupDir)) { $problems += 'INVALID  BACKUP_DIR must be an absolute path (ideally a different drive or a network location)' }

    if (-not $ForRunning -and $problems.Count -eq 0) {
        $subnet = Get-Setting $s 'INTERNAL_SUBNET'
        foreach ($existing in Get-DockerSubnets $project) { if (Test-CidrOverlap $subnet $existing.Subnet) { $problems += "CONFLICT  INTERNAL_SUBNET $subnet overlaps Docker network '$($existing.Network)' ($($existing.Subnet)); choose another" } }
        $running = (Invoke-ComposeText @('ps', '-q')).Output
        if (-not $running) {
            foreach ($p in 'HTTP_PORT', 'HTTPS_PORT') { $n = [int](Get-Setting $s $p); if (-not (Test-PortFree $n)) { $problems += "CONFLICT  $p $n is already in use on this PC; choose another" } }
        }
    }
    if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }; Stop-Helpdesk 'configuration is not ready (values are never printed)' }
    Write-Host "configuration ready: project $project, host $(Get-Setting $s 'PUBLIC_HOST'), HTTPS port $https" -ForegroundColor Green
}

# --- LAN certificate (no public domain needed): a self-signed certificate written as PEM files nginx can read ---
function ConvertTo-DerLength([int]$Length) {
    if ($Length -lt 128) { return [byte[]]@($Length) }
    $b = [System.Collections.Generic.List[byte]]::new()
    $v = $Length
    while ($v -gt 0) { $b.Insert(0, [byte]($v -band 0xFF)); $v = $v -shr 8 }
    return [byte[]](@([byte](0x80 + $b.Count)) + $b.ToArray())
}

function ConvertTo-DerInteger([byte[]]$Value) {
    $i = 0
    while ($i -lt $Value.Length - 1 -and $Value[$i] -eq 0) { $i++ }
    $v = $Value[$i..($Value.Length - 1)]
    if ($v[0] -band 0x80) { $v = @([byte]0) + $v }
    return [byte[]](@([byte]0x02) + (ConvertTo-DerLength $v.Length) + $v)
}

function ConvertTo-Pem([byte[]]$Der, [string]$Label) {
    $b64 = [Convert]::ToBase64String($Der)
    $lines = for ($i = 0; $i -lt $b64.Length; $i += 64) { $b64.Substring($i, [math]::Min(64, $b64.Length - $i)) }
    return "-----BEGIN $Label-----`n" + ($lines -join "`n") + "`n-----END $Label-----`n"
}

function New-LanCertificate([string]$HostName, [string]$OutDir, [int]$Days = 825) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    foreach ($f in 'fullchain.pem', 'privkey.pem') { if (Test-Path (Join-Path $OutDir $f)) { Stop-Helpdesk "$f already exists in $OutDir; remove it deliberately first (it may be a real certificate)" } }
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest("CN=$HostName", $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san = New-Object System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder
    $san.AddDnsName($HostName)
    if ($HostName -ne 'localhost') { $san.AddDnsName('localhost') }
    $req.CertificateExtensions.Add($san.Build())
    $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $true)))
    $cert = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays($Days))
    $p = $rsa.ExportParameters($true)
    $body = ConvertTo-DerInteger ([byte[]]@(0))
    foreach ($part in $p.Modulus, $p.Exponent, $p.D, $p.P, $p.Q, $p.DP, $p.DQ, $p.InverseQ) { $body += ConvertTo-DerInteger $part }
    $der = [byte[]](@([byte]0x30) + (ConvertTo-DerLength $body.Length) + $body)
    [System.IO.File]::WriteAllText((Join-Path $OutDir 'privkey.pem'), (ConvertTo-Pem $der 'RSA PRIVATE KEY'), [System.Text.Encoding]::ASCII)
    [System.IO.File]::WriteAllText((Join-Path $OutDir 'fullchain.pem'), (ConvertTo-Pem $cert.RawData 'CERTIFICATE'), [System.Text.Encoding]::ASCII)
    Write-Host "self-signed certificate for $HostName written to $OutDir (valid $Days days, thumbprint $($cert.Thumbprint))."
    Write-Host 'Browsers will warn until you trust fullchain.pem on the PCs that use Helpdesk (import it into Trusted Root Certification Authorities) or replace it with one from your own CA.'
}
