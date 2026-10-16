# Run: Invoke-Pester tests/Helpdesk.Scripts.Tests/Monitoring.Tests.ps1   (Pester 3.4; no Docker needed)
. (Join-Path $PSScriptRoot '..\..\scripts\windows\lib\Monitoring.ps1')

Describe 'Read-MonitorConfig and settings' {
    $cfgFile = Join-Path $TestDrive 'monitor.env'
    Set-Content -LiteralPath $cfgFile -Value @('# comment', 'ALERT_WEBHOOK_URL="https://ntfy.example.test/topic"', 'BACKUP_DIR=C:\Backups', 'BACKUP_REQUIRED_DIRS=\\nas\a;\\nas\b', 'ENTRA_SECRET_EXPIRES=2027-01-31')
    $cfg = Read-MonitorConfig $cfgFile
    $s = Get-MonitorSettings $cfg

    It 'parses values, strips quotes and ignores comments' {
        $cfg['ALERT_WEBHOOK_URL'] | Should Be 'https://ntfy.example.test/topic'
        $cfg.ContainsKey('# comment') | Should Be $false
    }
    It 'splits semicolon lists' { $s.RequiredBackupDirs.Count | Should Be 2 }
    It 'applies the agreed defaults' {
        $s.BackupMaxAgeMinutes | Should Be 120
        $s.MailSyncStaleMinutes | Should Be 15
        $s.ConfirmRuns | Should Be 2
        ($s.ExpiryWarnDays -join ',') | Should Be '30,14,7'
        $s.DockerExe | Should Be 'docker'
        $s.PruneDestinations | Should Be $false
    }
    It 'accepts a valid configuration' { @(Test-MonitorSettings $s).Count | Should Be 0 }
    It 'rejects a plain http alert URL' {
        $bad = Get-MonitorSettings @{ ALERT_WEBHOOK_URL = 'http://ntfy.example.test/t'; BACKUP_DIR = 'C:\b' }
        (Test-MonitorSettings $bad) -join ' ' | Should Match 'ALERT_WEBHOOK_URL'
    }
    It 'allows http only for loopback and only when the test switch is set' {
        (Test-WebhookUrl 'http://127.0.0.1:9/x' $false) | Should Be $false
        (Test-WebhookUrl 'http://127.0.0.1:9/x' $true) | Should Be $true
        (Test-WebhookUrl 'http://example.test/x' $true) | Should Be $false
    }
    It 'rejects a bad format, a bad date and a missing backup folder' {
        $bad = Get-MonitorSettings @{ ALERT_WEBHOOK_FORMAT = 'xml'; ENTRA_SECRET_EXPIRES = '31/01/2027' }
        $p = (Test-MonitorSettings $bad) -join ' '
        $p | Should Match 'ALERT_WEBHOOK_FORMAT'
        $p | Should Match 'ENTRA_SECRET_EXPIRES'
        $p | Should Match 'BACKUP_DIR'
    }
    It 'rejects a non-numeric threshold' {
        $bad = Get-MonitorSettings @{ BACKUP_DIR = 'C:\b'; MONITOR_CONFIRM_RUNS = 'abc' }
        (Test-MonitorSettings $bad) -join ' ' | Should Match 'ConfirmRuns'
    }
    It 'throws for a missing file without printing contents' { { Read-MonitorConfig (Join-Path $TestDrive 'nope.env') } | Should Throw 'not found' }
}

Describe 'Protect-LogText' {
    It 'removes URLs' { Protect-LogText 'failed posting to https://ntfy.example.test/secret-topic now' | Should Be 'failed posting to <url> now' }
    It 'removes credentials and bearer tokens' {
        (Protect-LogText 'password=hunter2 and Bearer abc123def') | Should Not Match 'hunter2|abc123def'
    }
    It 'removes long hashes and keys' { Protect-LogText ('hash ' + ('a' * 64)) | Should Be 'hash <redacted>' }
    It 'removes e-mail addresses' { Protect-LogText 'sent to someone@example.test' | Should Be 'sent to <email>' }
    It 'limits the length' { (Protect-LogText ('word ' * 200) 50).Length | Should BeLessThan 51 }
}

Describe 'Invoke-ExternalWithTimeout and Invoke-WithTimeout' {
    It 'returns output and exit code' {
        $r = Invoke-ExternalWithTimeout 'cmd.exe' @('/c', 'echo hello') 10
        $r.TimedOut | Should Be $false
        $r.ExitCode | Should Be 0
        $r.Output.Trim() | Should Be 'hello'
    }
    It 'kills a program that does not finish and reports TimedOut' {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-ExternalWithTimeout 'powershell.exe' @('-NoProfile', '-Command', 'Start-Sleep -Seconds 60') 3
        $sw.Stop()
        $r.TimedOut | Should Be $true
        $sw.Elapsed.TotalSeconds | Should BeLessThan 20
    }
    It 'passes stdin to the program' {
        $r = Invoke-ExternalWithTimeout 'findstr.exe' @('x') 10 "axb`r`nzzz"
        $r.Output | Should Match 'axb'
    }
    It 'reports a missing program without throwing' { (Invoke-ExternalWithTimeout 'definitely-not-a-program.exe' @() 5).ExitCode | Should Be -2 }
    It 'abandons a blocked script block after the limit' {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-WithTimeout { Start-Sleep -Seconds 60 } @() 2
        $sw.Stop()
        $r.TimedOut | Should Be $true
        $sw.Elapsed.TotalSeconds | Should BeLessThan 15
    }
    It 'returns a script block result' { (Invoke-WithTimeout { param($a) $a + 1 } @(41) 10).Result | Should Be 42 }
    It 'treats a path that cannot be reached as absent' { (Test-PathWithTimeout (Join-Path $TestDrive 'nothing-here') 5) | Should Be $false }
}

Describe 'Get-ExpiryLevel' {
    It 'is empty outside the warning window' { Get-ExpiryLevel 31 | Should BeNullOrEmpty }
    It 'uses the 30, 14 and 7 day tiers' {
        Get-ExpiryLevel 30 | Should Be '30'
        Get-ExpiryLevel 20 | Should Be '30'
        Get-ExpiryLevel 14 | Should Be '14'
        Get-ExpiryLevel 8 | Should Be '14'
        Get-ExpiryLevel 7 | Should Be '7'
        Get-ExpiryLevel 0.5 | Should Be '7'
    }
    It 'reports expired' { Get-ExpiryLevel -0.1 | Should Be 'expired' }
}

Describe 'Update-AlertState' {
    $t0 = [datetime]'2027-01-01T10:00:00Z'
    function Res([string]$status, [string]$level = '', [bool]$immediate = $false, [string]$detail = 'detail') { New-CheckResult 'k1' 'Check one' $status $detail $level $immediate }

    It 'does not alert on the first failure' {
        $st = @{}
        @(Update-AlertState $st @(Res 'FAIL') $t0).Count | Should Be 0
        $st['k1'].Fails | Should Be 1
    }
    It 'fires after the second consecutive failure (within 10 minutes at a 5-minute schedule)' {
        $st = @{}
        $null = Update-AlertState $st @(Res 'FAIL') $t0
        $e = @(Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(5))
        $e.Count | Should Be 1
        $e[0].Type | Should Be 'FIRING'
        $e[0].Key | Should Be 'k1'
    }
    It 'does not repeat a firing alert (deduplication)' {
        $st = @{}
        $null = Update-AlertState $st @(Res 'FAIL') $t0
        $null = Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(5)
        @(Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(10)).Count | Should Be 0
        @(Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(60)).Count | Should Be 0
    }
    It 'sends a reminder after the reminder interval' {
        $st = @{}
        $null = Update-AlertState $st @(Res 'FAIL') $t0
        $null = Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(5)
        $e = @(Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(5 + 361) 2 360)
        $e.Count | Should Be 1
        $e[0].Type | Should Be 'REMINDER'
    }
    It 'fires immediately for an Immediate result' {
        $st = @{}
        @(Update-AlertState $st @(Res 'FAIL' '30' $true) $t0).Count | Should Be 1
    }
    It 'sends exactly one recovery notice' {
        $st = @{}
        $null = Update-AlertState $st @(Res 'FAIL') $t0
        $null = Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(5)
        $e = @(Update-AlertState $st @(Res 'OK') $t0.AddMinutes(10))
        $e.Count | Should Be 1
        $e[0].Type | Should Be 'RESOLVED'
        @(Update-AlertState $st @(Res 'OK') $t0.AddMinutes(15)).Count | Should Be 0
        $st.ContainsKey('k1') | Should Be $false
    }
    It 'forgets a failure that recovered before it was confirmed (no recovery notice)' {
        $st = @{}
        $null = Update-AlertState $st @(Res 'FAIL') $t0
        @(Update-AlertState $st @(Res 'OK') $t0.AddMinutes(5)).Count | Should Be 0
        @(Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(10)).Count | Should Be 0
    }
    It 'does not change state for skipped checks' {
        $st = @{}
        $null = Update-AlertState $st @(Res 'FAIL') $t0
        $null = Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(5)
        @(Update-AlertState $st @(Res 'SKIP') $t0.AddMinutes(10)).Count | Should Be 0
        $st['k1'].Firing | Should Be $true
    }
    It 'notifies again when the level escalates (30 to 14 days)' {
        $st = @{}
        $null = Update-AlertState $st @(Res 'FAIL' '30' $true) $t0
        @(Update-AlertState $st @(Res 'FAIL' '30' $true) $t0.AddMinutes(5)).Count | Should Be 0
        $e = @(Update-AlertState $st @(Res 'FAIL' '14' $true) $t0.AddMinutes(10))
        $e.Count | Should Be 1
        $e[0].Level | Should Be '14'
    }
    It 'retries an undelivered first alert on the next run' {
        $st = @{}
        $null = Update-AlertState $st @(Res 'FAIL') $t0
        $e = @(Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(5))
        Undo-AlertEvent $st $e[0]
        $again = @(Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(10))
        $again.Count | Should Be 1
        $again[0].Type | Should Be 'FIRING'
    }
    It 'retries an undelivered recovery notice on the next run' {
        $st = @{}
        $null = Update-AlertState $st @(Res 'FAIL') $t0
        $null = Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(5)
        $e = @(Update-AlertState $st @(Res 'OK') $t0.AddMinutes(10))
        Undo-AlertEvent $st $e[0]
        $again = @(Update-AlertState $st @(Res 'OK') $t0.AddMinutes(15))
        $again.Count | Should Be 1
        $again[0].Type | Should Be 'RESOLVED'
    }
    It 'persists and reloads state' {
        $st = @{}
        $null = Update-AlertState $st @(Res 'FAIL') $t0
        $null = Update-AlertState $st @(Res 'FAIL') $t0.AddMinutes(5)
        Save-AlertState $TestDrive $st
        $loaded = Read-AlertState $TestDrive
        $loaded['k1'].Firing | Should Be $true
        @(Update-AlertState $loaded @(Res 'FAIL') $t0.AddMinutes(10)).Count | Should Be 0
    }
    It 'tolerates a corrupt state file' {
        $dir = Join-Path $TestDrive 'corrupt'; New-Item -ItemType Directory -Path $dir | Out-Null
        Set-Content -LiteralPath (Join-Path $dir 'alert-state.json') -Value '{ not json'
        (Read-AlertState $dir).Count | Should Be 0
    }
}

Describe 'New-AlertMessage' {
    It 'marks expiry within 7 days and expired as urgent, other alerts as high, recovery as default' {
        (New-AlertMessage ([pscustomobject]@{ Type = 'FIRING'; Key = 'k'; Name = 'N'; Detail = 'd'; Level = '7'; Since = $null })).Priority | Should Be 'urgent'
        (New-AlertMessage ([pscustomobject]@{ Type = 'FIRING'; Key = 'k'; Name = 'N'; Detail = 'd'; Level = ''; Since = $null })).Priority | Should Be 'high'
        (New-AlertMessage ([pscustomobject]@{ Type = 'RESOLVED'; Key = 'k'; Name = 'N'; Detail = 'd'; Level = ''; Since = $null })).Priority | Should Be 'default'
    }
    It 'never carries a URL or credential from the detail text' {
        $m = New-AlertMessage ([pscustomobject]@{ Type = 'FIRING'; Key = 'k'; Name = 'N'; Detail = 'could not post to https://x.example.test/abc token=SECRETVALUE'; Level = ''; Since = $null })
        $m.Body | Should Not Match 'x.example.test|SECRETVALUE'
    }
}

Describe 'Webhook and heartbeat delivery (loopback listener)' {
    function Start-TestListener {
        $port = Get-Random -Minimum 20000 -Maximum 40000
        $l = New-Object System.Net.HttpListener
        $l.Prefixes.Add("http://127.0.0.1:$port/")
        $l.Start()
        return @{ Listener = $l; Url = "http://127.0.0.1:$port/topic" }
    }
    function Invoke-InJob([string]$Code) {
        $lib = Join-Path $PSScriptRoot '..\..\scripts\windows\lib\Monitoring.ps1'
        $job = Start-Job -ScriptBlock ([scriptblock]::Create(". '$lib'; $Code"))
        return $job
    }
    function Receive-One($Listener, $Job) {
        $ar = $Listener.BeginGetContext($null, $null)
        if (-not $ar.AsyncWaitHandle.WaitOne(30000)) { throw 'no request received' }
        $ctx = $Listener.EndGetContext($ar)
        $body = (New-Object IO.StreamReader($ctx.Request.InputStream, [Text.Encoding]::UTF8)).ReadToEnd()
        $out = @{ Headers = $ctx.Request.Headers; Body = $body; Method = $ctx.Request.HttpMethod; ContentType = $ctx.Request.ContentType }
        $ctx.Response.StatusCode = 200; $ctx.Response.Close()
        $null = Wait-Job $Job -Timeout 30
        return $out
    }
    $msg = '[pscustomobject]@{ Title = ''Helpdesk ALERT: Test''; Body = ''FIRING on HOST. detail''; Priority = ''high''; Tags = ''rotating_light''; Type = ''FIRING''; Key = ''k'' }'

    It 'posts ntfy-style: text body with Title, Priority and Tags headers' {
        $t = Start-TestListener
        try {
            $j = Invoke-InJob "`$m = $msg; Send-WebhookAlert `$m '$($t.Url)' 'ntfy' '' 15 `$true"
            $r = Receive-One $t.Listener $j
            $r.Method | Should Be 'POST'
            $r.Body | Should Be 'FIRING on HOST. detail'
            $r.Headers['Title'] | Should Be 'Helpdesk ALERT: Test'
            $r.Headers['Priority'] | Should Be 'high'
            $r.Headers['Tags'] | Should Be 'rotating_light'
            (Receive-Job $j) | Should Be $true
        } finally { $t.Listener.Stop(); Get-Job | Remove-Job -Force }
    }
    It 'sends a bearer token from a token file without putting it in the body' {
        $t = Start-TestListener
        $tok = Join-Path $TestDrive 'token.txt'; Set-Content -LiteralPath $tok -Value 'tk_testtoken123'
        try {
            $j = Invoke-InJob "`$m = $msg; Send-WebhookAlert `$m '$($t.Url)' 'ntfy' '$tok' 15 `$true"
            $r = Receive-One $t.Listener $j
            $r.Headers['Authorization'] | Should Be 'Bearer tk_testtoken123'
            $r.Body | Should Not Match 'tk_testtoken123'
        } finally { $t.Listener.Stop(); Get-Job | Remove-Job -Force }
    }
    It 'posts the json format' {
        $t = Start-TestListener
        try {
            $j = Invoke-InJob "`$m = $msg; Send-WebhookAlert `$m '$($t.Url)' 'json' '' 15 `$true"
            $r = Receive-One $t.Listener $j
            $r.ContentType | Should Match 'application/json'
            $o = $r.Body | ConvertFrom-Json
            $o.status | Should Be 'FIRING'
            $o.key | Should Be 'k'
        } finally { $t.Listener.Stop(); Get-Job | Remove-Job -Force }
    }
    It 'pings the heartbeat URL with GET' {
        $t = Start-TestListener
        try {
            $j = Invoke-InJob "Send-Heartbeat '$($t.Url)' 10 `$true"
            $r = Receive-One $t.Listener $j
            $r.Method | Should Be 'GET'
            (Receive-Job $j) | Should Be $true
        } finally { $t.Listener.Stop(); Get-Job | Remove-Job -Force }
    }
    It 'returns false when nothing listens, without throwing' {
        $m = [pscustomobject]@{ Title = 'T'; Body = 'B'; Priority = 'high'; Tags = 'x'; Type = 'FIRING'; Key = 'k' }
        (Send-WebhookAlert $m 'http://127.0.0.1:9/topic' 'ntfy' '' 3 $true) | Should Be $false
        (Send-Heartbeat 'http://127.0.0.1:9/ping' 3 $true) | Should Be $false
    }
    It 'refuses a non-https URL before any network call' {
        $m = [pscustomobject]@{ Title = 'T'; Body = 'B'; Priority = 'high'; Tags = 'x'; Type = 'FIRING'; Key = 'k' }
        (Send-WebhookAlert $m 'http://example.test/topic' 'ntfy' '' 3 $false) | Should Be $false
        (Send-Heartbeat 'http://example.test/ping' 3 $false) | Should Be $false
    }
}

Describe 'Get-CertificateExpiry' {
    function New-TestCert([int]$Days) {
        $rsa = [Security.Cryptography.RSA]::Create(2048)
        $req = New-Object Security.Cryptography.X509Certificates.CertificateRequest('CN=test', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        return $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays($Days))
    }
    It 'reads the expiry of a .cer without a password' {
        $c = New-TestCert 20
        $f = Join-Path $TestDrive 'a.cer'
        [IO.File]::WriteAllBytes($f, $c.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $d = ((Get-CertificateExpiry $f) - [datetime]::UtcNow).TotalDays
        $d | Should BeGreaterThan 19
        $d | Should BeLessThan 21
    }
    It 'reads the expiry of a .pfx with a password file' {
        $c = New-TestCert 10
        $pfx = Join-Path $TestDrive 'a.pfx'; $pw = Join-Path $TestDrive 'a.pw'
        Set-Content -LiteralPath $pw -Value 'pfx-test-password'
        [IO.File]::WriteAllBytes($pfx, $c.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, 'pfx-test-password'))
        $d = ((Get-CertificateExpiry "$pfx|$pw") - [datetime]::UtcNow).TotalDays
        $d | Should BeGreaterThan 9
        $d | Should BeLessThan 11
    }
}

Describe 'Test-BroadlyReadable' {
    It 'is false for a file restricted to the current user and true once Everyone can read it' {
        $f = Join-Path $TestDrive 'secret.txt'
        Set-Content -LiteralPath $f -Value 'x'
        & icacls $f /inheritance:r /grant:r "$($env:USERNAME):(F)" | Out-Null
        Test-BroadlyReadable $f | Should Be $false
        & icacls $f /grant 'Everyone:(R)' | Out-Null
        Test-BroadlyReadable $f | Should Be $true
    }
}
