# Independent monitoring for the Windows Docker Desktop Production stack: configuration, bounded external calls, log redaction, alert state machine
# (deduplication, reminders, recovery notices) and the webhook / heartbeat senders. Windows PowerShell 5.1 and PowerShell 7 compatible.
# Nothing here reads, prints or stores application secrets. Alert text is built from fixed templates and always passes through Protect-LogText.

# ---- configuration ---------------------------------------------------------------------------------------------------------------------------------

function Read-MonitorConfig([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "monitor configuration file not found: $Path" }
    $m = @{}
    foreach ($l in Get-Content -LiteralPath $Path) {
        if ($l -match '^\s*#') { continue }
        if ($l -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
            $v = $Matches[2].Trim()
            if ($v.Length -ge 2 -and (($v.StartsWith('"') -and $v.EndsWith('"')) -or ($v.StartsWith("'") -and $v.EndsWith("'")))) { $v = $v.Substring(1, $v.Length - 2) }
            $m[$Matches[1]] = $v
        }
    }
    return $m
}

function Split-ConfigList([string]$Value) {
    if (-not $Value) { return @() }
    return @($Value -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function ConvertTo-ConfigInt($Config, [string]$Key, [int]$Default) {
    if (-not $Config.ContainsKey($Key) -or -not $Config[$Key]) { return $Default }
    $n = 0
    if ([int]::TryParse($Config[$Key], [ref]$n)) { return $n }
    return -1
}

# Typed settings with the agreed defaults (backup older than 2 h, mail sync stale after 15 min, two consecutive failed 5-minute runs = within 10 minutes).
function Get-MonitorSettings($Config) {
    $stateDir = if ($Config['MONITOR_STATE_DIR']) { $Config['MONITOR_STATE_DIR'] } else { Join-Path $env:ProgramData 'Helpdesk\monitor' }
    $warn = @(Split-ConfigList $Config['EXPIRY_WARN_DAYS'] | ForEach-Object { [int]$_ })
    if ($warn.Count -eq 0) { $warn = @(30, 14, 7) }
    [pscustomobject]@{
        AlertUrl               = $Config['ALERT_WEBHOOK_URL']
        AlertFormat            = $(if ($Config['ALERT_WEBHOOK_FORMAT']) { $Config['ALERT_WEBHOOK_FORMAT'].ToLowerInvariant() } else { 'ntfy' })
        AlertTokenFile         = $Config['ALERT_WEBHOOK_TOKEN_FILE']
        HeartbeatUrl           = $Config['HEARTBEAT_URL']
        AllowLoopbackHttp      = ($Config['MONITOR_TEST_ALLOW_LOOPBACK_HTTP'] -eq '1')
        StateDir               = $stateDir
        BackupDir              = $Config['BACKUP_DIR']
        PassphraseFile         = $Config['BACKUP_PASSPHRASE_FILE']
        RequiredBackupDirs     = @(Split-ConfigList $Config['BACKUP_REQUIRED_DIRS'])
        OptionalBackupDirs     = @(Split-ConfigList $Config['BACKUP_COPY_DIRS'])
        PruneDestinations      = ($Config['BACKUP_PRUNE_DESTINATIONS'] -eq 'true')
        BackupMaxAgeMinutes    = ConvertTo-ConfigInt $Config 'MONITOR_BACKUP_MAX_AGE_MINUTES' 120
        MailSyncStaleMinutes   = ConvertTo-ConfigInt $Config 'MONITOR_MAIL_SYNC_STALE_MINUTES' 15
        ConfirmRuns            = ConvertTo-ConfigInt $Config 'MONITOR_CONFIRM_RUNS' 2
        ReminderMinutes        = ConvertTo-ConfigInt $Config 'MONITOR_REMINDER_MINUTES' 360
        CommandTimeoutSec      = ConvertTo-ConfigInt $Config 'MONITOR_COMMAND_TIMEOUT_SECONDS' 20
        BootGraceMinutes       = ConvertTo-ConfigInt $Config 'MONITOR_BOOT_GRACE_MINUTES' 10
        MinFreeDiskGb          = ConvertTo-ConfigInt $Config 'MONITOR_MIN_FREE_DISK_GB' 10
        PgContainer            = $(if ($Config['PG_CONTAINER']) { $Config['PG_CONTAINER'] } else { 'helpdesk-postgres-prod-1' })
        AppContainer           = $(if ($Config['APP_CONTAINER']) { $Config['APP_CONTAINER'] } else { 'helpdesk-backend' })
        ObjectContainer        = $(if ($Config['OBJECTSTORE_CONTAINER']) { $Config['OBJECTSTORE_CONTAINER'] } else { 'helpdesk-objectstore-prod-1' })
        EntraSecretExpires     = $Config['ENTRA_SECRET_EXPIRES']
        CertFiles              = @(Split-ConfigList $Config['CERT_EXPIRY_FILES'])
        ExpiryWarnDays         = $warn
    }
}

function Test-WebhookUrl([string]$Url, [bool]$AllowLoopbackHttp = $false) {
    $u = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$u)) { return $false }
    if ($u.Scheme -eq 'https') { return $true }
    return ($AllowLoopbackHttp -and $u.Scheme -eq 'http' -and ($u.Host -eq '127.0.0.1' -or $u.Host -eq 'localhost'))
}

# Names of invalid settings (never values). Empty = usable.
function Test-MonitorSettings($S) {
    $p = New-Object System.Collections.Generic.List[string]
    if (-not $S.BackupDir) { $p.Add('BACKUP_DIR is required') }
    if ($S.AlertUrl -and -not (Test-WebhookUrl $S.AlertUrl $S.AllowLoopbackHttp)) { $p.Add('ALERT_WEBHOOK_URL must be an https URL') }
    if ($S.HeartbeatUrl -and -not (Test-WebhookUrl $S.HeartbeatUrl $S.AllowLoopbackHttp)) { $p.Add('HEARTBEAT_URL must be an https URL') }
    if ($S.AlertFormat -notin 'ntfy', 'json') { $p.Add('ALERT_WEBHOOK_FORMAT must be ntfy or json') }
    foreach ($k in 'BackupMaxAgeMinutes', 'MailSyncStaleMinutes', 'ConfirmRuns', 'ReminderMinutes', 'CommandTimeoutSec', 'BootGraceMinutes', 'MinFreeDiskGb') {
        if ($S.$k -lt 1) { $p.Add("$k must be a positive integer") }
    }
    if ($S.EntraSecretExpires) {
        $d = [datetime]::MinValue
        if (-not [datetime]::TryParseExact($S.EntraSecretExpires, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$d)) { $p.Add('ENTRA_SECRET_EXPIRES must be yyyy-MM-dd') }
    }
    return $p.ToArray()
}

# ---- redaction and log ----------------------------------------------------------------------------------------------------------------------------

# Everything that reaches a log, an alert or the console is passed through here: URLs, credentials, long tokens/hashes and e-mail addresses are removed.
function Protect-LogText([string]$Text, [int]$Max = 300) {
    if (-not $Text) { return '' }
    $t = $Text -replace '(?i)https?://[^\s"''<>]+', '<url>'
    $t = $t -replace '(?i)bearer\s+\S+', 'bearer <redacted>'
    $t = $t -replace '(?i)(password|passwd|pwd|secret|token|authorization|apikey|api_key)\s*[=:]\s*\S+', '$1=<redacted>'
    $t = $t -replace '[\w.+-]+@[\w-]+\.[\w.-]+', '<email>'
    $t = $t -replace '[A-Za-z0-9+/_-]{32,}={0,2}', '<redacted>'
    $t = ($t -replace '\s+', ' ').Trim()
    if ($t.Length -gt $Max) { $t = $t.Substring(0, $Max) }
    return $t
}

function Write-MonitorLog([string]$Dir, [string]$Message, [datetime]$Now = [datetime]::UtcNow) {
    try {
        if (-not (Test-Path -LiteralPath $Dir)) { New-Item -ItemType Directory -Force -Path $Dir | Out-Null }
        $file = Join-Path $Dir 'watchdog.log'
        if ((Test-Path -LiteralPath $file) -and (Get-Item -LiteralPath $file).Length -gt 1MB) {
            foreach ($i in 2, 1) {
                $from = if ($i -eq 1) { $file } else { "$file.$($i - 1)" }
                if (Test-Path -LiteralPath $from) { Move-Item -LiteralPath $from -Destination "$file.$i" -Force }
            }
        }
        Add-Content -LiteralPath $file -Value ('{0:yyyy-MM-ddTHH:mm:ssZ} {1}' -f $Now, (Protect-LogText $Message 500))
    } catch { }
}

# ---- bounded external calls -----------------------------------------------------------------------------------------------------------------------

function ConvertTo-ProcessArgument([string]$Value) {
    if ($Value -eq '') { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

# Runs a program with a hard time limit; a hung docker.exe is killed and reported as TimedOut. Input (for example a SQL script) goes through stdin so no quoting problems arise.
function Invoke-ExternalWithTimeout([string]$FilePath, [string[]]$Arguments = @(), [int]$TimeoutSec = 20, [string]$StdIn = '') {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (@($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ })) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    $psi.CreateNoWindow = $true
    try { $p = [System.Diagnostics.Process]::Start($psi) }
    catch { return [pscustomobject]@{ ExitCode = -2; Output = ''; Error = 'could not start the program'; TimedOut = $false } }
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    try { if ($StdIn) { $p.StandardInput.Write($StdIn) }; $p.StandardInput.Close() } catch { }
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        try { $p.Kill() } catch { }
        return [pscustomobject]@{ ExitCode = -1; Output = ''; Error = ''; TimedOut = $true }
    }
    $p.WaitForExit()
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $out.Result; Error = $err.Result; TimedOut = $false }
}

# Runs a script block in its own runspace with a time limit (UNC paths can block for a long time when a server is gone). A blocked call is abandoned, not waited for.
function Invoke-WithTimeout([scriptblock]$ScriptBlock, [object[]]$ArgumentList = @(), [int]$TimeoutSec = 20) {
    $ps = [powershell]::Create()
    try {
        [void]$ps.AddScript($ScriptBlock.ToString())
        foreach ($a in $ArgumentList) { [void]$ps.AddArgument($a) }
        $h = $ps.BeginInvoke()
        if (-not $h.AsyncWaitHandle.WaitOne($TimeoutSec * 1000)) {
            try { [void]$ps.BeginStop($null, $null) } catch { }
            return [pscustomobject]@{ TimedOut = $true; Result = $null }
        }
        $r = $ps.EndInvoke($h)
        return [pscustomobject]@{ TimedOut = $false; Result = $(if ($r.Count -gt 0) { $r[0] } else { $null }) }
    } catch { return [pscustomobject]@{ TimedOut = $false; Result = $null } }
    finally { if ($ps.InvocationStateInfo.State -ne 'Stopping') { $ps.Dispose() } }
}

function Test-PathWithTimeout([string]$Path, [int]$TimeoutSec = 20) {
    $r = Invoke-WithTimeout { param($p) Test-Path -LiteralPath $p } @($Path) $TimeoutSec
    if ($r.TimedOut) { return $false }
    return ($r.Result -eq $true)
}

# ---- alert state machine --------------------------------------------------------------------------------------------------------------------------
# A result is { Key, Name, Status = OK|FAIL|SKIP, Detail, Level, Immediate }. A FAIL must repeat for ConfirmRuns consecutive runs (Immediate results fire at once)
# before the first notification; it is then repeated only as a reminder (ReminderMinutes) or when its Level changes (for example an expiry moving from 30 to 14 days).
# Recovery sends exactly one RESOLVED notice. SKIP (for example a dependent check while Docker is down) never changes the state.

function New-CheckResult([string]$Key, [string]$Name, [string]$Status, [string]$Detail = '', [string]$Level = '', [bool]$Immediate = $false) {
    [pscustomobject]@{ Key = $Key; Name = $Name; Status = $Status; Detail = $Detail; Level = $Level; Immediate = $Immediate }
}

function Update-AlertState($State, $Results, [datetime]$Now, [int]$ConfirmRuns = 2, [int]$ReminderMinutes = 360) {
    $events = New-Object System.Collections.Generic.List[object]
    foreach ($r in $Results) {
        if ($r.Status -eq 'SKIP') { continue }
        $has = $State.ContainsKey($r.Key)
        $s = if ($has) { $State[$r.Key] } else { @{ Fails = 0; Firing = $false; Since = $null; Level = ''; LastNotified = $null } }
        if ($r.Status -eq 'FAIL') {
            $s.Fails = [int]$s.Fails + 1
            $need = if ($r.Immediate) { 1 } else { $ConfirmRuns }
            if (-not $s.Firing) {
                if ($s.Fails -ge $need) {
                    $s.Firing = $true; $s.Since = $Now.ToString('o'); $s.Level = $r.Level; $s.LastNotified = $Now.ToString('o')
                    $events.Add([pscustomobject]@{ Type = 'FIRING'; Key = $r.Key; Name = $r.Name; Detail = $r.Detail; Level = $r.Level; Since = $s.Since; Previous = $null })
                }
            } else {
                $last = [datetime]::Parse($s.LastNotified, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
                if ($r.Level -ne $s.Level) {
                    $prev = $s.LastNotified; $prevLevel = $s.Level
                    $s.Level = $r.Level; $s.LastNotified = $Now.ToString('o')
                    $events.Add([pscustomobject]@{ Type = 'FIRING'; Key = $r.Key; Name = $r.Name; Detail = $r.Detail; Level = $r.Level; Since = $s.Since; Previous = @{ LastNotified = $prev; Level = $prevLevel } })
                } elseif (($Now - $last).TotalMinutes -ge $ReminderMinutes) {
                    $prev = $s.LastNotified
                    $s.LastNotified = $Now.ToString('o')
                    $events.Add([pscustomobject]@{ Type = 'REMINDER'; Key = $r.Key; Name = $r.Name; Detail = $r.Detail; Level = $r.Level; Since = $s.Since; Previous = @{ LastNotified = $prev; Level = $s.Level } })
                }
            }
            $State[$r.Key] = $s
        } else {
            if ($has -and $s.Firing) {
                $events.Add([pscustomobject]@{ Type = 'RESOLVED'; Key = $r.Key; Name = $r.Name; Detail = $r.Detail; Level = $s.Level; Since = $s.Since; Previous = @{ Snapshot = $s } })
            }
            if ($has) { $State.Remove($r.Key) }
        }
    }
    return $events.ToArray()
}

# A notification that could not be delivered must be tried again on the next run: put the state back as it was before the event.
function Undo-AlertEvent($State, $Event) {
    switch ($Event.Type) {
        'FIRING' {
            if ($null -eq $Event.Previous) { $s = $State[$Event.Key]; if ($s) { $s.Firing = $false; $s.Since = $null; $s.LastNotified = $null; $s.Level = '' } }
            else { $s = $State[$Event.Key]; if ($s) { $s.LastNotified = $Event.Previous.LastNotified; $s.Level = $Event.Previous.Level } }
        }
        'REMINDER' { $s = $State[$Event.Key]; if ($s) { $s.LastNotified = $Event.Previous.LastNotified } }
        'RESOLVED' { $State[$Event.Key] = $Event.Previous.Snapshot }
    }
}

function Read-AlertState([string]$Dir) {
    $file = Join-Path $Dir 'alert-state.json'
    $state = @{}
    if (-not (Test-Path -LiteralPath $file)) { return $state }
    try {
        $o = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        foreach ($p in $o.PSObject.Properties) {
            $state[$p.Name] = @{ Fails = [int]$p.Value.Fails; Firing = [bool]$p.Value.Firing; Since = $p.Value.Since; Level = [string]$p.Value.Level; LastNotified = $p.Value.LastNotified }
        }
    } catch { }
    return $state
}

function Save-AlertState([string]$Dir, $State) {
    if (-not (Test-Path -LiteralPath $Dir)) { New-Item -ItemType Directory -Force -Path $Dir | Out-Null }
    $file = Join-Path $Dir 'alert-state.json'
    $text = if ($State.Count -eq 0) { '{}' } else { $State | ConvertTo-Json -Depth 5 }
    [IO.File]::WriteAllText("$file.tmp", $text, (New-Object Text.UTF8Encoding $false))
    Move-Item -LiteralPath "$file.tmp" -Destination $file -Force
}

# ---- message and delivery -------------------------------------------------------------------------------------------------------------------------

function New-AlertMessage($Event, [string]$ComputerName = $env:COMPUTERNAME) {
    $urgent = ($Event.Level -in '7', 'expired')
    $title = switch ($Event.Type) {
        'FIRING'   { "Helpdesk ALERT: $($Event.Name)" }
        'REMINDER' { "Helpdesk still failing: $($Event.Name)" }
        'RESOLVED' { "Helpdesk recovered: $($Event.Name)" }
        default    { "Helpdesk: $($Event.Name)" }
    }
    $priority = if ($Event.Type -eq 'RESOLVED') { 'default' } elseif ($urgent) { 'urgent' } else { 'high' }
    $tags = switch ($Event.Type) { 'RESOLVED' { 'white_check_mark' } 'REMINDER' { 'warning' } default { 'rotating_light' } }
    $body = '{0} on {1}. {2}' -f $Event.Type, $ComputerName, (Protect-LogText $Event.Detail 280)
    if ($Event.Since) { $body += " Since {0}Z." -f ([datetime]::Parse($Event.Since, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) }
    [pscustomobject]@{ Title = (Protect-LogText $title 120); Body = $body; Priority = $priority; Tags = $tags; Type = $Event.Type; Key = $Event.Key }
}

function Enable-Tls12 {
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
}

# Webhook transports: ntfy (POST text, Title/Priority/Tags headers, optional bearer token) or json. The URL and token are never logged; failures return $false.
function Send-WebhookAlert($Message, [string]$Url, [string]$Format = 'ntfy', [string]$TokenFile = '', [int]$TimeoutSec = 15, [bool]$AllowLoopbackHttp = $false) {
    if (-not (Test-WebhookUrl $Url $AllowLoopbackHttp)) { return $false }
    Enable-Tls12
    try {
        $headers = @{}
        if ($TokenFile) {
            $token = (Get-Content -LiteralPath $TokenFile -Raw).Trim()
            if ($token) { $headers['Authorization'] = "Bearer $token" }
        }
        if ($Format -eq 'json') {
            $payload = @{ title = $Message.Title; message = $Message.Body; priority = $Message.Priority; tags = @($Message.Tags); status = $Message.Type; key = $Message.Key; host = $env:COMPUTERNAME } | ConvertTo-Json -Compress
            $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
            $null = Invoke-WebRequest -Uri $Url -Method Post -Body $bytes -ContentType 'application/json; charset=utf-8' -Headers $headers -UseBasicParsing -TimeoutSec $TimeoutSec
        } else {
            $headers['Title'] = ($Message.Title -replace '[^\x20-\x7E]', '?')
            $headers['Priority'] = $Message.Priority
            $headers['Tags'] = $Message.Tags
            $bytes = [Text.Encoding]::UTF8.GetBytes($Message.Body)
            $null = Invoke-WebRequest -Uri $Url -Method Post -Body $bytes -ContentType 'text/plain; charset=utf-8' -Headers $headers -UseBasicParsing -TimeoutSec $TimeoutSec
        }
        return $true
    } catch { return $false }
}

# Dead-man heartbeat (Healthchecks-compatible: a plain GET of the check URL). A missing ping, not a failing one, is what raises the external alarm.
function Send-Heartbeat([string]$Url, [int]$TimeoutSec = 10, [bool]$AllowLoopbackHttp = $false) {
    if (-not $Url -or -not (Test-WebhookUrl $Url $AllowLoopbackHttp)) { return $false }
    Enable-Tls12
    try { $null = Invoke-WebRequest -Uri $Url -Method Get -UseBasicParsing -TimeoutSec $TimeoutSec; return $true } catch { return $false }
}

# ---- expiry ----------------------------------------------------------------------------------------------------------------------------------------

# Tier for a number of days left: 'expired', then the smallest warning threshold that applies (7, 14, 30), or '' when no warning is due.
function Get-ExpiryLevel([double]$DaysLeft, [int[]]$WarnDays = @(30, 14, 7)) {
    if ($DaysLeft -lt 0) { return 'expired' }
    $hit = @($WarnDays | Where-Object { $DaysLeft -le $_ } | Sort-Object)
    if ($hit.Count -eq 0) { return '' }
    return [string]$hit[0]
}

# Credentials expiring: reads only the certificate dates. '<file>|<password-file>' for a PFX; a .cer/.crt needs no password. Nothing about the key or password is kept.
function Get-CertificateExpiry([string]$Spec) {
    $parts = $Spec -split '\|', 2
    $path = $parts[0]
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet
    try { $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]32 } catch { }   # EphemeralKeySet: no key is written to the profile
    if ($parts.Count -gt 1 -and $parts[1]) {
        $pw = (Get-Content -LiteralPath $parts[1] -Raw).Trim()
        $c = New-Object Security.Cryptography.X509Certificates.X509Certificate2($path, $pw, $flags)
    } else {
        $c = New-Object Security.Cryptography.X509Certificates.X509Certificate2($path)
    }
    try { return $c.NotAfter.ToUniversalTime() } finally { $c.Dispose() }
}

# ---- secret file permissions -----------------------------------------------------------------------------------------------------------------------

# True when a file can be read by broad groups (Everyone, Users, Authenticated Users). Used to warn about the passphrase, token and configuration files.
function Test-BroadlyReadable([string]$Path) {
    $broad = @('Everyone', 'BUILTIN\Users', 'NT AUTHORITY\Authenticated Users')
    foreach ($rule in (Get-Acl -LiteralPath $Path).Access) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        if ($broad -notcontains $rule.IdentityReference.Value) { continue }
        if (($rule.FileSystemRights -band ([Security.AccessControl.FileSystemRights]::ReadData -bor [Security.AccessControl.FileSystemRights]::Read)) -ne 0) { return $true }
    }
    return $false
}
