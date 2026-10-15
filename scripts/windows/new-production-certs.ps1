<#
.SYNOPSIS
  Creates the dedicated PRODUCTION Data Protection and Microsoft Graph certificates, outside the repository. Never overwrites.
.DESCRIPTION
  Writes to -OutDir (default %USERPROFILE%\HelpdeskSecrets\production, ACL: this Windows user only):
    dp.pfx + dp-password            Data Protection key-ring certificate (RSA 3072, 5 years)
    graph.pfx + graph-password      Microsoft Graph client certificate (RSA 3072, 1 year)
    graph-public.cer                public key only: upload this to the Entra "Helpdesk mail" app registration
  The disposable validation certificates in ...\HelpdeskSecrets\m365 are not read, replaced or reused.
  Passwords are random, written only to the password files, and never printed. The certificates are removed from the Windows
  certificate store after export, so the PFX files are the only copies: back them up (docs/guides/CLOUDFLARE_PUBLIC_ACCESS.md, "Certificates").
  Generating persistent production credentials needs the user's approval: nothing is created without -Approved.
.EXAMPLE
  .\scripts\windows\new-production-certs.ps1 -Approved
#>
param(
    [string]$OutDir = (Join-Path $env:USERPROFILE 'HelpdeskSecrets\production'),
    [switch]$Approved
)
$ErrorActionPreference = 'Stop'
if (-not $Approved) { throw 'refusing: this creates persistent production credentials. Re-run with -Approved once you have approved it.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ((Resolve-Path -LiteralPath (Split-Path $OutDir -Parent) -ErrorAction SilentlyContinue).Path -like "$repo*" -or $OutDir -like "$repo*") { throw 'refusing: OutDir must be outside the repository.' }

$names = 'dp.pfx', 'dp-password', 'graph.pfx', 'graph-password', 'graph-public.cer'
$existing = $names | Where-Object { Test-Path -LiteralPath (Join-Path $OutDir $_) }
if ($existing) { throw "refusing to overwrite existing file(s) in ${OutDir}: $($existing -join ', ')" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
& icacls $OutDir /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null

function New-Pfx([string]$Subject, [int]$Years, [string]$Base, [switch]$ExportPublic) {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $plain = [Convert]::ToBase64String($bytes)
    $secure = ConvertTo-SecureString $plain -AsPlainText -Force
    $cert = New-SelfSignedCertificate -Subject $Subject -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable -KeyUsage DigitalSignature, KeyEncipherment -NotAfter (Get-Date).AddYears($Years) -CertStoreLocation Cert:\CurrentUser\My
    try {
        Export-PfxCertificate -Cert $cert -FilePath (Join-Path $OutDir "$Base.pfx") -Password $secure | Out-Null
        [IO.File]::WriteAllText((Join-Path $OutDir "$Base-password"), $plain)
        if ($ExportPublic) { Export-Certificate -Cert $cert -FilePath (Join-Path $OutDir "$Base-public.cer") | Out-Null }
    }
    finally { Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force }
    [pscustomobject]@{ File = "$Base.pfx"; Thumbprint = $cert.Thumbprint; Expires = $cert.NotAfter.ToString('yyyy-MM-dd') }
}

New-Pfx 'CN=Helpdesk production key ring' 5 'dp' | Format-Table -AutoSize
New-Pfx 'CN=Helpdesk production Graph client' 1 'graph' -ExportPublic | Format-Table -AutoSize
foreach ($f in 'dp-password', 'graph-password') { & icacls (Join-Path $OutDir $f) /inheritance:r /grant:r "$($env:USERNAME):R" | Out-Null }

Write-Host "Created in $OutDir (private keys and passwords are not shown)." -ForegroundColor Green
Write-Host 'Set in the git-ignored .env:  SECRETS_DIR=<that folder>  DP_CERT_PATH=/run/helpdesk-secrets/dp.pfx  DP_CERT_PASSWORD_FILE=/run/helpdesk-secrets/dp-password'
Write-Host '                              GRAPH_CERT_PATH=/run/helpdesk-secrets/graph.pfx  GRAPH_CERT_PASSWORD=<contents of graph-password, typed locally>'
Write-Host 'Next: back up the folder (offline, encrypted) BEFORE first use. Upload graph-public.cer to the mail app registration (checklist section K).'
