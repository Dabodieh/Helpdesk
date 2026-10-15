<#
.SYNOPSIS
  Independent check of an encrypted certificate backup made by backup-production-certs.ps1. Prints PASS/FAIL lines only; never a password or key.
.DESCRIPTION
  Verifies the .sha256 sidecar, decrypts with the passphrase (asked, hidden), expands to a temporary folder, and checks that each PFX opens with the password file
  stored beside it, that the thumbprints equal the live certificates in SourceDir, that graph-public.cer matches graph.pfx, and that no 'retired' material is inside.
  The temporary folder is removed afterwards.
.EXAMPLE
  .\scripts\windows\verify-production-certs-backup.ps1 -Backup I:\HelpdeskCertBackup\helpdesk-production-certs-20261014-1800.zip.enc
#>
param(
    [Parameter(Mandatory)][string]$Backup,
    [string]$SourceDir = (Join-Path $env:USERPROFILE 'HelpdeskSecrets\production')
)
$ErrorActionPreference = 'Stop'
$openssl = @('C:\Program Files\Git\usr\bin\openssl.exe', 'C:\Program Files\OpenSSL-Win64\bin\openssl.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $openssl) { throw 'OpenSSL not found (Git for Windows ships one).' }
$results = New-Object System.Collections.Generic.List[string]
function Check([string]$Name, [bool]$Ok) { $results.Add(('{0}  {1}' -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name)) }

$expected = ((Get-Content -LiteralPath "$Backup.sha256" -Raw).Trim() -split '\s+')[0]
Check 'sha256 sidecar matches the backup file' ((Get-FileHash -LiteralPath $Backup).Hash -eq $expected)

$s = Read-Host 'Backup passphrase' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('hdverify-' + [guid]::NewGuid().ToString('N'))
try {
    $env:HDCERT_PASS = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    New-Item -ItemType Directory -Path $tmp | Out-Null
    $zip = Join-Path $tmp 'b.zip'
    & $openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -pass env:HDCERT_PASS -in $Backup -out $zip 2>$null
    Check 'decrypts with the passphrase' ($LASTEXITCODE -eq 0 -and (Test-Path $zip))
    if (Test-Path $zip) {
        $out = Join-Path $tmp 'x'
        Expand-Archive -LiteralPath $zip -DestinationPath $out
        Check 'archive holds exactly the five live files' (((@(Get-ChildItem $out -Recurse -File).Name | Sort-Object) -join ',') -eq 'dp-password,dp.pfx,graph-password,graph-public.cer,graph.pfx')
        Check 'no retired material inside' (-not (Get-ChildItem $out -Recurse -Directory | Where-Object Name -eq 'retired'))
        foreach ($n in 'dp', 'graph') {
            try {
                $pw = (Get-Content -LiteralPath (Join-Path $out "$n-password") -Raw).Trim()
                $b = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $out "$n.pfx"), $pw)
                $l = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $SourceDir "$n.pfx"), (Get-Content -LiteralPath (Join-Path $SourceDir "$n-password") -Raw).Trim())
                Check "$n.pfx opens with its backed-up password, private key present" $b.HasPrivateKey
                Check "$n.pfx thumbprint equals the live certificate" ($b.Thumbprint -eq $l.Thumbprint)
                Check "$n-password file equals the live password file" ((Get-FileHash -LiteralPath (Join-Path $out "$n-password")).Hash -eq (Get-FileHash -LiteralPath (Join-Path $SourceDir "$n-password")).Hash)
            } catch { Check "$n.pfx opens and matches live ($($_.Exception.GetType().Name))" $false }
        }
        $cer = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $out 'graph-public.cer'))
        Check 'graph-public.cer matches graph.pfx' ($cer.Thumbprint -eq [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $out 'graph.pfx'), (Get-Content -LiteralPath (Join-Path $out 'graph-password') -Raw).Trim()).Thumbprint)
    }
}
finally {
    $env:HDCERT_PASS = $null
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
}
if ($results.Count -eq 0) { $results.Add('FAIL  no checks completed') }
$results | ForEach-Object { Write-Host $_ -ForegroundColor $(if ($_ -like 'PASS*') { 'Green' } else { 'Red' }) }
if ($results -like 'FAIL*') { exit 1 }
