# Run: Invoke-Pester tests/Helpdesk.Scripts.Tests/Preflight.Tests.ps1   (Pester 3.4; Docker, ACL reads, Task Scheduler and the security policy are mocked, folders are temporary)
foreach ($l in 'Monitoring', 'WatchdogChecks', 'BackupRetention', 'AclTemplates', 'ProductionTasks', 'Preflight') { . (Join-Path $PSScriptRoot "..\..\scripts\windows\lib\$l.ps1") }

$sidSystem = 'S-1-5-18'; $sidAdmin = 'S-1-5-32-544'; $sidUsers = 'S-1-5-32-545'
function New-TestAcl([object[]]$Rules) {
    $a = New-Object Security.AccessControl.DirectorySecurity
    $a.SetAccessRuleProtection($true, $false)
    foreach ($r in $Rules) { $a.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($r[0])), [Security.AccessControl.FileSystemRights]$r[1], 'ContainerInherit, ObjectInherit', 'None', 'Allow'))) }
    return $a
}
function AdminOnly { New-TestAcl @(@($sidSystem, 'FullControl'), @($sidAdmin, 'FullControl')) }
function Ok([string]$Output = '') { [pscustomobject]@{ ExitCode = 0; Output = $Output; Error = ''; TimedOut = $false } }
function Denied { [pscustomobject]@{ ExitCode = 1; Output = ''; Error = 'permission denied while trying to connect to the docker API'; TimedOut = $false } }
function NoEngine { [pscustomobject]@{ ExitCode = 1; Output = ''; Error = 'open //./pipe/x: The system cannot find the file specified.'; TimedOut = $false } }
function Hung { [pscustomobject]@{ ExitCode = -1; Output = ''; Error = ''; TimedOut = $true } }
function Info([bool]$Admin = $false, [string[]]$Groups = @('TONSPC\docker-users', 'BUILTIN\Users'), [string]$Name = 'TONSPC\hd-svc') { [pscustomobject]@{ Name = $Name; IsAdmin = $Admin; Groups = $Groups; Sid = 'S-1-5-21-1-2-3-1001' } }
function Settings([hashtable]$Extra = @{}) {
    $c = @{ BACKUP_DIR = (Join-Path $TestDrive 'backups'); ALERT_WEBHOOK_URL = 'https://alerts.example.invalid/t'; HEARTBEAT_URL = 'https://hb.example.invalid/p'; BACKUP_REQUIRED_DIRS = (Join-Path $TestDrive 'nas') }
    foreach ($k in $Extra.Keys) { $c[$k] = $Extra[$k] }
    Get-MonitorSettings $c
}
function ByName($results, [string]$Name) { @($results | Where-Object { $_.Name -eq $Name })[0] }

Describe 'Docker failure classification (watchdog and preflight)' {
    It 'tells an access problem from a missing engine and from a hang' {
        Get-DockerFailureKind (Denied) | Should Be 'access'
        Get-DockerFailureKind (NoEngine) | Should Be 'unavailable'
        Get-DockerFailureKind (Hung) | Should Be 'timeout'
        Get-DockerFailureKind ([pscustomobject]@{ ExitCode = 1; Output = ''; Error = 'something else'; TimedOut = $false }) | Should Be 'other'
    }
    It 'guides an access failure to docker-users without any path, URL or secret' {
        $g = Get-DockerFailureGuidance 'access' $true
        $g | Should Match 'docker-users'
        $g | Should Not Match 'pipe/|http|\\\\'
    }
    It 'says "sign in" for a missing engine while Docker Desktop is not running, and "check Docker Desktop" while it is' {
        Get-DockerFailureGuidance 'unavailable' $false | Should Match 'sign in'
        Get-DockerFailureGuidance 'unavailable' $true | Should Match 'open Docker Desktop'
    }
    It 'watchdog: an access failure is reported as access, with its own level, and is not hidden by the boot grace period' {
        Mock Invoke-ExternalWithTimeout { Denied }
        Mock Get-BootAgeMinutes { 1 }
        Mock Get-DockerDesktopRunning { $true }
        $r = Test-DockerEngine (Settings @{ MONITOR_BOOT_GRACE_MINUTES = '10' })
        $r.Status | Should Be 'FAIL'
        $r.Level | Should Be 'access'
        $r.Detail | Should Match 'docker-users'
    }
    It 'watchdog: a missing engine right after a restart is still held back by the grace period' {
        Mock Invoke-ExternalWithTimeout { NoEngine }
        Mock Get-BootAgeMinutes { 1 }
        Mock Get-DockerDesktopRunning { $false }
        (Test-DockerEngine (Settings @{ MONITOR_BOOT_GRACE_MINUTES = '10' })).Status | Should Be 'SKIP'
    }
    It 'watchdog: a missing engine with Docker Desktop stopped says to sign in and has no access level' {
        Mock Invoke-ExternalWithTimeout { NoEngine }
        Mock Get-BootAgeMinutes { 900 }
        Mock Get-DockerDesktopRunning { $false }
        $r = Test-DockerEngine (Settings)
        $r.Detail | Should Match 'sign in'
        $r.Level | Should BeNullOrEmpty
    }
    It 'watchdog: moving from "unavailable" to "access" notifies again (level change), staying "unavailable" does not' {
        $st = @{}
        $t0 = [datetime]'2027-01-01T10:00:00Z'
        $a = New-CheckResult 'docker-engine' 'Docker engine' 'FAIL' 'd' ''
        $b = New-CheckResult 'docker-engine' 'Docker engine' 'FAIL' 'd' 'access'
        $null = Update-AlertState $st @($a) $t0
        @(Update-AlertState $st @($a) $t0.AddMinutes(5)).Count | Should Be 1
        @(Update-AlertState $st @($a) $t0.AddMinutes(10)).Count | Should Be 0
        $e = @(Update-AlertState $st @($b) $t0.AddMinutes(15))
        $e.Count | Should Be 1
        $e[0].Level | Should Be 'access'
    }
}

Describe 'Security policy parsing and batch-logon state' {
    $policy = "[Privilege Rights]`r`nSeBatchLogonRight = *S-1-5-32-544,*S-1-5-32-551,*S-1-5-21-1-2-3-1001`r`nSeDenyBatchLogonRight = *S-1-5-21-1-2-3-1002`r`nSeInteractiveLogonRight = *S-1-5-32-544"
    It 'extracts the SIDs of a privilege' {
        $h = @(ConvertFrom-SeceditPrivilege $policy 'SeBatchLogonRight')
        $h.Count | Should Be 3
        ($h -contains 'S-1-5-21-1-2-3-1001') | Should Be $true
        @(ConvertFrom-SeceditPrivilege $policy 'SeNoSuchRight').Count | Should Be 0
        @(ConvertFrom-SeceditPrivilege '' 'SeBatchLogonRight').Count | Should Be 0
    }
    It 'grants through the account, grants through a group, denies first, otherwise missing' {
        Get-BatchLogonState @('S-1-5-21-1-2-3-1001') @() 'S-1-5-21-1-2-3-1001' @() | Should Be 'granted'
        Get-BatchLogonState @('S-1-5-32-559') @() 'S-1-5-21-1-2-3-1001' @('S-1-5-32-559') | Should Be 'granted'
        Get-BatchLogonState @('S-1-5-32-559') @('S-1-5-32-559') 'S-1-5-21-1-2-3-1001' @('S-1-5-32-559') | Should Be 'denied'
        Get-BatchLogonState @('S-1-5-32-544') @() 'S-1-5-21-1-2-3-1001' @() | Should Be 'missing'
    }
}

Describe 'Test-PfIdentity' {
    It 'passes for a standard user in docker-users' {
        $r = Test-PfIdentity (Info) '.\hd-svc'
        (ByName $r 'docker-users membership').Status | Should Be 'PASS'
        (ByName $r 'Identity').Status | Should Be 'PASS'
    }
    It 'fails for a standard user outside docker-users and says how to fix it' {
        $r = ByName (Test-PfIdentity (Info -Groups @('BUILTIN\Users')) '.\hd-svc') 'docker-users membership'
        $r.Status | Should Be 'FAIL'
        $r.Fix | Should Match 'docker-users'
    }
    It 'only warns for an administrator and recommends a standard account' {
        $r = Test-PfIdentity (Info -Admin $true -Groups @('BUILTIN\Administrators')) '.\hd-svc'
        (ByName $r 'docker-users membership').Status | Should Be 'WARN'
        (ByName $r 'Least privilege').Status | Should Be 'WARN'
    }
    It 'warns that results describe another account when not run as the service account' {
        (ByName (Test-PfIdentity (Info -Name 'TONSPC\abode') '.\hd-svc') 'Identity').Status | Should Be 'WARN'
    }
}

Describe 'Test-PfBatchLogon' {
    It 'cannot be read without elevation and says so (it is proven by the one-shot task)' {
        Mock Resolve-AccountSid { 'S-1-5-21-1-2-3-1001' }
        (Test-PfBatchLogon (Info) '.\hd-svc' $null).Status | Should Be 'WARN'
    }
    It 'fails when the account does not exist' {
        Mock Resolve-AccountSid { $null }
        (Test-PfBatchLogon (Info -Admin $true) '.\hd-svc' 'x').Status | Should Be 'FAIL'
    }
    It 'passes, fails and fails differently from the security policy' {
        Mock Resolve-AccountSid { 'S-1-5-21-1-2-3-1001' }
        Mock Get-LocalGroupSidsOfAccount { @() }
        (Test-PfBatchLogon (Info -Admin $true) '.\hd-svc' "SeBatchLogonRight = *S-1-5-21-1-2-3-1001`r`n").Status | Should Be 'PASS'
        $m = Test-PfBatchLogon (Info -Admin $true) '.\hd-svc' "SeBatchLogonRight = *S-1-5-32-544`r`n"
        $m.Status | Should Be 'FAIL'
        $m.Fix | Should Match 'Log on as a batch job'
        (Test-PfBatchLogon (Info -Admin $true) '.\hd-svc' "SeBatchLogonRight = *S-1-5-21-1-2-3-1001`r`nSeDenyBatchLogonRight = *S-1-5-21-1-2-3-1001`r`n").Detail | Should Match 'denied'
    }
    It 'is skipped when no service account is named' { (Test-PfBatchLogon (Info) '' $null).Status | Should Be 'SKIP' }
}

Describe 'Test-PfDocker' {
    $s = Settings
    It 'passes when the default context answers' {
        Mock Invoke-ExternalWithTimeout { Ok '29.0.0' }
        Mock Get-PipeAcl { New-TestAcl @(, @($sidAdmin, 'FullControl')) }
        (ByName (Test-PfDocker $s) 'Engine pipe access').Status | Should Be 'PASS'
        Assert-MockCalled Invoke-ExternalWithTimeout -Exactly 1 -Scope It -ParameterFilter { $Arguments -contains 'default' -and $Arguments -contains '--context' }
    }
    It 'fails with docker-users guidance on access denied' {
        Mock Invoke-ExternalWithTimeout { Denied }
        Mock Get-PipeAcl { $null }
        $r = ByName (Test-PfDocker $s) 'Engine pipe access'
        $r.Status | Should Be 'FAIL'
        $r.Fix | Should Match 'docker-users'
    }
    It 'warns (not fails) when the engine is simply not running, and fails on a hang' {
        Mock Get-PipeAcl { $null }
        Mock Get-DockerDesktopRunning { $false }
        Mock Invoke-ExternalWithTimeout { NoEngine }
        (ByName (Test-PfDocker $s) 'Engine pipe access').Status | Should Be 'WARN'
        Mock Invoke-ExternalWithTimeout { Hung }
        (ByName (Test-PfDocker $s) 'Engine pipe access').Status | Should Be 'FAIL'
    }
    It 'reports whether docker-users is on the pipe ACL' {
        Mock Invoke-ExternalWithTimeout { Ok '29.0.0' }
        $acl = New-Object Security.AccessControl.DirectorySecurity
        Mock Get-PipeAcl { $null }
        (ByName (Test-PfDocker $s) 'Pipe permissions').Status | Should Be 'WARN'
    }
}

Describe 'Test-PfScripts' {
    $root = Join-Path $TestDrive 'scripts'
    foreach ($f in 'windows\backup-production.ps1', 'windows\watchdog-production.ps1', 'windows\recover-production.ps1', 'windows\health-check-production.ps1', 'windows\lib\Monitoring.ps1', 'windows\lib\WatchdogChecks.ps1', 'windows\lib\BackupRetention.ps1', 'windows\lib\ModeGuard.ps1', 'ops\backup-all.sh', 'ops\lib-backup.sh', 'ops\backup-database.sh', 'ops\verify-backup.sh') {
        $p = Join-Path $root $f; New-Item -ItemType Directory -Force -Path (Split-Path $p -Parent) | Out-Null; Set-Content -LiteralPath $p -Value 'x'
    }
    It 'passes when every script is readable and only administrators can change them' {
        Mock Get-ScriptTreeWriteIssues { @() }
        $r = Test-PfScripts $root
        (ByName $r 'Read access to operational scripts').Status | Should Be 'PASS'
        (ByName $r 'Scripts not writable by ordinary users').Status | Should Be 'PASS'
    }
    It 'fails when ordinary users can change the scripts' {
        Mock Get-ScriptTreeWriteIssues { @('windows\a.ps1', 'windows') }
        $r = ByName (Test-PfScripts $root) 'Scripts not writable by ordinary users'
        $r.Status | Should Be 'FAIL'
        $r.Fix | Should Match 'Program Files'
    }
    It 'fails when a required script is missing' {
        Mock Get-ScriptTreeWriteIssues { @() }
        Remove-Item -LiteralPath (Join-Path $root 'ops\verify-backup.sh') -Force
        (ByName (Test-PfScripts $root) 'Read access to operational scripts').Status | Should Be 'FAIL'
    }
}

Describe 'Passphrase file format (compatibility with the key of existing backups)' {
    function Write-Bytes([string]$Name, [byte[]]$Bytes) { $p = Join-Path $TestDrive $Name; [IO.File]::WriteAllBytes($p, $Bytes); return $p }
    $enc = New-Object Text.UTF8Encoding $false
    It 'accepts the exact format the DPAPI-derived file has: text only, no line ending' { Test-PassphraseFileFormat (Write-Bytes 'ok1' $enc.GetBytes('correct horse battery')) | Should BeNullOrEmpty }
    It 'accepts a single trailing line feed' { Test-PassphraseFileFormat (Write-Bytes 'ok2' $enc.GetBytes("correct horse battery`n")) | Should BeNullOrEmpty }
    It 'warns (portability) about Windows line endings and extra lines' {
        (Test-PassphraseFileFormat (Write-Bytes 'crlf' $enc.GetBytes("secret`r`n"))).Severity | Should Be 'WARN'
        (Test-PassphraseFileFormat (Write-Bytes 'two' $enc.GetBytes("a`nb"))).Severity | Should Be 'WARN'
    }
    It 'fails what really changes the key here: byte-order mark, UTF-16, lone carriage return, empty file' {
        (Test-PassphraseFileFormat (Write-Bytes 'bom' ([byte[]](0xEF, 0xBB, 0xBF) + $enc.GetBytes('secret')))).Severity | Should Be 'FAIL'
        (Test-PassphraseFileFormat (Write-Bytes 'u16' ([Text.Encoding]::Unicode.GetPreamble() + [Text.Encoding]::Unicode.GetBytes('secret')))).Reason | Should Match 'UTF-16'
        (Test-PassphraseFileFormat (Write-Bytes 'cr' $enc.GetBytes("secret`r"))).Severity | Should Be 'FAIL'
        (Test-PassphraseFileFormat (Write-Bytes 'empty' ([byte[]]@()))).Reason | Should Match 'empty'
    }
    It 'never returns the content' { (Test-PassphraseFileFormat (Write-Bytes 'leak' $enc.GetBytes("topsecret-value`r`n")) | Out-String) | Should Not Match 'topsecret' }
    It 'New-PassphraseFileFromSecureString writes the compatible format and never overwrites' {
        $p = Join-Path $TestDrive 'new.pass'
        New-PassphraseFileFromSecureString $p (ConvertTo-SecureString 'a-long-typed-passphrase' -AsPlainText -Force)
        Test-PassphraseFileFormat $p | Should BeNullOrEmpty
        [IO.File]::ReadAllBytes($p).Length | Should Be 'a-long-typed-passphrase'.Length
        { New-PassphraseFileFromSecureString $p (ConvertTo-SecureString 'other-passphrase-here' -AsPlainText -Force) } | Should Throw 'overwrite'
    }
    It 'matches the real behaviour of the Windows OpenSSL: LF/CRLF keep the key, a BOM or a lone CR change it' {
        $bash = Find-GitBash
        if (-not $bash) { return }
        $ref = Write-Bytes 'k-ref' $enc.GetBytes('same-typed-passphrase')
        $plain = Join-Path $TestDrive 'plain.txt'; [IO.File]::WriteAllText($plain, 'backup-content')
        $cipher = Join-Path $TestDrive 'c.enc'
        function G([string]$p) { $p.Replace('\', '/') }
        function Run([string]$cmd) { Invoke-ExternalWithTimeout $bash @('-c', $cmd) 60 }
        (Run "openssl enc -aes-256-cbc -pbkdf2 -iter 1000 -salt -pass file:'$(G $ref)' -in '$(G $plain)' -out '$(G $cipher)'").ExitCode | Should Be 0
        function Decrypts([string]$name, [byte[]]$bytes) { (Run "openssl enc -d -aes-256-cbc -pbkdf2 -iter 1000 -pass file:'$(G (Write-Bytes $name $bytes))' -in '$(G $cipher)' 2>/dev/null").Output -eq 'backup-content' }
        Decrypts 'k-lf' $enc.GetBytes("same-typed-passphrase`n") | Should Be $true
        Decrypts 'k-crlf' $enc.GetBytes("same-typed-passphrase`r`n") | Should Be $true
        Decrypts 'k-bom' ([byte[]](0xEF, 0xBB, 0xBF) + $enc.GetBytes('same-typed-passphrase')) | Should Be $false
        Decrypts 'k-cr' $enc.GetBytes("same-typed-passphrase`r") | Should Be $false
    }
    It 'backup-production.ps1 keeps the DPAPI path, the passphrase-file option and the config bundle option' {
        $t = $null; $e = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path (Join-Path $PSScriptRoot '..\..\scripts\windows\backup-production.ps1')).Path, [ref]$t, [ref]$e)
        $names = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
        foreach ($n in 'PassphraseFile', 'DpapiKeyFile', 'IncludeConfig', 'ObjectVolume', 'NamePrefix') { ($names -contains $n) | Should Be $true }
        $src = Get-Content (Join-Path $PSScriptRoot '..\..\scripts\windows\backup-production.ps1') -Raw
        $src | Should Match 'ConvertTo-SecureString'
        $src | Should Match 'dev-backup-key\.dpapi\.txt'
    }
}

Describe 'Test-PfSecretFile' {
    $good = Join-Path $TestDrive 'good.pass'; [IO.File]::WriteAllText($good, 'a-secret-that-must-never-be-printed')
    $backups = Join-Path $TestDrive 'bk'; New-Item -ItemType Directory -Force -Path $backups | Out-Null
    It 'passes for a protected, readable, non-empty file and never shows the content' {
        Mock Get-PathAcl { AdminOnly }
        $r = Test-PfSecretFile $good 'Backup passphrase file' @($backups) $true $true
        $r.Status | Should Be 'PASS'
        ($r | Out-String) | Should Not Match 'never-be-printed'
    }
    It 'fails when it is not configured (required), skips when optional' {
        (Test-PfSecretFile '' 'X' @() $true).Status | Should Be 'FAIL'
        (Test-PfSecretFile '' 'X' @() $false).Status | Should Be 'SKIP'
    }
    It 'fails for a missing or empty file' {
        (Test-PfSecretFile (Join-Path $TestDrive 'nope') 'X' @() $true).Detail | Should Match 'does not exist'
        $e = Join-Path $TestDrive 'empty.pass'; [IO.File]::WriteAllBytes($e, [byte[]]@())
        (Test-PfSecretFile $e 'X' @() $true).Detail | Should Match 'empty'
    }
    It 'fails when ordinary users can read or write it' {
        Mock Get-PathAcl { New-TestAcl @(@($sidAdmin, 'FullControl'), @($sidUsers, 'Modify')) }
        $r = Test-PfSecretFile $good 'X' @() $true
        $r.Status | Should Be 'FAIL'
        $r.Detail | Should Match 'readable by ordinary users'
        $r.Detail | Should Match 'writable by ordinary users'
    }
    It 'fails when stored inside the backup folder' {
        Mock Get-PathAcl { AdminOnly }
        $inside = Join-Path $backups 'p.pass'; [IO.File]::WriteAllText($inside, 'x')
        (Test-PfSecretFile $inside 'X' @($backups) $true).Detail | Should Match 'stored inside'
    }
    It 'warns about a passphrase file with a Windows line ending and fails one with a byte-order mark' {
        Mock Get-PathAcl { AdminOnly }
        $crlf = Join-Path $TestDrive 'crlf.pass'; [IO.File]::WriteAllText($crlf, "secret`r`n")
        $bomf = Join-Path $TestDrive 'bom.pass'; [IO.File]::WriteAllBytes($bomf, [byte[]](0xEF, 0xBB, 0xBF, 0x73, 0x65))
        (Test-PfSecretFile $bomf 'X' @() $true $true).Status | Should Be 'FAIL'
        (Test-PfSecretFile $crlf 'X' @() $true $true).Status | Should Be 'WARN'
    }
}

Describe 'Invoke-DestinationMarkerTest' {
    It 'writes, reads back and removes exactly its own marker' {
        $d = Join-Path $TestDrive 'dest'; New-Item -ItemType Directory -Force -Path $d | Out-Null
        Set-Content (Join-Path $d 'keep.txt') 'keep'
        Invoke-DestinationMarkerTest $d 20 | Should Be 'ok'
        @(Get-ChildItem $d -Force).Count | Should Be 1
        (Get-ChildItem $d -Force)[0].Name | Should Be 'keep.txt'
    }
    It 'reports a destination it cannot write to' { Invoke-DestinationMarkerTest (Join-Path $TestDrive 'does-not-exist') 20 | Should Be 'cannot write' }
}

Describe 'Test-PfBackupDestinations' {
    $bk = Join-Path $TestDrive 'backups'; $nas = Join-Path $TestDrive 'nas'
    New-Item -ItemType Directory -Force -Path $bk, $nas | Out-Null
    $s = Settings @{ MONITOR_COMMAND_TIMEOUT_SECONDS = '10' }
    It 'checks permissions only, unless write tests are requested' {
        Mock Get-PathAcl { AdminOnly }
        $r = Test-PfBackupDestinations $s $false
        (ByName $r 'Backup folder write test').Status | Should Be 'SKIP'
        (ByName $r 'Required destination 1').Detail | Should Match 'not tested'
        @(Get-ChildItem $bk -Force).Count | Should Be 0
    }
    It 'proves read/write on the backup folder and the required destination when asked' {
        Mock Get-PathAcl { AdminOnly }
        $r = Test-PfBackupDestinations $s $true
        (ByName $r 'Backup folder write test').Status | Should Be 'PASS'
        (ByName $r 'Required destination 1 read/write').Status | Should Be 'PASS'
        @(Get-ChildItem $nas -Force).Count | Should Be 0
    }
    It 'fails for an unreachable required destination, a missing backup folder and ordinary-user access' {
        Mock Get-PathAcl { New-TestAcl @(@($sidAdmin, 'FullControl'), @($sidUsers, 'Modify')) }
        $r = Test-PfBackupDestinations (Settings @{ BACKUP_REQUIRED_DIRS = 'Q:\no-such-nas\prod'; MONITOR_COMMAND_TIMEOUT_SECONDS = '5' }) $false
        (ByName $r 'Required destination 1').Status | Should Be 'FAIL'
        (ByName $r 'Backup folder permissions').Status | Should Be 'FAIL'
        (ByName (Test-PfBackupDestinations (Settings @{ BACKUP_DIR = (Join-Path $TestDrive 'absent') }) $false) 'Backup folder').Status | Should Be 'FAIL'
    }
    It 'fails when no required destination is configured' {
        Mock Get-PathAcl { AdminOnly }
        (ByName (Test-PfBackupDestinations (Settings @{ BACKUP_REQUIRED_DIRS = '' }) $false) 'Required off-host destination').Status | Should Be 'FAIL'
    }
}

Describe 'Alert, heartbeat and recovery evidence' {
    It 'accepts https endpoints without contacting them' {
        $r = Test-PfAlertConfig (Settings)
        (ByName $r 'Alert endpoint').Status | Should Be 'PASS'
        (ByName $r 'Heartbeat').Status | Should Be 'PASS'
    }
    It 'fails a missing or plain-http alert endpoint and warns about a missing heartbeat' {
        (ByName (Test-PfAlertConfig (Settings @{ ALERT_WEBHOOK_URL = '' })) 'Alert endpoint').Status | Should Be 'FAIL'
        (ByName (Test-PfAlertConfig (Settings @{ ALERT_WEBHOOK_URL = 'http://x.invalid/t' })) 'Alert endpoint').Status | Should Be 'FAIL'
        (ByName (Test-PfAlertConfig (Settings @{ HEARTBEAT_URL = '' })) 'Heartbeat').Status | Should Be 'WARN'
        (ByName (Test-PfAlertConfig (Settings @{ HEARTBEAT_URL = 'http://x.invalid/p' })) 'Heartbeat').Status | Should Be 'FAIL'
    }
    It 'never prints the endpoint URL' { ((Test-PfAlertConfig (Settings)) | Out-String) | Should Not Match 'example\.invalid' }
    $now = [datetime]'2027-03-01T00:00:00Z'
    function Result([string]$Json) { $f = Join-Path $TestDrive ('r' + [guid]::NewGuid().ToString('N') + '.json'); Set-Content $f $Json; return $f }
    It 'verifies recovery only from a recent successful isolated restore result' {
        (Test-RecoveryVerified (Result '{"result":"ok","testedAt":"2027-02-20T10:00:00Z"}') 35 $now).Verified | Should Be $true
        (Test-RecoveryVerified (Result '{"result":"ok","testedAt":"2027-01-01T10:00:00Z"}') 35 $now).Reason | Should Match 'older than 35 days'
        (Test-RecoveryVerified (Result '{"result":"failed","testedAt":"2027-02-28T10:00:00Z"}') 35 $now).Verified | Should Be $false
        (Test-RecoveryVerified (Result 'not json') 35 $now).Verified | Should Be $false
        (Test-RecoveryVerified (Result '{"result":"ok"}') 35 $now).Reason | Should Match 'time is missing'
        (Test-RecoveryVerified (Join-Path $TestDrive 'none.json') 35 $now).Reason | Should Match 'no restore-test'
        (Test-RecoveryVerified '' 35 $now).Verified | Should Be $false
    }
    It 'reports the evidence as a warning (pruning stays off) until verified' {
        (Test-PfRecoveryEvidence (Settings)).Status | Should Be 'WARN'
        (Test-PfRecoveryEvidence (Settings @{ RESTORE_TEST_RESULT_FILE = (Result ('{"result":"ok","testedAt":"' + [datetime]::UtcNow.AddDays(-2).ToString('o') + '"}')) })).Status | Should Be 'PASS'
    }
}

Describe 'Test-PfDiskAndEncryption' {
    It 'says BitLocker cannot be read without elevation' {
        $r = ByName (Test-PfDiskAndEncryption (Settings) (Info)) 'BitLocker'
        $r.Status | Should Be 'WARN'
    }
    It 'passes when protection is on and warns when it is off or unreadable' {
        Mock Get-BitLockerState { 'On' }
        (Test-PfDiskAndEncryption (Settings) (Info -Admin $true) | Where-Object { $_.Name -like 'BitLocker*' -and $_.Status -ne 'PASS' }) | Should BeNullOrEmpty
        Mock Get-BitLockerState { 'Off' }
        @(Test-PfDiskAndEncryption (Settings) (Info -Admin $true) | Where-Object { $_.Name -like 'BitLocker*' -and $_.Status -eq 'WARN' }).Count | Should BeGreaterThan 0
        Mock Get-BitLockerState { $null }
        @(Test-PfDiskAndEncryption (Settings) (Info -Admin $true) | Where-Object { $_.Name -like 'BitLocker*' -and $_.Detail -match 'could not be read' }).Count | Should BeGreaterThan 0
    }
    It 'fails when there is not enough free space' {
        (ByName (Test-PfDiskAndEncryption (Settings @{ MONITOR_MIN_FREE_DISK_GB = '99999999' }) (Info)) 'Backup disk space').Status | Should Be 'FAIL'
    }
}

Describe 'Test-PfLayout' {
    It 'skips folders that do not exist yet and judges existing ones against their template' {
        $l = Get-HelpdeskLayout -InstallRoot (Join-Path $TestDrive 'PF') -DataRoot (Join-Path $TestDrive 'PD')
        New-Item -ItemType Directory -Force -Path $l.Secrets | Out-Null
        Mock Test-PathAgainstTemplate { @([pscustomobject]@{ Severity = 'FAIL'; Message = 'ordinary users can read this location' }) }
        $r = Test-PfLayout $l '.\hd-svc'
        (ByName $r 'Secrets folder permissions').Status | Should Be 'FAIL'
        (ByName $r 'Config folder').Status | Should Be 'SKIP'
    }
}

Describe 'Invoke-ActivationPreflight' {
    It 'runs every area and keeps going when single checks fail' {
        Mock Get-CurrentIdentityInfo { Info }
        Mock Get-SecurityPolicyText { $null }
        Mock Resolve-AccountSid { 'S-1-5-21-1-2-3-1001' }
        Mock Invoke-ExternalWithTimeout { NoEngine }
        Mock Get-PipeAcl { $null }
        Mock Get-DockerDesktopRunning { $false }
        Mock Get-ScriptTreeWriteIssues { @('x') }
        Mock Get-PathAcl { AdminOnly }
        $cfg = Join-Path $TestDrive 'monitor.env'; Set-Content $cfg 'x=1'
        $r = @(Invoke-ActivationPreflight -Settings (Settings) -ScriptRoot (Join-Path $TestDrive 'scripts') -ServiceUser '.\hd-svc' -ConfigFile $cfg -WriteTests $false)
        foreach ($a in 'Account', 'Docker', 'Tools', 'Scripts', 'Secrets', 'Backup', 'Alerts', 'Host') { @($r | Where-Object { $_.Area -eq $a }).Count | Should BeGreaterThan 0 }
        @($r | Where-Object { $_.Status -eq 'FAIL' }).Count | Should BeGreaterThan 0
    }
}

Describe 'Invoke-OneShotPreflight (Task Scheduler is mocked)' {
    $result = Join-Path $TestDrive 'preflight-result.json'
    $pw = ConvertTo-SecureString 'not-a-real-password' -AsPlainText -Force
    $args1 = @{ RepoRoot = 'C:\Program Files\Helpdesk'; ConfigFile = 'C:\ProgramData\Helpdesk\config\monitor.env'; ServiceUser = '.\hd-svc'; ResultFile = $result; ServicePassword = $pw; TimeoutSec = 3 }
    It 'refuses without the explicit authorisation switch' {
        Mock Register-ProductionTasks { }
        { Invoke-OneShotPreflight @args1 } | Should Throw 'AuthoriseOneShotTask'
        Assert-MockCalled Register-ProductionTasks -Exactly 0 -Scope It
    }
    It 'registers nothing under -WhatIf' {
        Mock Register-ProductionTasks { }
        Mock Start-ScheduledTask { }
        Mock Get-ScheduledTask { $null }
        Invoke-OneShotPreflight @args1 -Authorise -WhatIf
        Assert-MockCalled Register-ProductionTasks -Exactly 0 -Scope It
        Assert-MockCalled Start-ScheduledTask -Exactly 0 -Scope It
    }
    It 'registers, runs, collects the results and always removes the temporary task' {
        $script:calls = 0
        Mock Get-ScheduledTask { $script:calls++; if ($script:calls -eq 1) { $null } else { [pscustomobject]@{ State = 'Ready' } } }
        Mock Register-ProductionTasks { }
        Mock Start-Sleep { }
        Mock Unregister-ScheduledTask { }
        Mock Start-ScheduledTask { Set-Content -LiteralPath $result -Value '[{"Area":"Backup","Name":"Required destination 1 read/write","Status":"PASS","Detail":"ok","Fix":""}]' }
        $r = @(Invoke-OneShotPreflight @args1 -Authorise -Confirm:$false)
        $r.Count | Should Be 1
        $r[0].Status | Should Be 'PASS'
        Assert-MockCalled Register-ProductionTasks -Exactly 1 -Scope It -ParameterFilter { $Definitions[0].LogonType -eq 'Password' -and $Definitions[0].Trigger -eq 'Once' -and $Definitions[0].Arguments -match '-WriteTests' -and $Definitions[0].Arguments -notmatch '(?i)password' }
        Assert-MockCalled Unregister-ScheduledTask -Exactly 1 -Scope It
    }
    It 'reports a failure and still removes the task when the task never produces a result' {
        if (Test-Path $result) { Remove-Item $result -Force }
        $script:calls = 0
        Mock Get-ScheduledTask { $script:calls++; if ($script:calls -eq 1) { $null } else { [pscustomobject]@{ State = 'Ready' } } }
        Mock Get-ScheduledTaskInfo { [pscustomobject]@{ LastTaskResult = 0x8007052E } }
        Mock Register-ProductionTasks { }
        Mock Start-Sleep { }
        Mock Unregister-ScheduledTask { }
        Mock Start-ScheduledTask { }
        $r = @(Invoke-OneShotPreflight @args1 -Authorise -Confirm:$false)
        $r[0].Status | Should Be 'FAIL'
        $r[0].Detail | Should Match 'batch-logon right'
        Assert-MockCalled Unregister-ScheduledTask -Exactly 1 -Scope It
    }
    It 'removes the task even when registration fails, and refuses to reuse an existing task' {
        Mock Get-ScheduledTask { $null }
        Mock Register-ProductionTasks { throw 'registration failed' }
        Mock Unregister-ScheduledTask { }
        { Invoke-OneShotPreflight @args1 -Authorise -Confirm:$false } | Should Throw 'registration failed'
        Assert-MockCalled Unregister-ScheduledTask -Exactly 1 -Scope It
    }
    It 'refuses to reuse an existing task of the same name and does not register' {
        Mock Get-ScheduledTask { [pscustomobject]@{ State = 'Ready' } }
        Mock Register-ProductionTasks { }
        Mock Unregister-ScheduledTask { }
        { Invoke-OneShotPreflight @args1 -Authorise -Confirm:$false } | Should Throw 'already exists'
        Assert-MockCalled Register-ProductionTasks -Exactly 0 -Scope It
        Assert-MockCalled Unregister-ScheduledTask -Exactly 0 -Scope It
    }
}

Describe 'preflight-production-activation.ps1 (child process)' {
    $script = (Resolve-Path (Join-Path $PSScriptRoot '..\..\scripts\windows\preflight-production-activation.ps1')).Path
    $cfg = Join-Path $TestDrive 'monitor.env'
    Set-Content $cfg @('ALERT_WEBHOOK_URL=https://alerts.example.invalid/t', "BACKUP_DIR=$TestDrive\b", 'BACKUP_REQUIRED_DIRS=\\nas.example.invalid\h')
    It 'rejects an unusable configuration with exit code 2' {
        $ErrorActionPreference = 'Continue'
        $bad = Join-Path $TestDrive 'bad.env'; Set-Content $bad 'ALERT_WEBHOOK_URL=http://plain.example.invalid/t'
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -ConfigFile $bad 2>&1 | Out-String
        $LASTEXITCODE | Should Be 2
        $out | Should Match 'configuration invalid'
    }
    It '-OneShotTask -WhatIf registers nothing and asks for no password' {
        $ErrorActionPreference = 'Continue'
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -ConfigFile $cfg -ServiceUser '.\hd-svc' -OneShotTask -AuthoriseOneShotTask -WhatIf 2>&1 | Out-String
        $LASTEXITCODE | Should Be 0
        $out | Should Match 'no temporary task was registered'
        @(Get-ScheduledTask -TaskName 'Helpdesk Preflight*' -ErrorAction SilentlyContinue).Count | Should Be 0
    }
    It '-OneShotTask without authorisation is refused' {
        $ErrorActionPreference = 'Continue'
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -ConfigFile $cfg -ServiceUser '.\hd-svc' -OneShotTask 2>&1 | Out-String
        $LASTEXITCODE | Should Not Be 0
        $out | Should Match 'AuthoriseOneShotTask'
    }
    It 'a normal run prints findings, writes a JSON result, exits 1 on a failure and never prints the endpoint' {
        $ErrorActionPreference = 'Continue'
        $res = Join-Path $TestDrive 'result.json'
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -ConfigFile $cfg -ResultFile $res 2>&1 | Out-String
        $LASTEXITCODE | Should Be 1
        $out | Should Match 'checks:'
        $out | Should Not Match 'example\.invalid'
        (Get-Content $res -Raw | ConvertFrom-Json).Count | Should BeGreaterThan 10
        (Get-Content $res -Raw) | Should Not Match 'example\.invalid'
    }
}
