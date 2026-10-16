# Run: Invoke-Pester tests/Helpdesk.Scripts.Tests/WatchdogChecks.Tests.ps1   (Pester 3.4; Docker and the file system are mocked or temporary)
. (Join-Path $PSScriptRoot '..\..\scripts\windows\lib\Monitoring.ps1')
. (Join-Path $PSScriptRoot '..\..\scripts\windows\lib\WatchdogChecks.ps1')

function New-Settings([hashtable]$Extra = @{}) {
    $c = @{ BACKUP_DIR = (Join-Path $TestDrive 'backups'); ENTRA_SECRET_EXPIRES = '2099-01-01' }
    foreach ($k in $Extra.Keys) { $c[$k] = $Extra[$k] }
    Get-MonitorSettings $c
}
function Ok([string]$Output = '') { [pscustomobject]@{ ExitCode = 0; Output = $Output; Error = ''; TimedOut = $false } }
function Failed([int]$Code = 1) { [pscustomobject]@{ ExitCode = $Code; Output = ''; Error = ''; TimedOut = $false } }
function Hung { [pscustomobject]@{ ExitCode = -1; Output = ''; Error = ''; TimedOut = $true } }
$now = [datetime]'2027-03-01T12:00:00Z'

Describe 'Test-DockerEngine' {
    $s = New-Settings
    It 'is OK when the engine answers' {
        Mock Invoke-ExternalWithTimeout { Ok '29.1.0' }
        (Test-DockerEngine $s).Status | Should Be 'OK'
    }
    It 'fails when docker does not answer in time and says so' {
        Mock Invoke-ExternalWithTimeout { Hung }
        Mock Get-BootAgeMinutes { 500 }
        Mock Get-DockerDesktopRunning { $true }
        $r = Test-DockerEngine $s
        $r.Status | Should Be 'FAIL'
        $r.Detail | Should Match 'no answer within 20 s'
    }
    It 'tells the operator to sign in when Docker Desktop is not running (restart, nobody signed in)' {
        Mock Invoke-ExternalWithTimeout { Failed }
        Mock Get-BootAgeMinutes { 500 }
        Mock Get-DockerDesktopRunning { $false }
        (Test-DockerEngine $s).Detail | Should Match 'sign in'
    }
    It 'waits during the boot grace period instead of alerting' {
        Mock Invoke-ExternalWithTimeout { Failed }
        Mock Get-BootAgeMinutes { 3 }
        Mock Get-DockerDesktopRunning { $false }
        (Test-DockerEngine $s).Status | Should Be 'SKIP'
    }
}

Describe 'Test-StackContainers' {
    $s = New-Settings
    It 'is OK for running healthy containers' {
        Mock Invoke-ExternalWithTimeout { Ok 'running|healthy' }
        @(Test-StackContainers $s | Where-Object { $_.Status -ne 'OK' }).Count | Should Be 0
    }
    It 'fails for a stopped container, an unhealthy one, a missing one and a hung docker' {
        $script:n = 0
        Mock Invoke-ExternalWithTimeout {
            $script:n++
            switch ($script:n) { 1 { Ok 'exited|none' } 2 { Ok 'running|unhealthy' } default { Failed } }
        }
        $r = @(Test-StackContainers $s)
        ($r | Where-Object { $_.Status -eq 'FAIL' }).Count | Should Be 3
        $r[0].Detail | Should Match 'exited'
        $r[1].Detail | Should Match 'unhealthy'
        $r[2].Detail | Should Match 'not found'
    }
    It 'fails when docker hangs' {
        Mock Invoke-ExternalWithTimeout { Hung }
        (Test-StackContainers $s)[0].Detail | Should Match 'did not answer'
    }
}

Describe 'Test-DatabaseReady and Test-AppReady' {
    $s = New-Settings
    It 'database: OK / FAIL / hung' {
        Mock Invoke-ExternalWithTimeout { Ok }
        (Test-DatabaseReady $s).Status | Should Be 'OK'
        Mock Invoke-ExternalWithTimeout { Failed 2 }
        (Test-DatabaseReady $s).Status | Should Be 'FAIL'
        Mock Invoke-ExternalWithTimeout { Hung }
        (Test-DatabaseReady $s).Detail | Should Match 'no answer'
    }
    It 'application: ready 200 is OK' {
        Mock Invoke-ExternalWithTimeout { Ok 'HTTP/1.1 200 OK' }
        (Test-AppReady $s).Status | Should Be 'OK'
    }
    It 'application: 503 and silence both fail' {
        Mock Invoke-ExternalWithTimeout { Ok 'HTTP/1.1 503 Service Unavailable' }
        (Test-AppReady $s).Detail | Should Match '503'
        Mock Invoke-ExternalWithTimeout { Hung }
        (Test-AppReady $s).Status | Should Be 'FAIL'
        Mock Invoke-ExternalWithTimeout { Failed }
        (Test-AppReady $s).Detail | Should Match 'no response'
    }
    It 'sends the readiness script on stdin with a time limit' {
        Mock Invoke-ExternalWithTimeout { Ok 'HTTP/1.1 200 OK' }
        $null = Test-AppReady $s
        Assert-MockCalled Invoke-ExternalWithTimeout -Exactly 1 -Scope It -ParameterFilter { $TimeoutSec -eq 20 -and $StdIn -match '/health/ready' }
    }
}

Describe 'Test-MailSync' {
    $s = New-Settings
    It 'is OK when every active mailbox was checked recently' {
        Mock Invoke-ExternalWithTimeout { Ok "t|2.5`nf|9999" }
        (Test-MailSync $s).Status | Should Be 'OK'
    }
    It 'fails when an active mailbox is older than 15 minutes' {
        Mock Invoke-ExternalWithTimeout { Ok "t|16.2" }
        $r = Test-MailSync $s
        $r.Status | Should Be 'FAIL'
        $r.Detail | Should Match 'limit 15'
    }
    It 'passes at exactly the limit boundary minus one and fails just over' {
        Mock Invoke-ExternalWithTimeout { Ok "t|14.9" }
        (Test-MailSync $s).Status | Should Be 'OK'
        Mock Invoke-ExternalWithTimeout { Ok "t|15.1" }
        (Test-MailSync $s).Status | Should Be 'FAIL'
    }
    It 'fails for a mailbox that never completed a check' {
        Mock Invoke-ExternalWithTimeout { Ok "t|-1" }
        (Test-MailSync $s).Detail | Should Match 'never completed'
    }
    It 'fails when the state cannot be read' {
        Mock Invoke-ExternalWithTimeout { Failed }
        (Test-MailSync $s).Status | Should Be 'FAIL'
    }
    It 'is OK with no active mailbox' {
        Mock Invoke-ExternalWithTimeout { Ok '' }
        (Test-MailSync $s).Detail | Should Match 'no active mailbox'
    }
}

Describe 'Test-BackupFreshness' {
    $dir = Join-Path $TestDrive 'backups'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $s = New-Settings
    function Write-Status([string]$Json) { Set-Content -LiteralPath (Join-Path $dir 'last-backup.json') -Value $Json }
    It 'is OK for a verified backup inside two hours' {
        Write-Status '{"status":"ok","finishedAt":"2027-03-01T11:00:00Z","file":"x","verified":true}'
        (Test-BackupFreshness $s $now).Status | Should Be 'OK'
    }
    It 'fails after two hours without a verified backup' {
        Write-Status '{"status":"ok","finishedAt":"2027-03-01T09:30:00Z","file":"x","verified":true}'
        $r = Test-BackupFreshness $s $now
        $r.Status | Should Be 'FAIL'
        $r.Detail | Should Match '150 min'
    }
    It 'fails and reports the reason when the last backup failed, without secrets' {
        Write-Status '{"status":"failed","finishedAt":"2027-03-01T11:59:00Z","error":"copy to nas failed password=hunter2 https://nas.example.test/x"}'
        $r = Test-BackupFreshness $s $now
        $r.Status | Should Be 'FAIL'
        $r.Detail | Should Not Match 'hunter2|nas.example.test'
    }
    It 'fails when the backup is not marked verified' {
        Write-Status '{"status":"ok","finishedAt":"2027-03-01T11:59:00Z","file":"x","verified":false}'
        (Test-BackupFreshness $s $now).Detail | Should Match 'not marked verified'
    }
    It 'fails when there is no status file' {
        Remove-Item -LiteralPath (Join-Path $dir 'last-backup.json') -Force
        (Test-BackupFreshness $s $now).Detail | Should Match 'no backup status'
    }
}

Describe 'Test-OffHostBackup' {
    $local = Join-Path $TestDrive 'localb'; $nas = Join-Path $TestDrive 'nas'
    New-Item -ItemType Directory -Force -Path $local, $nas | Out-Null
    $name = 'helpdesk-prod-20270301T110000Z.dump.enc'
    Set-Content -LiteralPath (Join-Path $local $name) -Value 'dump-bytes'
    Set-Content -LiteralPath (Join-Path $local "$name.sha256") -Value 'abc123  helpdesk-prod-20270301T110000Z.dump.enc'
    Set-Content -LiteralPath (Join-Path $local 'last-backup.json') -Value ('{"status":"ok","finishedAt":"2027-03-01T11:00:00Z","file":"' + $name + '","verified":true}')
    $s = New-Settings @{ BACKUP_DIR = $local; BACKUP_REQUIRED_DIRS = $nas; MONITOR_COMMAND_TIMEOUT_SECONDS = '10' }

    It 'fails when no required destination is configured' {
        $r = @(Test-OffHostBackup (New-Settings @{ BACKUP_DIR = $local }))
        $r[0].Status | Should Be 'FAIL'
        $r[0].Detail | Should Match 'BACKUP_REQUIRED_DIRS'
    }
    It 'fails when the newest backup was not copied' {
        (Test-OffHostBackup $s)[0].Detail | Should Match 'copy missing'
    }
    It 'fails when the copy differs in size or checksum' {
        Set-Content -LiteralPath (Join-Path $nas $name) -Value 'different-length-content'
        Set-Content -LiteralPath (Join-Path $nas "$name.sha256") -Value 'abc123  x'
        (Test-OffHostBackup $s)[0].Detail | Should Match 'copy size'
    }
    It 'fails when the checksum record differs' {
        Set-Content -LiteralPath (Join-Path $nas $name) -Value 'dump-bytes'
        Set-Content -LiteralPath (Join-Path $nas "$name.sha256") -Value 'ffffff  x'
        (Test-OffHostBackup $s)[0].Detail | Should Match 'checksum'
    }
    It 'is OK when the copy matches' {
        Set-Content -LiteralPath (Join-Path $nas "$name.sha256") -Value 'abc123  x'
        (Test-OffHostBackup $s)[0].Status | Should Be 'OK'
    }
    It 'fails when the destination is unavailable' {
        $gone = New-Settings @{ BACKUP_DIR = $local; BACKUP_REQUIRED_DIRS = (Join-Path $TestDrive 'no-such-nas'); MONITOR_COMMAND_TIMEOUT_SECONDS = '5' }
        (Test-OffHostBackup $gone)[0].Detail | Should Match 'unavailable'
    }
}

Describe 'Test-DiskSpace' {
    It 'fails below the limit and passes above it' {
        (Test-DiskSpace (New-Settings @{ MONITOR_MIN_FREE_DISK_GB = '99999999' })).Status | Should Be 'FAIL'
        (Test-DiskSpace (New-Settings @{ MONITOR_MIN_FREE_DISK_GB = '1' })).Status | Should Be 'OK'
    }
    It 'skips a network location' { (Test-DiskSpace (New-Settings @{ BACKUP_DIR = '\\nas\share\b' })).Status | Should Be 'SKIP' }
}

Describe 'Test-CredentialExpiry' {
    It 'is OK far from expiry' {
        $r = @(Test-CredentialExpiry (New-Settings @{ ENTRA_SECRET_EXPIRES = '2027-12-31' }) $now | Where-Object { $_.Key -eq 'expiry-entra-secret' })
        $r[0].Status | Should Be 'OK'
    }
    It 'warns at the 30, 14 and 7 day tiers and when expired' {
        foreach ($case in @(@('2027-03-25', '30'), @('2027-03-12', '14'), @('2027-03-05', '7'), @('2027-02-28', 'expired'))) {
            $r = Test-CredentialExpiry (New-Settings @{ ENTRA_SECRET_EXPIRES = $case[0] }) $now | Where-Object { $_.Key -eq 'expiry-entra-secret' }
            $r.Status | Should Be 'FAIL'
            $r.Level | Should Be $case[1]
            $r.Immediate | Should Be $true
        }
    }
    It 'reports an unconfigured sign-in secret date instead of ignoring it' {
        $r = Test-CredentialExpiry (New-Settings @{ ENTRA_SECRET_EXPIRES = '' }) $now | Where-Object { $_.Key -eq 'expiry-entra-secret' }
        $r.Status | Should Be 'FAIL'
        $r.Level | Should Be 'unset'
    }
    It 'checks certificate files and flags an unreadable one' {
        $rsa = [Security.Cryptography.RSA]::Create(2048)
        $req = New-Object Security.Cryptography.X509Certificates.CertificateRequest('CN=t', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $c = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(5))
        $f = Join-Path $TestDrive 'soon.cer'
        [IO.File]::WriteAllBytes($f, $c.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $bad = Join-Path $TestDrive 'broken.cer'; Set-Content -LiteralPath $bad -Value 'not a certificate'
        $r = @(Test-CredentialExpiry (New-Settings @{ CERT_EXPIRY_FILES = "$f;$bad" }) ([datetime]::UtcNow) | Where-Object { $_.Key -like 'expiry-cert-*' })
        $r[0].Level | Should Be '7'
        $r[1].Detail | Should Match 'could not be read'
    }
}

Describe 'Test-SecretFileAccess' {
    It 'flags broadly readable secret files without naming them' {
        Mock Test-BroadlyReadable { $true }
        $f = Join-Path $TestDrive 'p.txt'; Set-Content -LiteralPath $f -Value 'x'
        $r = Test-SecretFileAccess (New-Settings) @($f)
        $r.Status | Should Be 'FAIL'
        $r.Detail | Should Not Match 'p\.txt'
    }
    It 'passes restricted files and ignores unset paths' {
        Mock Test-BroadlyReadable { $false }
        $f = Join-Path $TestDrive 'q.txt'; Set-Content -LiteralPath $f -Value 'x'
        (Test-SecretFileAccess (New-Settings) @($f, '', $null)).Status | Should Be 'OK'
    }
}

Describe 'Invoke-WatchdogChecks' {
    It 'reports one Docker failure and skips everything that depends on Docker' {
        Mock Test-DockerEngine { New-CheckResult 'docker-engine' 'Docker engine' 'FAIL' 'down' }
        Mock Test-StackContainers { throw 'must not run' }
        Mock Test-DatabaseReady { throw 'must not run' }
        Mock Test-AppReady { throw 'must not run' }
        Mock Test-MailSync { throw 'must not run' }
        $r = @(Invoke-WatchdogChecks (New-Settings) @() $now)
        @($r | Where-Object { $_.Key -in 'database', 'app-ready', 'mail-sync' }).Count | Should Be 0
        @($r | Where-Object { $_.Key -eq 'docker-engine' -and $_.Status -eq 'FAIL' }).Count | Should Be 1
        @($r | Where-Object { $_.Key -eq 'backup-fresh' }).Count | Should Be 1
    }
    It 'does not run the mail check while the database is down' {
        Mock Test-DockerEngine { New-CheckResult 'docker-engine' 'Docker engine' 'OK' 'ok' }
        Mock Test-StackContainers { @() }
        Mock Test-DatabaseReady { New-CheckResult 'database' 'Database' 'FAIL' 'down' }
        Mock Test-AppReady { New-CheckResult 'app-ready' 'Application readiness' 'FAIL' 'down' }
        Mock Test-MailSync { throw 'must not run' }
        $r = @(Invoke-WatchdogChecks (New-Settings) @() $now)
        @($r | Where-Object { $_.Key -eq 'mail-sync' }).Count | Should Be 0
    }
    It 'turns a crashing check into a failure instead of stopping the run' {
        Mock Test-DockerEngine { New-CheckResult 'docker-engine' 'Docker engine' 'OK' 'ok' }
        Mock Test-StackContainers { @() }
        Mock Test-DatabaseReady { New-CheckResult 'database' 'Database' 'OK' 'ok' }
        Mock Test-AppReady { throw 'boom with password=hunter2' }
        Mock Test-MailSync { New-CheckResult 'mail-sync' 'Inbound mail check' 'OK' 'ok' }
        $r = @(Invoke-WatchdogChecks (New-Settings) @() $now)
        $crash = $r | Where-Object { $_.Key -eq 'app-ready' }
        $crash.Status | Should Be 'FAIL'
        $crash.Detail | Should Not Match 'hunter2'
        ($r | Where-Object { $_.Key -eq 'mail-sync' }).Status | Should Be 'OK'
    }
}
