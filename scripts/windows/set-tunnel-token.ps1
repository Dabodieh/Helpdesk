<#
.SYNOPSIS
  Writes the Cloudflare tunnel token to the git-ignored .tunnel.env from a hidden prompt. The token is never echoed, logged or passed on a command line.
.EXAMPLE
  .\scripts\windows\set-tunnel-token.ps1     # paste the token from Zero Trust > Networks > Tunnels > Helpdesk > Configure
#>
param([string]$TunnelId = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $TunnelId) {
    $line = Get-Content (Join-Path $root '.env') -ErrorAction SilentlyContinue | Where-Object { $_ -match '^CLOUDFLARE_TUNNEL_ID=' } | Select-Object -First 1
    if ($line) { $TunnelId = ($line -split '=', 2)[1].Trim() }
}
if ($TunnelId -notmatch '^[0-9a-fA-F-]{36}$') { throw 'give -TunnelId or set CLOUDFLARE_TUNNEL_ID in the git-ignored .env' }
$s = Read-Host 'Cloudflare tunnel token' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
try {
    $t = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim()
    if ($t -match '^cloudflared\s' ) { $t = ($t -split '\s+')[-1] }
    if ($t -notmatch '^[A-Za-z0-9+/=_-]{80,}$') { throw 'that does not look like a tunnel token (expected one long base64 string); nothing was written' }
    $d = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($t))
    if ($d -notmatch ('"t":"' + [regex]::Escape($TunnelId) + '"')) { throw 'token is not for the Helpdesk tunnel; nothing was written' }
    [IO.File]::WriteAllText((Join-Path $root '.tunnel.env'), "TUNNEL_TOKEN=$t`n")
    Write-Host '.tunnel.env updated for the Helpdesk tunnel (value not shown).' -ForegroundColor Green
}
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $t = $null }
