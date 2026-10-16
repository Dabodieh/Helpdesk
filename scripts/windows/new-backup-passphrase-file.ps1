<#
.SYNOPSIS
  Writes the backup passphrase file in the exact format the backup scripts need (UTF-8, no byte-order mark, no line ending). Never overwrites, never generates a passphrase.
.DESCRIPTION
  The passphrase is typed twice at a hidden prompt. Use the SAME passphrase as the existing backups if they must stay restorable with this file; otherwise the new file
  protects only new backups. Afterwards restrict the file with the Secrets ACL template (docs\architecture\WINDOWS_INSTALL_LAYOUT.md) and run the activation preflight.
  Editing the file in Notepad or with Set-Content is NOT safe: they add a byte-order mark or a carriage return, which silently changes the encryption key.
.EXAMPLE
  .\scripts\windows\new-backup-passphrase-file.ps1 -Path C:\ProgramData\Helpdesk\secrets\backup.pass -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Preflight.ps1')
if (Test-Path -LiteralPath $Path) { throw 'refusing to overwrite an existing passphrase file' }
if (-not $PSCmdlet.ShouldProcess($Path, 'write a new backup passphrase file')) { Write-Host 'WhatIf: nothing was written and no passphrase was requested.'; return }
$a = Read-Host -AsSecureString -Prompt 'Backup passphrase'
$b = Read-Host -AsSecureString -Prompt 'Repeat the passphrase'
$pa = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($a); $pb = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($b)
try { $same = ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pa) -ceq [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pb)) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pa); [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pb) }
if (-not $same) { throw 'the two entries differ; nothing was written' }
if ($a.Length -lt 16) { throw 'the passphrase is shorter than 16 characters; nothing was written' }
New-PassphraseFileFromSecureString -Path $Path -Passphrase $a
Write-Host 'Passphrase file written (content not shown). Restrict its permissions next.'
