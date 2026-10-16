# Run: Invoke-Pester tests/Helpdesk.Scripts.Tests/ProductionTasks.Tests.ps1   (Pester 3.4; the Task Scheduler cmdlets are mocked, nothing is registered)
. (Join-Path $PSScriptRoot '..\..\scripts\windows\lib\Monitoring.ps1')
. (Join-Path $PSScriptRoot '..\..\scripts\windows\lib\ProductionTasks.ps1')

$repo = 'C:\Program Files\Helpdesk'
$cfg = 'C:\ProgramData\Helpdesk\monitor.env'
function New-Defs { Get-ProductionTaskDefinitions -RepoRoot $repo -ConfigFile $cfg -ServiceUser '.\hd-svc' -DesktopUser 'CORP\admin' -PassphraseFile 'C:\ProgramData\Helpdesk\backup.pass' }

Describe 'Get-ProductionTaskDefinitions' {
    $defs = @(New-Defs)
    $backup = $defs | Where-Object { $_.Name -eq 'Helpdesk Production Backup' }
    $watch = $defs | Where-Object { $_.Name -eq 'Helpdesk Production Watchdog' }
    $recover = $defs | Where-Object { $_.Name -eq 'Helpdesk Production Recovery' }

    It 'defines exactly the backup, watchdog and recovery tasks' { $defs.Count | Should Be 3 }
    It 'runs the backup hourly and the watchdog every 5 minutes as the dedicated service account without a signed-in user' {
        $backup.RepeatMinutes | Should Be 60
        $watch.RepeatMinutes | Should Be 5
        $backup.RunAs | Should Be '.\hd-svc'
        $watch.RunAs | Should Be '.\hd-svc'
        $backup.LogonType | Should Be 'Password'
        $watch.LogonType | Should Be 'Password'
    }
    It 'runs recovery at sign-in in the interactive session of the desktop user' {
        $recover.Trigger | Should Be 'AtLogOn'
        $recover.RunAs | Should Be 'CORP\admin'
        $recover.LogonType | Should Be 'Interactive'
    }
    It 'bounds every run with a time limit shorter than the schedule' {
        $watch.TimeLimitMinutes | Should BeLessThan $watch.RepeatMinutes
        $backup.TimeLimitMinutes | Should BeLessThan $backup.RepeatMinutes
        $recover.TimeLimitMinutes | Should BeGreaterThan 0
    }
    It 'passes the configuration and passphrase file paths, never a secret value' {
        $backup.Arguments | Should Match 'backup-production\.ps1'
        $backup.Arguments | Should Match 'monitor\.env'
        $backup.Arguments | Should Match 'backup\.pass'
        $watch.Arguments | Should Match 'watchdog-production\.ps1'
        ($defs | ForEach-Object { $_.Arguments }) -join ' ' | Should Not Match '(?i)password|token|https?://'
    }
    It 'adds -IncludeConfig only on request' {
        $backup.Arguments | Should Not Match 'IncludeConfig'
        (Get-ProductionTaskDefinitions -RepoRoot $repo -ConfigFile $cfg -ServiceUser 'a' -DesktopUser 'b' -IncludeConfig | Where-Object { $_.Name -like '*Backup' }).Arguments | Should Match '-IncludeConfig'
    }
    It 'never starts the Cloudflare tunnel or Development' {
        ($defs | ForEach-Object { $_.Arguments }) -join ' ' | Should Not Match '(?i)tunnel|dev\.ps1'
    }
}

Describe 'Register-ProductionTasks' {
    $defs = @(New-Defs)
    $pw = ConvertTo-SecureString 'not-a-real-password' -AsPlainText -Force

    It 'registers nothing under -WhatIf and asks for no password' {
        Mock Get-ScheduledTask { $null }
        Mock Register-ScheduledTask { }
        Register-ProductionTasks -Definitions $defs -WhatIf
        Assert-MockCalled Register-ScheduledTask -Exactly 0 -Scope It
    }
    It 'registers the three tasks, two with the service account password and one interactive' {
        Mock Get-ScheduledTask { $null }
        Mock Register-ScheduledTask { }
        Register-ProductionTasks -Definitions $defs -ServicePassword $pw
        Assert-MockCalled Register-ScheduledTask -Exactly 3 -Scope It
        Assert-MockCalled Register-ScheduledTask -Exactly 2 -Scope It -ParameterFilter { $User -eq '.\hd-svc' -and $RunLevel -eq 'Limited' }
        Assert-MockCalled Register-ScheduledTask -Exactly 1 -Scope It -ParameterFilter { $TaskName -eq 'Helpdesk Production Recovery' }
    }
    It 'refuses to replace an existing task without -Force' {
        Mock Get-ScheduledTask { [pscustomobject]@{ TaskName = 'x' } }
        Mock Register-ScheduledTask { }
        { Register-ProductionTasks -Definitions $defs -ServicePassword $pw } | Should Throw 'already exists'
        Assert-MockCalled Register-ScheduledTask -Exactly 0 -Scope It
    }
    It 'needs the service account password for the unattended tasks' {
        Mock Get-ScheduledTask { $null }
        Mock Register-ScheduledTask { }
        { Register-ProductionTasks -Definitions $defs } | Should Throw 'password'
    }
}

Describe 'Unregister-ProductionTasks' {
    It 'removes nothing under -WhatIf' {
        Mock Get-ScheduledTask { [pscustomobject]@{ TaskName = 'x' } }
        Mock Unregister-ScheduledTask { }
        Unregister-ProductionTasks -WhatIf
        Assert-MockCalled Unregister-ScheduledTask -Exactly 0 -Scope It
    }
    It 'removes only the three Helpdesk Production tasks by exact name' {
        Mock Get-ScheduledTask { [pscustomobject]@{ TaskName = 'x' } }
        Mock Unregister-ScheduledTask { }
        Unregister-ProductionTasks
        Assert-MockCalled Unregister-ScheduledTask -Exactly 3 -Scope It
        Assert-MockCalled Unregister-ScheduledTask -Exactly 1 -Scope It -ParameterFilter { $TaskName -eq 'Helpdesk Production Watchdog' }
    }
    It 'skips tasks that do not exist' {
        Mock Get-ScheduledTask { $null }
        Mock Unregister-ScheduledTask { }
        Unregister-ProductionTasks
        Assert-MockCalled Unregister-ScheduledTask -Exactly 0 -Scope It
    }
}

Describe 'install-production-tasks.ps1 and uninstall-production-tasks.ps1 -WhatIf (real cmdlets, nothing may be created)' {
    $install = Join-Path $PSScriptRoot '..\..\scripts\windows\install-production-tasks.ps1'
    $uninstall = Join-Path $PSScriptRoot '..\..\scripts\windows\uninstall-production-tasks.ps1'
    $pass = Join-Path $TestDrive 'backup.pass'; Set-Content -LiteralPath $pass -Value 'test-only'
    $conf = Join-Path $TestDrive 'monitor.env'
    Set-Content -LiteralPath $conf -Value @('ALERT_WEBHOOK_URL=https://ntfy.example.test/topic', "BACKUP_DIR=$TestDrive\b", "BACKUP_PASSPHRASE_FILE=$pass", 'BACKUP_REQUIRED_DIRS=\\nas\helpdesk\prod')

    It 'shows the plan and registers nothing' {
        $ErrorActionPreference = 'Continue'
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $install -ConfigFile $conf -ServiceUser '.\hd-svc' -DesktopUser 'CORP\admin' -WhatIf 2>&1 | Out-String
        $out | Should Match 'Helpdesk Production Watchdog: every 5 min'
        $out | Should Match 'WhatIf: nothing was registered and no password was requested'
        (Get-ScheduledTask -TaskName 'Helpdesk Production*' -ErrorAction SilentlyContinue) | Should BeNullOrEmpty
    }
    It 'refuses an incomplete configuration' {
        $ErrorActionPreference = 'Continue'
        $bad = Join-Path $TestDrive 'bad.env'; Set-Content -LiteralPath $bad -Value @("BACKUP_DIR=$TestDrive\b")
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $install -ConfigFile $bad -ServiceUser '.\hd-svc' -DesktopUser 'CORP\admin' -WhatIf 2>&1 | Out-String
        $out | Should Match 'cannot install'
        $out | Should Match 'BACKUP_REQUIRED_DIRS is required'
    }
    It 'uninstall -WhatIf removes nothing' {
        $ErrorActionPreference = 'Continue'
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $uninstall -WhatIf 2>&1 | Out-String
        $LASTEXITCODE | Should Be 0
    }
}
