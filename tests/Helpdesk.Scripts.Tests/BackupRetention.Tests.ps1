# Run: Invoke-Pester tests/Helpdesk.Scripts.Tests/BackupRetention.Tests.ps1   (Pester 3.4; temporary folders only)
. (Join-Path $PSScriptRoot '..\..\scripts\windows\lib\Monitoring.ps1')
. (Join-Path $PSScriptRoot '..\..\scripts\windows\lib\BackupRetention.ps1')

$now = [datetime]'2027-03-01T12:00:00Z'

# One backup set as the backup scripts write it: dump + .sha256 + .meta.json, objects and configuration bundles a few seconds later.
function New-TestSet([string]$Dir, [datetime]$Time, [bool]$Good = $true, [string]$Prefix = 'helpdesk-prod') {
    $stamp = { param($t) $t.ToString("yyyyMMdd'T'HHmmss'Z'") }
    $dump = Join-Path $Dir ("$Prefix-" + (& $stamp $Time) + '.dump.enc')
    [IO.File]::WriteAllBytes($dump, [Text.Encoding]::UTF8.GetBytes("dump $($Time.Ticks)"))
    $hash = if ($Good) { (Get-FileHash -LiteralPath $dump -Algorithm SHA256).Hash.ToLower() } else { ('0' * 64) }
    Set-Content -LiteralPath "$dump.sha256" -Value "$hash  $(Split-Path $dump -Leaf)"
    Set-Content -LiteralPath "$dump.meta.json" -Value '{}'
    foreach ($k in @(@('objects', 2), @('config', 4))) {
        $f = Join-Path $Dir ("$Prefix-$($k[0])-" + (& $stamp $Time.AddSeconds($k[1])) + '.tar.enc')
        [IO.File]::WriteAllBytes($f, [byte[]](1, 2, 3))
        Set-Content -LiteralPath "$f.sha256" -Value 'x'
    }
}
function New-Dir([string]$Name) { $d = Join-Path $TestDrive $Name; New-Item -ItemType Directory -Force -Path $d | Out-Null; return $d }

Describe 'Backup destinations' {
    It 'converts local and UNC paths for Git bash' {
        ConvertTo-GitBashPath 'C:\Backups\prod' | Should Be '/c/Backups/prod'
        ConvertTo-GitBashPath '\\nas\helpdesk-backups\prod' | Should Be '//nas/helpdesk-backups/prod'
    }
    It 'finds the root that must already exist' {
        Get-DestinationRoot '\\nas\share\a\b' | Should Be '\\nas\share'
        Get-DestinationRoot 'I:\HelpdeskBackups\prod' | Should Be 'I:\'
        Get-DestinationRoot 'relative\path' | Should BeNullOrEmpty
    }
    It 'accepts a folder below an existing root and a UNC share root that exists' {
        (Test-BackupDestination (Join-Path $TestDrive 'not-yet-created') 10) | Should Be $true
    }
    It 'rejects a missing drive, a missing UNC server and a relative path' {
        (Test-BackupDestination 'Q:\nothing\here' 5) | Should Be $false
        (Test-BackupDestination '\\helpdesk-no-such-server-xyz\share\prod' 5) | Should Be $false
        (Test-BackupDestination 'relative\path' 5) | Should Be $false
    }
}

Describe 'Get-BackupSets' {
    $d = New-Dir 'sets'
    New-TestSet $d $now
    New-TestSet $d $now.AddHours(-1)
    Set-Content -LiteralPath (Join-Path $d 'last-backup.json') -Value '{}'
    Set-Content -LiteralPath (Join-Path $d 'notes.txt') -Value 'mine'
    New-TestSet $d $now.AddHours(-2) $true 'other-product'
    It 'groups dump, sidecars and the bundles written right after it' {
        $sets = @(Get-BackupSets $d)
        $sets.Count | Should Be 2
        $sets[0].Files.Count | Should Be 7
        $sets[0].Time | Should Be $now
    }
    It 'ignores files that are not backups of this prefix' {
        $names = @(Get-BackupSets $d | ForEach-Object { $_.Files } | ForEach-Object { $_.Name })
        ($names -contains 'last-backup.json') | Should Be $false
        ($names -contains 'notes.txt') | Should Be $false
        (@($names | Where-Object { $_ -like 'other-product*' })).Count | Should Be 0
    }
}

Describe 'Invoke-BackupRetention' {
    It 'removes nothing while every set is younger than the retention days' {
        $d = New-Dir 'young'
        0..29 | ForEach-Object { New-TestSet $d $now.AddHours(-$_) }
        $r = Invoke-BackupRetention -Directory $d -Days 14 -MinSets 24 -Now $now
        @($r.Removed).Count | Should Be 0
        @(Get-ChildItem $d).Count | Should Be (30 * 7)
    }
    It 'keeps sets younger than 14 days AND the 24 newest verified, removes the rest with their bundles' {
        $d = New-Dir 'daily'
        0..39 | ForEach-Object { New-TestSet $d $now.AddDays(-$_) }
        $r = Invoke-BackupRetention -Directory $d -Days 14 -MinSets 24 -Now $now
        $left = @(Get-BackupSets $d)
        $left.Count | Should Be 24
        ($left | Sort-Object Time | Select-Object -First 1).Time | Should Be $now.AddDays(-23)
        @($r.Removed).Count | Should Be (16 * 7)
        @(Get-ChildItem $d -Filter '*objects*').Count | Should Be (24 * 2)
    }
    It 'keeps every set when fewer than 24 verified sets exist even if they are old' {
        $d = New-Dir 'few-old'
        20..24 | ForEach-Object { New-TestSet $d $now.AddDays(-$_) }
        @((Invoke-BackupRetention -Directory $d -Days 14 -MinSets 24 -Now $now).Removed).Count | Should Be 0
    }
    It 'does not count a set whose checksum does not match as a successful backup' {
        $d = New-Dir 'corrupt'
        0..2 | ForEach-Object { New-TestSet $d $now.AddDays(-20 - $_) $false }
        0..23 | ForEach-Object { New-TestSet $d $now.AddDays(-30 - $_) $true }
        $null = Invoke-BackupRetention -Directory $d -Days 14 -MinSets 24 -Now $now
        $left = @(Get-BackupSets $d)
        @($left | Where-Object { Test-BackupSetVerified $_ }).Count | Should Be 24
        @($left | Where-Object { -not (Test-BackupSetVerified $_) }).Count | Should Be 0
    }
    It 'removes nothing when no verified set exists' {
        $d = New-Dir 'none'
        0..4 | ForEach-Object { New-TestSet $d $now.AddDays(-40 - $_) $false }
        $r = Invoke-BackupRetention -Directory $d -Days 14 -MinSets 24 -Now $now
        @($r.Removed).Count | Should Be 0
        $r.Note | Should Match 'no verified'
    }
    It 'never touches other files in the folder' {
        $d = New-Dir 'others'
        0..39 | ForEach-Object { New-TestSet $d $now.AddDays(-$_) }
        Set-Content -LiteralPath (Join-Path $d 'last-backup.json') -Value '{}'
        Set-Content -LiteralPath (Join-Path $d 'important.txt') -Value 'x'
        New-Item -ItemType Directory -Path (Join-Path $d 'volume-cold') | Out-Null
        New-TestSet $d $now.AddDays(-60) $true 'helpdesk-dev'
        $null = Invoke-BackupRetention -Directory $d -Days 14 -MinSets 24 -Now $now
        Test-Path (Join-Path $d 'last-backup.json') | Should Be $true
        Test-Path (Join-Path $d 'important.txt') | Should Be $true
        Test-Path (Join-Path $d 'volume-cold') | Should Be $true
        @(Get-ChildItem $d -Filter 'helpdesk-dev*').Count | Should Be 7
    }
    It 'previews with -WhatIf and deletes nothing' {
        $d = New-Dir 'whatif'
        0..25 | ForEach-Object { New-TestSet $d $now.AddDays(-$_) }
        $before = @(Get-ChildItem $d).Count
        $r = Invoke-BackupRetention -Directory $d -Days 14 -MinSets 24 -Now $now -WhatIf
        @($r.Removed).Count | Should Be (2 * 7)
        @(Get-ChildItem $d).Count | Should Be $before
    }
    It 'judges age by the time in the file name, not the modification time of a copy' {
        $d = New-Dir 'copied'
        0..29 | ForEach-Object { New-TestSet $d $now.AddDays(-$_) }
        Get-ChildItem $d | ForEach-Object { $_.LastWriteTime = [datetime]::Now }
        $r = Invoke-BackupRetention -Directory $d -Days 14 -MinSets 24 -Now $now
        @($r.Removed).Count | Should Be (6 * 7)
    }
}

Describe 'Set-BackupStatusFailed' {
    It 'marks the run failed, keeps the recorded fields and removes secrets from the reason' {
        $f = Join-Path $TestDrive 'last-backup.json'
        Set-Content -LiteralPath $f -Value '{"status":"ok","file":"helpdesk-prod-1.dump.enc","verified":true,"copies":0}'
        Set-BackupStatusFailed $f 'required destination unavailable password=hunter2 \\nas\share' $now
        $o = Get-Content -LiteralPath $f -Raw | ConvertFrom-Json
        $o.status | Should Be 'failed'
        $o.file | Should Be 'helpdesk-prod-1.dump.enc'
        $o.error | Should Match 'required destination unavailable'
        $o.error | Should Not Match 'hunter2'
    }
}

Describe 'backup-production.ps1 -WhatIf' {
    $script = Join-Path $PSScriptRoot '..\..\scripts\windows\backup-production.ps1'
    It 'takes no backup, reports a missing required destination and removes nothing' {
        $ErrorActionPreference = 'Continue'
        $d = New-Dir 'wi-backups'
        0..25 | ForEach-Object { New-TestSet $d $now.AddDays(-$_) }
        $before = @(Get-ChildItem $d).Count
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -BackupDir $d -CopyDirs 'Q:\no-such-disk\x' -RequiredCopyDirs 'Q:\no-such-nas\prod' -WhatIf 2>&1 | Out-String
        $out | Should Match 'required destinations available 0/1'
        $out | Should Match 'would FAIL'
        @(Get-ChildItem $d).Count | Should Be $before
        Test-Path (Join-Path $d 'last-backup.json') | Should Be $false
    }
}
