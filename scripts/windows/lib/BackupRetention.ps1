# Backup destinations (local drives and UNC shares) and retention for the Windows Production backup. Requires lib\Monitoring.ps1 (Test-PathWithTimeout).
# Retention only ever touches files whose names match the strict backup pattern below; nothing else in a folder is read for deletion.

# Converts a Windows path to the form Git for Windows' bash understands: C:\x\y -> /c/x/y, \\server\share\x -> //server/share/x.
function ConvertTo-GitBashPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -match '^([A-Za-z]):\\(.*)$') { return '/' + $Matches[1].ToLower() + '/' + ($Matches[2] -replace '\\', '/') }
    return ($full -replace '\\', '/')
}

# The part of a destination that must already exist for the destination to count as available: the drive for C:\..., the share (\\server\share) for a UNC path.
# A destination whose server, share or drive is missing is NOT created implicitly; only a folder below an existing root is.
function Get-DestinationRoot([string]$Path) {
    if ($Path -match '^(\\\\[^\\]+\\[^\\]+)') { return $Matches[1] }
    if ($Path -match '^([A-Za-z]:\\)') { return $Matches[1] }
    return $null
}

function Test-BackupDestination([string]$Path, [int]$TimeoutSec = 20) {
    $root = Get-DestinationRoot $Path
    if (-not $root) { return $false }
    return (Test-PathWithTimeout $root $TimeoutSec)
}

$script:BackupNamePattern = '^(?<prefix>[a-z0-9-]+?)(?<kind>-objects|-config)?-(?<stamp>\d{8}T\d{6}Z)\.(?<ext>dump\.enc|tar\.enc)(?<side>\.sha256|\.meta\.json)?$'

function ConvertFrom-BackupStamp([string]$Stamp) {
    return [datetime]::ParseExact($Stamp, "yyyyMMdd'T'HHmmss'Z'", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal -bor [Globalization.DateTimeStyles]::AdjustToUniversal)
}

# Groups the backup files of one folder into sets. A set is a database dump with its sidecar files plus the object and configuration bundles written right after it
# (within 10 minutes). Files that match no dump form their own set without a dump (never counted as verified).
function Get-BackupSets([string]$Directory, [string]$Prefix = 'helpdesk-prod') {
    $files = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $Directory -File -ErrorAction Stop)) {
        $m = [regex]::Match($f.Name, $script:BackupNamePattern)
        if (-not $m.Success -or $m.Groups['prefix'].Value -ne $Prefix) { continue }
        $files += [pscustomobject]@{ File = $f; Kind = $(if ($m.Groups['kind'].Value) { $m.Groups['kind'].Value.TrimStart('-') } else { 'dump' }); Time = (ConvertFrom-BackupStamp $m.Groups['stamp'].Value); Side = $m.Groups['side'].Value }
    }
    $dumps = @($files | Where-Object { $_.Kind -eq 'dump' -and -not $_.Side } | Sort-Object Time)
    $sets = @{}
    foreach ($d in $dumps) { $sets[$d.File.Name] = [pscustomobject]@{ Time = $d.Time; Dump = $d.File; Files = (New-Object System.Collections.Generic.List[object]) } }
    foreach ($f in $files) {
        $owner = $null
        foreach ($d in ($dumps | Sort-Object Time -Descending)) {
            $gap = ($f.Time - $d.Time).TotalMinutes
            if ($f.Kind -eq 'dump') { if ($f.Time -eq $d.Time) { $owner = $d; break } }
            elseif ($gap -ge 0 -and $gap -le 10) { $owner = $d; break }
        }
        if ($owner) { $sets[$owner.File.Name].Files.Add($f.File) }
        else {
            $k = "orphan-$($f.File.Name)"
            if (-not $sets.ContainsKey($k)) { $sets[$k] = [pscustomobject]@{ Time = $f.Time; Dump = $null; Files = (New-Object System.Collections.Generic.List[object]) } }
            $sets[$k].Files.Add($f.File)
        }
    }
    return @($sets.Values | Sort-Object Time -Descending)
}

# A set counts as a successful backup only when its dump has a checksum file and the checksum matches.
function Test-BackupSetVerified($Set) {
    if (-not $Set.Dump) { return $false }
    $side = "$($Set.Dump.FullName).sha256"
    if (-not (Test-Path -LiteralPath $side)) { return $false }
    try {
        $want = ((Get-Content -LiteralPath $side -Raw) -split '\s+')[0]
        return ((Get-FileHash -LiteralPath $Set.Dump.FullName -Algorithm SHA256).Hash -ieq $want)
    } catch { return $false }
}

# Keeps every set younger than $Days AND the newest $MinSets verified sets; removes only older sets outside both. Never deletes when no verified set exists.
function Invoke-BackupRetention {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Directory,
        [int]$Days = 14,
        [int]$MinSets = 24,
        [datetime]$Now = [datetime]::UtcNow,
        [string]$Prefix = 'helpdesk-prod'
    )
    $sets = @(Get-BackupSets $Directory $Prefix)
    $keepVerified = New-Object System.Collections.Generic.HashSet[object]
    foreach ($s in $sets) {
        if ($keepVerified.Count -ge $MinSets) { break }
        if (Test-BackupSetVerified $s) { [void]$keepVerified.Add($s) }
    }
    $removed = @(); $kept = 0
    if ($keepVerified.Count -eq 0) { return [pscustomobject]@{ Directory = $Directory; Kept = $sets.Count; Removed = @(); Note = 'no verified set found; nothing removed' } }
    foreach ($s in $sets) {
        $young = (($Now - $s.Time).TotalDays -lt $Days)
        if ($young -or $keepVerified.Contains($s)) { $kept++; continue }
        foreach ($f in $s.Files) {
            if ($PSCmdlet.ShouldProcess($f.Name, 'remove expired backup file')) { Remove-Item -LiteralPath $f.FullName -Force }
            $removed += $f.Name
        }
    }
    return [pscustomobject]@{ Directory = $Directory; Kept = $kept; Removed = $removed; Note = '' }
}

# Marks the status file of a finished run as failed (for example when a required off-host copy is missing) while keeping what the run recorded.
function Set-BackupStatusFailed([string]$StatusFile, [string]$Reason, [datetime]$Now = [datetime]::UtcNow) {
    $o = [ordered]@{}
    if (Test-Path -LiteralPath $StatusFile) {
        try { foreach ($p in (Get-Content -LiteralPath $StatusFile -Raw | ConvertFrom-Json).PSObject.Properties) { $o[$p.Name] = $p.Value } } catch { }
    }
    $o['status'] = 'failed'
    $o['error'] = (Protect-LogText $Reason 300)
    $o['finishedAt'] = $Now.ToString('yyyy-MM-ddTHH:mm:ssZ')
    [IO.File]::WriteAllText("$StatusFile.tmp", ($o | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath "$StatusFile.tmp" -Destination $StatusFile -Force
}
