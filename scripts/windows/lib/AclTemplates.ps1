# Secure installation layout and narrowly scoped ACL templates for a commercial Helpdesk installation on Windows 11 Pro with Docker Desktop.
# The templates are DATA plus pure verification functions: nothing here changes a permission unless Set-HelpdeskAcl is called on a path inside the layout roots
# (and that function refuses drive roots, network paths, development checkouts, and anything outside the layout). Verification reads ACLs only.
#
# Principles: scripts that privileged scheduled accounts execute are writable by Administrators and SYSTEM only; secrets, configuration, state and backups are not
# readable by ordinary users; the service account gets the least it needs (read for configuration and secrets, modify for state and backups).

$script:Sid = @{
    System = 'S-1-5-18'; Administrators = 'S-1-5-32-544'; Users = 'S-1-5-32-545'; AuthenticatedUsers = 'S-1-5-11'; Everyone = 'S-1-1-0'; CreatorOwner = 'S-1-3-0'
}
$script:OrdinarySids = @($script:Sid.Users, $script:Sid.AuthenticatedUsers, $script:Sid.Everyone)

# Rights bits that let a principal change a file or folder (including generic write/all and the permission-changing rights).
$script:WriteMask = 0x2L -bor 0x4L -bor 0x10L -bor 0x40L -bor 0x100L -bor 0x10000L -bor 0x40000L -bor 0x80000L -bor 0x40000000L -bor 0x10000000L
# Rights bits that let a principal read content or list a folder (including generic read/all).
$script:ReadMask = 0x1L -bor 0x8L -bor 0x20L -bor 0x80000000L -bor 0x10000000L

# ---- layout --------------------------------------------------------------------------------------------------------------------------------------

# Where a commercial installation keeps things. InstallRoot holds the release (scripts, compose files); DataRoot holds configuration, secrets and monitor state;
# BackupRoot holds backups. All three are configurable; the defaults are Program Files and ProgramData.
function Get-HelpdeskLayout {
    param([string]$InstallRoot = (Join-Path $env:ProgramFiles 'Helpdesk'), [string]$DataRoot = (Join-Path $env:ProgramData 'Helpdesk'), [string]$BackupRoot = '')
    if (-not $BackupRoot) { $BackupRoot = Join-Path $DataRoot 'backups' }
    [pscustomobject]@{
        InstallRoot    = $InstallRoot
        Scripts        = Join-Path $InstallRoot 'scripts'
        DataRoot       = $DataRoot
        Config         = Join-Path $DataRoot 'config'
        Secrets        = Join-Path $DataRoot 'secrets'
        State          = Join-Path $DataRoot 'monitor'
        BackupRoot     = $BackupRoot
        ConfigFile     = Join-Path (Join-Path $DataRoot 'config') 'monitor.env'
        PassphraseFile = Join-Path (Join-Path $DataRoot 'secrets') 'backup.pass'
        TokenFile      = Join-Path (Join-Path $DataRoot 'secrets') 'alert.token'
        RestoreResult  = Join-Path (Join-Path $DataRoot 'monitor') 'restore-test-result.json'
    }
}

# ---- templates ------------------------------------------------------------------------------------------------------------------------------------

# Kinds: Program (scripts, readable by everyone, writable by administrators only), Config and Secrets (service account reads), State and Backups (service account modifies).
# $ServiceAccount is an account name or a SID string.
function Get-HelpdeskAclTemplate {
    param([Parameter(Mandatory)][ValidateSet('Program', 'Config', 'Secrets', 'State', 'Backups')][string]$Kind, [Parameter(Mandatory)][string]$ServiceAccount)
    $ci = 'ContainerInherit, ObjectInherit'
    $entries = @(
        @{ Id = $script:Sid.System; Rights = 'FullControl'; Inherit = $ci },
        @{ Id = $script:Sid.Administrators; Rights = 'FullControl'; Inherit = $ci }
    )
    switch ($Kind) {
        'Program' { $entries += @{ Id = $ServiceAccount; Rights = 'ReadAndExecute'; Inherit = $ci }; $entries += @{ Id = $script:Sid.Users; Rights = 'ReadAndExecute'; Inherit = $ci }; $readableByUsers = $true }
        'Config'  { $entries += @{ Id = $ServiceAccount; Rights = 'Read'; Inherit = $ci }; $readableByUsers = $false }
        'Secrets' { $entries += @{ Id = $ServiceAccount; Rights = 'Read'; Inherit = $ci }; $readableByUsers = $false }
        'State'   { $entries += @{ Id = $ServiceAccount; Rights = 'Modify'; Inherit = $ci }; $readableByUsers = $false }
        'Backups' { $entries += @{ Id = $ServiceAccount; Rights = 'Modify'; Inherit = $ci }; $readableByUsers = $false }
    }
    [pscustomobject]@{ Kind = $Kind; ServiceAccount = $ServiceAccount; Entries = $entries; ProtectedInheritance = $true; UsersMayRead = $readableByUsers }
}

function ConvertTo-IdentitySid([string]$Id) {
    if ($Id -match '^S-\d-\d+(-\d+)+$') { return New-Object Security.Principal.SecurityIdentifier($Id) }
    return (New-Object Security.Principal.NTAccount($Id)).Translate([Security.Principal.SecurityIdentifier])
}

# The security descriptor the template describes, in memory only (nothing is applied).
function New-AclFromTemplate($Template) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($e in $Template.Entries) {
        $sid = ConvertTo-IdentitySid $e.Id
        $rights = [Security.AccessControl.FileSystemRights]$e.Rights
        $inh = [Security.AccessControl.InheritanceFlags]$e.Inherit
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, $rights, $inh, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)))
    }
    return $acl
}

# The icacls commands that realise a template, for an administrator to review and run. Printed, never executed here.
function Get-IcaclsCommands($Template, [string]$Path) {
    $map = @{ FullControl = 'F'; Modify = 'M'; ReadAndExecute = 'RX'; Read = 'R' }
    $cmds = @("icacls `"$Path`" /inheritance:r")
    foreach ($e in $Template.Entries) {
        $who = if ($e.Id -match '^S-\d-') { "*$($e.Id)" } else { $e.Id }
        $cmds += "icacls `"$Path`" /grant:r `"${who}:(OI)(CI)$($map[$e.Rights])`""
    }
    return $cmds
}

# ---- reading and judging ACLs ---------------------------------------------------------------------------------------------------------------------

# FileSystemRights as an unsigned 32-bit value (generic bits appear as negative numbers in the enum).
function Get-RightsBits($Rights) { return [int64][BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$Rights), 0) }

function Get-PathAcl([string]$Path) { return (Get-Acl -LiteralPath $Path) }

function Get-RuleSidValue($Rule) {
    try { return $Rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { return [string]$Rule.IdentityReference.Value }
}

# SIDs of ordinary-user groups (Users, Authenticated Users, Everyone) that hold the given kind of access through an Allow rule.
function Get-OrdinaryUserGrants($Acl, [ValidateSet('Write', 'Read')][string]$Access) {
    $mask = if ($Access -eq 'Write') { $script:WriteMask } else { $script:ReadMask }
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($r in $Acl.Access) {
        if ($r.AccessControlType -ne 'Allow') { continue }
        $sid = Get-RuleSidValue $r
        if ($script:OrdinarySids -notcontains $sid) { continue }
        if (((Get-RightsBits $r.FileSystemRights) -band $mask) -ne 0) { $found.Add($sid) }
    }
    return @($found | Select-Object -Unique)
}

function Get-OrdinaryUserWriteGrants($Acl) { return @(Get-OrdinaryUserGrants $Acl 'Write') }

# Compares an ACL with a template. Returns issue objects { Severity = FAIL|WARN; Message }. An empty result means the ACL matches the template.
function Test-AclAgainstTemplate($Acl, $Template) {
    $issues = New-Object System.Collections.Generic.List[object]
    $add = { param($sev, $msg) $issues.Add([pscustomobject]@{ Severity = $sev; Message = $msg }) }
    if ($Template.ProtectedInheritance -and -not $Acl.AreAccessRulesProtected) { & $add 'FAIL' 'inheritance is not disabled, so permissions come from the parent folder' }
    $expected = @{}
    foreach ($e in $Template.Entries) { $expected[(ConvertTo-IdentitySid $e.Id).Value] = Get-RightsBits ([Security.AccessControl.FileSystemRights]$e.Rights) }
    $present = @{}
    foreach ($r in $Acl.Access) {
        if ($r.AccessControlType -ne 'Allow') { continue }
        $sid = Get-RuleSidValue $r
        if ($sid -eq $script:Sid.CreatorOwner) { continue }
        if ($r.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
        $bits = Get-RightsBits $r.FileSystemRights
        if ($present.ContainsKey($sid)) { $present[$sid] = $present[$sid] -bor $bits } else { $present[$sid] = $bits }
    }
    foreach ($sid in $expected.Keys) {
        $want = $expected[$sid]
        if (-not $present.ContainsKey($sid)) { & $add 'FAIL' "missing the required entry for $sid"; continue }
        if (($present[$sid] -band $want) -ne $want) { & $add 'FAIL' "the entry for $sid has fewer rights than the template requires" }
        if ((($present[$sid] -band $script:WriteMask) -ne 0) -and (($want -band $script:WriteMask) -eq 0)) { & $add 'FAIL' "$sid can change content but the template allows read only" }
    }
    foreach ($sid in $present.Keys) {
        if ($expected.ContainsKey($sid)) { continue }
        if ($script:OrdinarySids -contains $sid) {
            if (($present[$sid] -band $script:WriteMask) -ne 0) { & $add 'FAIL' "ordinary users ($sid) can change content" }
            elseif (($present[$sid] -band $script:ReadMask) -ne 0 -and -not $Template.UsersMayRead) { & $add 'FAIL' "ordinary users ($sid) can read this location" }
        } else { & $add 'WARN' "unexpected entry for $sid" }
    }
    return $issues.ToArray()
}

function Test-PathAgainstTemplate([string]$Path, $Template) { return @(Test-AclAgainstTemplate (Get-PathAcl $Path) $Template) }

# Every folder and script file of the tree that scheduled tasks execute: anything an ordinary user could change is a way to run code as the service account.
function Get-ScriptTreeWriteIssues([string]$ScriptRoot) {
    $issues = New-Object System.Collections.Generic.List[string]
    $items = @(Get-Item -LiteralPath $ScriptRoot) + @(Get-ChildItem -LiteralPath $ScriptRoot -Recurse -Include '*.ps1', '*.sh', '*.sql' -File -ErrorAction SilentlyContinue) + @(Get-ChildItem -LiteralPath $ScriptRoot -Recurse -Directory -ErrorAction SilentlyContinue)
    $seen = @{}
    foreach ($i in $items) {
        if ($seen.ContainsKey($i.FullName)) { continue }
        $seen[$i.FullName] = $true
        $g = @(Get-OrdinaryUserWriteGrants (Get-PathAcl $i.FullName))
        if ($g.Count -gt 0) { $issues.Add($i.FullName.Substring([math]::Min($ScriptRoot.Length, $i.FullName.Length)).TrimStart('\')) }
    }
    return $issues.ToArray()
}

# ---- applying (guarded) ---------------------------------------------------------------------------------------------------------------------------

function Assert-AclTargetAllowed([string]$Path, $Layout) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full.StartsWith('\\')) { throw 'refusing: network paths are never changed by this tool' }
    if ($full -match '^[A-Za-z]:$') { throw 'refusing: a drive root is never changed' }
    $roots = @($Layout.InstallRoot, $Layout.DataRoot, $Layout.BackupRoot) | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }
    foreach ($r in $roots) { if ($r -match '^[A-Za-z]:$') { throw 'refusing: a layout root must not be a drive root' } }
    $inside = $false
    foreach ($r in $roots) { if ($full -ieq $r -or $full.StartsWith($r + '\', [StringComparison]::OrdinalIgnoreCase)) { $inside = $true } }
    if (-not $inside) { throw 'refusing: the path is outside the Helpdesk installation layout' }
    $probe = $full
    while ($probe -and $probe -notmatch '^[A-Za-z]:\\?$') {
        if (Test-Path -LiteralPath (Join-Path $probe '.git')) { throw 'refusing: this is inside a development checkout (a git working tree)' }
        $probe = Split-Path $probe -Parent
    }
}

# Replaces the permissions of one layout folder with the template (inheritance off, only the template entries). Needs an elevated session; -WhatIf shows the commands.
function Set-HelpdeskAcl {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][ValidateSet('Program', 'Config', 'Secrets', 'State', 'Backups')][string]$Kind,
          [Parameter(Mandatory)][string]$ServiceAccount, [Parameter(Mandatory)]$Layout)
    Assert-AclTargetAllowed $Path $Layout
    $t = Get-HelpdeskAclTemplate -Kind $Kind -ServiceAccount $ServiceAccount
    if (-not $PSCmdlet.ShouldProcess($Path, "replace permissions with the $Kind template")) { return }
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($r in @($acl.Access)) { [void]$acl.RemoveAccessRuleAll($r) }
    $template = New-AclFromTemplate $t
    foreach ($r in $template.Access) { $acl.AddAccessRule($r) }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
