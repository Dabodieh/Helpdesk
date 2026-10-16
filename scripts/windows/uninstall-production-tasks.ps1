<#
.SYNOPSIS
  Removes the three Helpdesk Production scheduled tasks (backup, watchdog, recovery) by exact name. Use -WhatIf first.
.DESCRIPTION
  Removes only those tasks. Backups, retained files, the alert state, the monitor configuration, containers, volumes and Windows settings are untouched.
.EXAMPLE
  .\scripts\windows\uninstall-production-tasks.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\ProductionTasks.ps1')
Unregister-ProductionTasks
