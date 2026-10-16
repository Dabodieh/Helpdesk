# Task Scheduler definitions for the Windows Production operations: backup, watchdog and sign-in recovery. Pure data first (Get-ProductionTaskDefinitions), then
# registration through ShouldProcess so -WhatIf shows exactly what would be created. No password or secret is ever part of a definition or an argument list.

$script:TaskNames = [ordered]@{
    Backup   = 'Helpdesk Production Backup'
    Watchdog = 'Helpdesk Production Watchdog'
    Recovery = 'Helpdesk Production Recovery'
}

function Get-ProductionTaskNames { return @($script:TaskNames.Values) }

# ServiceUser runs backup and watchdog without anyone signed in (logon type Password, "log on as a batch job" required). DesktopUser is the person whose sign-in starts
# Docker Desktop; the recovery task runs in that interactive session, because Docker Desktop cannot be started without one.
function Get-ProductionTaskDefinitions {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,
        [Parameter(Mandatory)][string]$ConfigFile,
        [Parameter(Mandatory)][string]$ServiceUser,
        [Parameter(Mandatory)][string]$DesktopUser,
        [string]$PassphraseFile = '',
        [string]$PowerShellExe = (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'),
        [switch]$IncludeConfig,
        [int]$WatchdogMinutes = 5,
        [int]$BackupMinutes = 60
    )
    $common = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File'
    $scripts = Join-Path $RepoRoot 'scripts\windows'
    $backupArgs = "$common `"$(Join-Path $scripts 'backup-production.ps1')`" -ConfigFile `"$ConfigFile`""
    if ($PassphraseFile) { $backupArgs += " -PassphraseFile `"$PassphraseFile`"" }
    if ($IncludeConfig) { $backupArgs += ' -IncludeConfig' }
    @(
        [pscustomobject]@{
            Name = $script:TaskNames.Backup; Description = 'Hourly encrypted, verified Helpdesk Production backup with required off-host copy and retention.'
            Execute = $PowerShellExe; Arguments = $backupArgs; Trigger = 'Repeat'; RepeatMinutes = $BackupMinutes; DelayMinutes = 10
            TimeLimitMinutes = 45; RunAs = $ServiceUser; LogonType = 'Password'
        }
        [pscustomobject]@{
            Name = $script:TaskNames.Watchdog; Description = 'Independent Helpdesk Production watchdog: checks, deduplicated alerts, recovery notices and heartbeat.'
            Execute = $PowerShellExe; Arguments = "$common `"$(Join-Path $scripts 'watchdog-production.ps1')`" -ConfigFile `"$ConfigFile`""
            Trigger = 'Repeat'; RepeatMinutes = $WatchdogMinutes; DelayMinutes = 2; TimeLimitMinutes = 4; RunAs = $ServiceUser; LogonType = 'Password'
        }
        [pscustomobject]@{
            Name = $script:TaskNames.Recovery; Description = 'At sign-in: start Docker Desktop if needed, then the Production containers in dependency order (never the Cloudflare tunnel).'
            Execute = $PowerShellExe; Arguments = "$common `"$(Join-Path $scripts 'recover-production.ps1')`""
            Trigger = 'AtLogOn'; RepeatMinutes = 0; DelayMinutes = 1; TimeLimitMinutes = 20; RunAs = $DesktopUser; LogonType = 'Interactive'
        }
    )
}

function ConvertTo-ScheduledTaskParts($Def) {
    $action = New-ScheduledTaskAction -Execute $Def.Execute -Argument $Def.Arguments
    if ($Def.Trigger -eq 'Repeat') {
        $trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes($Def.DelayMinutes)) -RepetitionInterval (New-TimeSpan -Minutes $Def.RepeatMinutes)
    } else {
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $Def.RunAs
        $trigger.Delay = 'PT{0}M' -f $Def.DelayMinutes
    }
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes $Def.TimeLimitMinutes) `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    [pscustomobject]@{ Action = $action; Trigger = $trigger; Settings = $settings }
}

function Register-ProductionTasks {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)]$Definitions, [securestring]$ServicePassword, [switch]$Force)
    foreach ($d in $Definitions) {
        if ($PSCmdlet.ShouldProcess($d.Name, "register scheduled task ($(if ($d.Trigger -eq 'Repeat') { "every $($d.RepeatMinutes) min" } else { 'at sign-in' }), as $($d.RunAs), $($d.LogonType))")) {
            if ((Get-ScheduledTask -TaskName $d.Name -ErrorAction SilentlyContinue) -and -not $Force) { throw "task '$($d.Name)' already exists; use -Force to replace it" }
            $p = ConvertTo-ScheduledTaskParts $d
            if ($d.LogonType -eq 'Password') {
                if (-not $ServicePassword) { throw "task '$($d.Name)' needs the service account password" }
                $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ServicePassword)
                try {
                    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
                    Register-ScheduledTask -TaskName $d.Name -Description $d.Description -Action $p.Action -Trigger $p.Trigger -Settings $p.Settings -User $d.RunAs -Password $plain -RunLevel Limited -Force:$Force | Out-Null
                } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $plain = $null }
            } else {
                $principal = New-ScheduledTaskPrincipal -UserId $d.RunAs -LogonType Interactive -RunLevel Limited
                Register-ScheduledTask -TaskName $d.Name -Description $d.Description -Action $p.Action -Trigger $p.Trigger -Settings $p.Settings -Principal $principal -Force:$Force | Out-Null
            }
        }
    }
}

function Unregister-ProductionTasks {
    [CmdletBinding(SupportsShouldProcess)]
    param()
    foreach ($name in (Get-ProductionTaskNames)) {
        if (-not (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)) { Write-Host "no task named '$name'"; continue }
        if ($PSCmdlet.ShouldProcess($name, 'remove scheduled task')) { Unregister-ScheduledTask -TaskName $name -Confirm:$false }
    }
}
