# ONE read-only certificate-based Graph check: GET the Inbox folder resource of each mailbox with the Helpdesk mail app certificate and print only the HTTP status.
# Expected after Exchange RBAC propagation: test-a 200, test-b 200, outside 403, personal 403. Reads GRAPH_* from the git-ignored .env; prints no secret or token.
# Not a poller: run it once, and only after the propagation window.
param(
    [string[]]$Mailboxes = @()
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$m = @{}
foreach ($l in Get-Content (Join-Path $root '.env')) { if ($l -match '^([A-Z_0-9]+)=(.*)$') { $m[$Matches[1]] = $Matches[2].Trim() } }
$dir = $m['SECRETS_DIR']
if ($Mailboxes.Count -eq 0 -and $m['GRAPH_CHECK_MAILBOXES']) { $Mailboxes = @($m['GRAPH_CHECK_MAILBOXES'] -split '[,; ]+' | Where-Object { $_ }) }
if ($Mailboxes.Count -eq 0) { throw 'give -Mailboxes or set GRAPH_CHECK_MAILBOXES (comma separated) in the git-ignored .env' }
$c = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $dir 'graph.pfx'), $m['GRAPH_CERT_PASSWORD'])
function B64([byte[]]$b) { [Convert]::ToBase64String($b).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
$tokenUrl = "https://login.microsoftonline.com/$($m['GRAPH_TENANT_ID'])/oauth2/v2.0/token"
$h = @{ alg = 'RS256'; typ = 'JWT'; x5t = (B64 $c.GetCertHash()) } | ConvertTo-Json -Compress
$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$p = @{ aud = $tokenUrl; iss = $m['GRAPH_CLIENT_ID']; sub = $m['GRAPH_CLIENT_ID']; jti = [guid]::NewGuid().ToString(); nbf = $now; exp = $now + 300 } | ConvertTo-Json -Compress
$u = (B64 ([Text.Encoding]::UTF8.GetBytes($h))) + '.' + (B64 ([Text.Encoding]::UTF8.GetBytes($p)))
$sig = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($c).SignData([Text.Encoding]::UTF8.GetBytes($u), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$t = Invoke-RestMethod -Method Post -Uri $tokenUrl -Body @{
    client_id = $m['GRAPH_CLIENT_ID']; scope = 'https://graph.microsoft.com/.default'; grant_type = 'client_credentials'
    client_assertion_type = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'; client_assertion = "$u." + (B64 $sig)
}
foreach ($mb in $Mailboxes) {
    $code = 0; $err = ''
    try { $r = Invoke-WebRequest -Uri "https://graph.microsoft.com/v1.0/users/$mb/mailFolders/inbox?`$select=id" -Headers @{ Authorization = "Bearer $($t.access_token)" } -UseBasicParsing; $code = [int]$r.StatusCode }
    catch { $code = [int]$_.Exception.Response.StatusCode; try { $err = ($_.ErrorDetails.Message | ConvertFrom-Json).error.code } catch { } }
    '{0,-40} HTTP {1} {2}' -f $mb, $code, $err
}
