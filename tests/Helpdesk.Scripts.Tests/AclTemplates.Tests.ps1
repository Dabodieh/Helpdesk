# Run: Invoke-Pester tests/Helpdesk.Scripts.Tests/AclTemplates.Tests.ps1   (Pester 3.4)
# Everything here is in memory or mocked: no permission on this PC is read for judgement or changed. ACL objects are built from SIDs, so no account needs to exist.
. (Join-Path $PSScriptRoot '..\..\scripts\windows\lib\AclTemplates.ps1')

$svc = 'S-1-5-21-1111111111-2222222222-3333333333-1001'
$sidSystem = 'S-1-5-18'; $sidAdmin = 'S-1-5-32-544'; $sidUsers = 'S-1-5-32-545'; $sidAuth = 'S-1-5-11'; $sidEveryone = 'S-1-1-0'

function New-TestAcl([bool]$Protected, [object[]]$Rules) {
    $a = New-Object Security.AccessControl.DirectorySecurity
    $a.SetAccessRuleProtection($Protected, $false)
    foreach ($r in $Rules) {
        $type = if ($r.Count -gt 2) { $r[2] } else { 'Allow' }
        $prop = if ($r.Count -gt 3) { $r[3] } else { 'None' }
        $a.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($r[0])), [Security.AccessControl.FileSystemRights]$r[1], 'ContainerInherit, ObjectInherit', $prop, $type)))
    }
    return $a
}
function Base([string]$ServiceRights) { @(@($sidSystem, 'FullControl'), @($sidAdmin, 'FullControl'), @($svc, $ServiceRights)) }
function Messages($issues) { (@($issues | ForEach-Object { $_.Message }) -join ' | ') }

Describe 'Get-HelpdeskLayout' {
    It 'defaults to Program Files for the release and ProgramData for data' {
        $l = Get-HelpdeskLayout
        $l.InstallRoot | Should Be (Join-Path $env:ProgramFiles 'Helpdesk')
        $l.DataRoot | Should Be (Join-Path $env:ProgramData 'Helpdesk')
        $l.BackupRoot | Should Be (Join-Path (Join-Path $env:ProgramData 'Helpdesk') 'backups')
    }
    It 'keeps configuration, secrets and monitor state in separate folders' {
        $l = Get-HelpdeskLayout -InstallRoot 'D:\Apps\Helpdesk' -DataRoot 'D:\Data\Helpdesk'
        $l.ConfigFile | Should Be 'D:\Data\Helpdesk\config\monitor.env'
        $l.PassphraseFile | Should Be 'D:\Data\Helpdesk\secrets\backup.pass'
        $l.State | Should Be 'D:\Data\Helpdesk\monitor'
        $l.Scripts | Should Be 'D:\Apps\Helpdesk\scripts'
    }
    It 'accepts a customer-chosen backup location' { (Get-HelpdeskLayout -BackupRoot 'E:\HelpdeskBackups').BackupRoot | Should Be 'E:\HelpdeskBackups' }
}

Describe 'Get-RightsBits' {
    It 'turns the negative enum value of generic rights into an unsigned bit set' {
        (Get-RightsBits (-536805376)) | Should Be 3758161920
    }
}

Describe 'ACL templates round trip' {
    foreach ($kind in 'Program', 'Config', 'Secrets', 'State', 'Backups') {
        It "the in-memory ACL of the $kind template satisfies the $kind template" {
            $t = Get-HelpdeskAclTemplate -Kind $kind -ServiceAccount $svc
            $issues = @(Test-AclAgainstTemplate (New-AclFromTemplate $t) $t)
            (Messages $issues) | Should Be ''
        }
    }
    It 'gives the service account read only on configuration and secrets, modify on state and backups' {
        (Get-HelpdeskAclTemplate -Kind Config -ServiceAccount $svc).Entries | Where-Object { $_.Id -eq $svc } | ForEach-Object { $_.Rights } | Should Be 'Read'
        (Get-HelpdeskAclTemplate -Kind Secrets -ServiceAccount $svc).Entries | Where-Object { $_.Id -eq $svc } | ForEach-Object { $_.Rights } | Should Be 'Read'
        (Get-HelpdeskAclTemplate -Kind State -ServiceAccount $svc).Entries | Where-Object { $_.Id -eq $svc } | ForEach-Object { $_.Rights } | Should Be 'Modify'
        (Get-HelpdeskAclTemplate -Kind Backups -ServiceAccount $svc).Entries | Where-Object { $_.Id -eq $svc } | ForEach-Object { $_.Rights } | Should Be 'Modify'
    }
    It 'never grants ordinary users anything but read and execute on the program folder, and nothing elsewhere' {
        $prog = (Get-HelpdeskAclTemplate -Kind Program -ServiceAccount $svc).Entries | Where-Object { $_.Id -eq $sidUsers }
        $prog.Rights | Should Be 'ReadAndExecute'
        foreach ($kind in 'Config', 'Secrets', 'State', 'Backups') {
            @((Get-HelpdeskAclTemplate -Kind $kind -ServiceAccount $svc).Entries | Where-Object { $_.Id -in $sidUsers, $sidAuth, $sidEveryone }).Count | Should Be 0
        }
    }
    It 'grants nobody Modify or more on the program folder except SYSTEM and Administrators' {
        $risky = @((Get-HelpdeskAclTemplate -Kind Program -ServiceAccount $svc).Entries | Where-Object { $_.Rights -in 'Modify', 'FullControl', 'Write' -and $_.Id -notin $sidSystem, $sidAdmin })
        $risky.Count | Should Be 0
    }
}

Describe 'Test-AclAgainstTemplate detects wrong permissions' {
    $config = Get-HelpdeskAclTemplate -Kind Config -ServiceAccount $svc
    $program = Get-HelpdeskAclTemplate -Kind Program -ServiceAccount $svc
    It 'flags inheritance left on' {
        (Messages (Test-AclAgainstTemplate (New-TestAcl $false (Base 'Read')) $config)) | Should Match 'inheritance is not disabled'
    }
    It 'flags a missing service-account entry' {
        (Messages (Test-AclAgainstTemplate (New-TestAcl $true @(@($sidSystem, 'FullControl'), @($sidAdmin, 'FullControl'))) $config)) | Should Match 'missing the required entry'
    }
    It 'flags a service account that can change content when read is enough' {
        (Messages (Test-AclAgainstTemplate (New-TestAcl $true (Base 'Modify')) $config)) | Should Match 'can change content'
    }
    It 'flags Users with Modify on the program folder (ordinary users could replace scripts)' {
        $acl = New-TestAcl $true ((Base 'ReadAndExecute') + @(, @($sidUsers, 'Modify')))
        (Messages (Test-AclAgainstTemplate $acl $program)) | Should Match 'can change content'
    }
    It 'flags Authenticated Users with write on the program folder' {
        $acl = New-TestAcl $true ((Base 'ReadAndExecute') + @(, @($sidAuth, 'Write')))
        (Messages (Test-AclAgainstTemplate $acl $program)) | Should Match 'can change content'
    }
    It 'allows read and execute for Users on the program folder' {
        $acl = New-TestAcl $true ((Base 'ReadAndExecute') + @(, @($sidUsers, 'ReadAndExecute')))
        (Messages (Test-AclAgainstTemplate $acl $program)) | Should Be ''
    }
    It 'flags Users, Authenticated Users and Everyone reading configuration or secrets' {
        foreach ($sid in $sidUsers, $sidAuth, $sidEveryone) {
            (Messages (Test-AclAgainstTemplate (New-TestAcl $true ((Base 'Read') + @(, @($sid, 'ReadAndExecute')))) $config)) | Should Match 'can read this location'
        }
    }
    It 'warns about an unexpected extra identity but does not fail' {
        $issues = @(Test-AclAgainstTemplate (New-TestAcl $true ((Base 'Read') + @(, @('S-1-5-21-9-9-9-500', 'Read')))) $config)
        @($issues | Where-Object { $_.Severity -eq 'FAIL' }).Count | Should Be 0
        (Messages $issues) | Should Match 'unexpected entry'
    }
    It 'ignores deny rules and CREATOR OWNER entries' {
        $acl = New-TestAcl $true ((Base 'Read') + @(, @($sidUsers, 'FullControl', 'Deny')) + @(, @('S-1-3-0', 'FullControl', 'Allow', 'InheritOnly')))
        (Messages (Test-AclAgainstTemplate $acl $config)) | Should Be ''
    }
    It 'flags SYSTEM with fewer rights than required' {
        (Messages (Test-AclAgainstTemplate (New-TestAcl $true @(@($sidSystem, 'ReadAndExecute'), @($sidAdmin, 'FullControl'), @($svc, 'Read'))) $config)) | Should Match 'fewer rights'
    }
}

Describe 'Get-OrdinaryUserWriteGrants and Get-OrdinaryUserGrants' {
    It 'reports ordinary groups that can change content' {
        $acl = New-TestAcl $true @(@($sidUsers, 'Modify'), @($sidAuth, 'Write'), @($sidEveryone, 'FullControl'), @($sidAdmin, 'FullControl'))
        $g = @(Get-OrdinaryUserWriteGrants $acl)
        ($g -contains $sidUsers) | Should Be $true
        ($g -contains $sidAuth) | Should Be $true
        ($g -contains $sidEveryone) | Should Be $true
        ($g -contains $sidAdmin) | Should Be $false
    }
    It 'does not report read-only access as write' {
        @(Get-OrdinaryUserWriteGrants (New-TestAcl $true @(@($sidUsers, 'ReadAndExecute'), @($sidAuth, 'Read')))).Count | Should Be 0
    }
    It 'reports read access separately' {
        @(Get-OrdinaryUserGrants (New-TestAcl $true @(, @($sidUsers, 'ReadAndExecute'))) 'Read').Count | Should Be 1
    }
    It 'ignores deny rules' { @(Get-OrdinaryUserWriteGrants (New-TestAcl $true @(, @($sidUsers, 'Modify', 'Deny')))).Count | Should Be 0 }
}

Describe 'Get-IcaclsCommands' {
    $t = Get-HelpdeskAclTemplate -Kind Secrets -ServiceAccount $svc
    $cmds = @(Get-IcaclsCommands $t 'C:\ProgramData\Helpdesk\secrets')
    It 'removes inheritance first and grants only the template identities, by SID' {
        $cmds[0] | Should Match '/inheritance:r'
        ($cmds -join "`n") | Should Match '\*S-1-5-18:\(OI\)\(CI\)F'
        ($cmds -join "`n") | Should Match '\*S-1-5-32-544:\(OI\)\(CI\)F'
        ($cmds -join "`n") | Should Match ([regex]::Escape("*${svc}:(OI)(CI)R"))
        ($cmds -join "`n") | Should Not Match 'Everyone|Users|S-1-1-0|S-1-5-11'
    }
    It 'is only printed, never executed' { (Get-Command Get-IcaclsCommands).CommandType | Should Be 'Function' }
}

Describe 'Assert-AclTargetAllowed' {
    $dev = Join-Path $TestDrive 'devcheckout'; New-Item -ItemType Directory -Force -Path (Join-Path $dev '.git') | Out-Null
    $layout = Get-HelpdeskLayout -InstallRoot (Join-Path $TestDrive 'Program Files\Helpdesk') -DataRoot (Join-Path $TestDrive 'ProgramData\Helpdesk')
    It 'allows folders inside the layout roots' {
        { Assert-AclTargetAllowed $layout.Config $layout } | Should Not Throw
        { Assert-AclTargetAllowed $layout.InstallRoot $layout } | Should Not Throw
        { Assert-AclTargetAllowed $layout.BackupRoot $layout } | Should Not Throw
    }
    It 'refuses a drive root such as F:\' { { Assert-AclTargetAllowed 'F:\' $layout } | Should Throw 'drive root' }
    It 'refuses network paths' { { Assert-AclTargetAllowed '\\nas\share\x' $layout } | Should Throw 'network' }
    It 'refuses paths outside the layout' {
        { Assert-AclTargetAllowed 'F:\Software Development\Helpdesk' $layout } | Should Throw 'outside the Helpdesk installation layout'
        { Assert-AclTargetAllowed (Join-Path $TestDrive 'elsewhere') $layout } | Should Throw 'outside'
    }
    It 'refuses a layout whose root is a drive root' {
        $bad = Get-HelpdeskLayout -InstallRoot 'F:\' -DataRoot (Join-Path $TestDrive 'd')
        { Assert-AclTargetAllowed (Join-Path $TestDrive 'd\config') $bad } | Should Throw 'drive root'
    }
    It 'refuses a development checkout even when it is inside the layout' {
        $l2 = Get-HelpdeskLayout -InstallRoot $dev -DataRoot (Join-Path $TestDrive 'd2')
        { Assert-AclTargetAllowed $dev $l2 } | Should Throw 'development checkout'
    }
}

Describe 'Set-HelpdeskAcl (Get-Acl and Set-Acl are mocked)' {
    $layout = Get-HelpdeskLayout -InstallRoot (Join-Path $TestDrive 'PF\Helpdesk') -DataRoot (Join-Path $TestDrive 'PD\Helpdesk')
    It 'changes nothing under -WhatIf' {
        Mock Get-Acl { New-TestAcl $false @(@($sidUsers, 'Modify')) }
        Mock Set-Acl { }
        Set-HelpdeskAcl -Path $layout.Secrets -Kind Secrets -ServiceAccount $svc -Layout $layout -WhatIf
        Assert-MockCalled Set-Acl -Exactly 0 -Scope It
    }
    It 'replaces the permissions with exactly the template and turns inheritance off' {
        Mock Get-Acl { New-TestAcl $false @(@($sidUsers, 'Modify'), @($sidAuth, 'Read')) }
        Mock Set-Acl { }
        Set-HelpdeskAcl -Path $layout.Secrets -Kind Secrets -ServiceAccount $svc -Layout $layout -Confirm:$false
        Assert-MockCalled Set-Acl -Exactly 1 -Scope It -ParameterFilter { $AclObject.AreAccessRulesProtected -and @($AclObject.Access).Count -eq 3 -and @(Get-OrdinaryUserGrants $AclObject 'Read').Count -eq 0 }
    }
    It 'refuses an unsafe target before touching anything' {
        Mock Get-Acl { throw 'must not be called' }
        Mock Set-Acl { throw 'must not be called' }
        { Set-HelpdeskAcl -Path 'F:\' -Kind Program -ServiceAccount $svc -Layout $layout -Confirm:$false } | Should Throw 'drive root'
        { Set-HelpdeskAcl -Path 'F:\Software Development\Helpdesk' -Kind Program -ServiceAccount $svc -Layout $layout -Confirm:$false } | Should Throw 'outside'
    }
}

Describe 'Get-ScriptTreeWriteIssues (ACL reads are mocked)' {
    $root = Join-Path $TestDrive 'scripts'
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'windows\lib'), (Join-Path $root 'ops') | Out-Null
    Set-Content (Join-Path $root 'windows\a.ps1') 'x'; Set-Content (Join-Path $root 'windows\lib\b.ps1') 'x'; Set-Content (Join-Path $root 'ops\c.sh') 'x'
    It 'reports nothing when only administrators and SYSTEM can write' {
        Mock Get-PathAcl { New-TestAcl $true @(@($sidSystem, 'FullControl'), @($sidAdmin, 'FullControl'), @($sidUsers, 'ReadAndExecute')) }
        @(Get-ScriptTreeWriteIssues $root).Count | Should Be 0
    }
    It 'reports every folder and file ordinary users can change' {
        Mock Get-PathAcl { New-TestAcl $true @(@($sidSystem, 'FullControl'), @($sidAuth, 'Modify')) }
        @(Get-ScriptTreeWriteIssues $root).Count | Should BeGreaterThan 4
    }
    It 'finds a single writable script' {
        Mock Get-PathAcl { if ($Path -like '*b.ps1') { New-TestAcl $true @(, @($sidUsers, 'Write')) } else { New-TestAcl $true @(, @($sidAdmin, 'FullControl')) } }
        $i = @(Get-ScriptTreeWriteIssues $root)
        $i.Count | Should Be 1
        $i[0] | Should Match 'b\.ps1'
    }
}
