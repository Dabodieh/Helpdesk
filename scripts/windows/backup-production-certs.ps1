<#
.SYNOPSIS
  Encrypted, verifiable backup of the production certificate folder (dp.pfx, graph.pfx, password files, graph-public.cer).
.DESCRIPTION
  Run it yourself: it asks for a passphrase twice (hidden). The passphrase is NOT stored by this script; record it in your password manager or on paper
  kept apart from this PC, otherwise the backup is not recoverable independently of this Windows installation.
  Output: one AES-256-CBC (PBKDF2, 600000 iterations) file you copy OFF this PC (USB drive, another machine, cloud vault). The script verifies it by
  decrypting and comparing the archive hash, and never prints the passphrase or any key material.
  Restore on another machine (OpenSSL 3, PowerShell):
    openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -in helpdesk-production-certs.zip.enc -out certs.zip   (asks for the passphrase)
    Expand-Archive certs.zip -DestinationPath <folder outside any repository>
  then set SECRETS_DIR to that folder. Passwords are the contents of dp-password and graph-password.
.EXAMPLE
  .\scripts\windows\backup-production-certs.ps1 -Destination E:\HelpdeskCertBackup
#>
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$SourceDir = (Join-Path $env:USERPROFILE 'HelpdeskSecrets\production')
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$openssl = @('C:\Program Files\Git\usr\bin\openssl.exe', 'C:\Program Files\OpenSSL-Win64\bin\openssl.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $openssl) { throw 'OpenSSL not found (Git for Windows ships one).' }
foreach ($n in 'dp.pfx', 'dp-password', 'graph.pfx', 'graph-password', 'graph-public.cer') {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDir $n))) { throw "missing $n in $SourceDir" }
}
if ($Destination -like "$repo*") { throw 'Destination must be outside the repository.' }
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$target = Join-Path $Destination ('helpdesk-production-certs-{0}.zip.enc' -f (Get-Date -Format 'yyyyMMdd-HHmm'))
if (Test-Path -LiteralPath $target) { throw "refusing to overwrite $target" }

function Read-Secret([string]$Prompt) {
    $s = Read-Host $Prompt -AsSecureString
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}
$a = Read-Secret 'Backup passphrase (min 16 characters; record it OFF this PC)'
$b = Read-Secret 'Repeat passphrase'
if ($a -ne $b -or $a.Length -lt 16) { throw 'passphrases differ or are shorter than 16 characters' }

$zip = Join-Path ([IO.Path]::GetTempPath()) ('hdcerts-' + [guid]::NewGuid().ToString('N') + '.zip')
$chk = $zip + '.chk'
try {
    # Only the five live files: never the 'retired' folder (it holds superseded material).
    Compress-Archive -Path ('dp.pfx', 'dp-password', 'graph.pfx', 'graph-password', 'graph-public.cer' | ForEach-Object { Join-Path $SourceDir $_ }) -DestinationPath $zip
    $env:HDCERT_PASS = $a
    & $openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -salt -pass env:HDCERT_PASS -in $zip -out $target
    if ($LASTEXITCODE -ne 0) { throw 'encryption failed' }
    & $openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -pass env:HDCERT_PASS -in $target -out $chk
    if ($LASTEXITCODE -ne 0) { throw 'verification decrypt failed' }
    if ((Get-FileHash $zip).Hash -ne (Get-FileHash $chk).Hash) { throw 'verification hash mismatch' }
}
finally {
    $env:HDCERT_PASS = $null; $a = $null; $b = $null
    foreach ($f in $zip, $chk) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }
}
"{0}  {1}" -f (Get-FileHash $target).Hash, (Split-Path $target -Leaf) | Set-Content "$target.sha256"
Write-Host "Backup written and verified: $target" -ForegroundColor Green
Write-Host 'Now copy it (and the .sha256) OFF this PC and store the passphrase separately. Without both, the certificates are not recoverable.'
