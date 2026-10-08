# Verification and scheduling for the Windows Docker Desktop deployment.
Set-StrictMode -Version 2.0

if (-not ('HdHttp' -as [type])) {
    $source = @'
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
public static class HdHttp {
    public static System.DateTime LastNotAfter = System.DateTime.MinValue;
    public static HttpClientHandler Handler() {
        var h = new HttpClientHandler();
        h.AllowAutoRedirect = false;
        h.ServerCertificateCustomValidationCallback = (m, c, ch, e) => { LastNotAfter = c.NotAfter; return true; };
        return h;
    }
}
'@
    if ($PSVersionTable.PSEdition -eq 'Desktop') { Add-Type -TypeDefinition $source -ReferencedAssemblies 'System.Net.Http' } else { Add-Type -TypeDefinition $source }
}

# Returns @{ Code = <status>; Headers = <header text> } for a request sent to the local PC with the public host name in the Host header
# (the certificate is allowed to be self-signed here: this check talks to this PC only, over the loopback address).
function Get-LocalHttp([string]$Url, [string]$HostHeader) {
    $client = New-Object System.Net.Http.HttpClient((([HdHttp]::Handler())))
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    try {
        $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $Url)
        if ($HostHeader) { $req.Headers.Host = $HostHeader }
        $resp = $client.SendAsync($req).GetAwaiter().GetResult()
        return @{ Code = [int]$resp.StatusCode; Headers = ($resp.Headers.ToString() + $resp.Content.Headers.ToString()) }
    } catch { return @{ Code = 0; Headers = '' } } finally { $client.Dispose() }
}

function Invoke-Verify {
    $s = Read-EnvFile
    $hostName = Get-Setting $s 'PUBLIC_HOST'
    $httpsPort = Get-Setting $s 'HTTPS_PORT' '443'
    $httpPort = Get-Setting $s 'HTTP_PORT' '80'
    $suffix = Get-Setting $s 'PUBLIC_PORT_SUFFIX'
    $hostHeader = "$hostName$suffix"
    $base = "https://127.0.0.1:$httpsPort"
    $script:failures = 0
    function Check([string]$Name, $Expected, $Actual) {
        if ("$Expected" -eq "$Actual") { Write-Host "PASS  $Name" -ForegroundColor Green } else { Write-Host "FAIL  $Name (expected $Expected, got $Actual)" -ForegroundColor Red; $script:failures++ }
    }
    $running = ((Invoke-ComposeText @('ps', '--status', 'running', '--services')).Output -split "`r?`n" | Where-Object { $_ } | Sort-Object) -join ' '
    $expected = if ((Get-Setting $s 'LOCAL_OBJECT_STORE') -eq 'true') { 'helpdesk nginx objectstore postgres' } else { 'helpdesk nginx postgres' }
    Check "containers running ($expected)" $expected $running
    Check 'http redirects to https' 301 (Get-LocalHttp "http://127.0.0.1:$httpPort/" $hostHeader).Code
    $ready = Get-LocalHttp "$base/health/ready" $hostHeader
    Check 'readiness (database, migrations, object store)' 200 $ready.Code
    Check 'liveness' 200 (Get-LocalHttp "$base/health/live" $hostHeader).Code
    Check 'API requires sign-in' 401 (Get-LocalHttp "$base/api/tickets" $hostHeader).Code
    Check 'dev sign-in route absent' 404 (Get-LocalHttp "$base/api/auth/dev-login?subject=x" $hostHeader).Code
    Check 'Hangfire dashboard not anonymous' $false ((Get-LocalHttp "$base/hangfire" $hostHeader).Code -eq 200)
    $root = Get-LocalHttp "$base/" $hostHeader
    Check 'HSTS header present' $true ($root.Headers -match '(?im)^Strict-Transport-Security:')
    Check 'nosniff header present' $true ($root.Headers -match '(?im)^X-Content-Type-Options:\s*nosniff')
    $days = -1
    if ([HdHttp]::LastNotAfter -gt [DateTime]::MinValue) { $days = [int](([HdHttp]::LastNotAfter - [DateTime]::Now).TotalDays) }
    Check 'TLS certificate valid for more than 14 days' $true ($days -gt 14)
    $jobs = (Invoke-ComposeText @('exec', '-T', 'postgres', 'psql', '-U', 'helpdesk', '-d', 'helpdesk', '-tAc', "SELECT count(*) FROM hangfire.set WHERE key='recurring-jobs'")).Output.Trim()
    Check 'background workers registered (5 recurring jobs)' 5 $jobs
    $errs = ([regex]::Matches((Invoke-ComposeText @('logs', '--since', '10m', 'helpdesk')).Output, '\[(ERR|FTL)\]')).Count
    Check 'no application errors in the last 10 minutes' 0 $errs
    $fresh = Test-BackupFresh
    Check "hourly backup fresh ($($fresh.Reason))" $true $fresh.Ok
    if ($script:failures -gt 0) { Stop-Helpdesk "verification FAILED: $($script:failures) check(s)" }
    Write-Host 'verification passed' -ForegroundColor Green
}

function Register-BackupTask([string]$TaskName) {
    $psExe = (Get-Process -Id $PID).Path
    $script = (Resolve-Path (Join-Path $PSScriptRoot '..\helpdesk.ps1')).Path
    $action = New-ScheduledTaskAction -Execute $psExe -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" backup -EnvFile `"$script:EnvFile`""
    $start = (Get-Date).Date.AddHours((Get-Date).Hour + 1)
    $trigger = New-ScheduledTaskTrigger -Once -At $start -RepetitionInterval (New-TimeSpan -Hours 1)
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    # Interactive: runs while this user is signed in, which is also when Docker Desktop runs; the passphrase is protected for this same user (DPAPI).
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Description 'Hourly encrypted Helpdesk database backup (helpdesk.ps1 backup)' -Force | Out-Null
    Write-Host "scheduled task '$TaskName' registered for ${me}: hourly from $($start.ToString('HH:mm')), catches up after sleep or a missed run, runs only while you are signed in."
}

function Unregister-BackupTask([string]$TaskName) {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "scheduled task '$TaskName' removed (backups and the passphrase are untouched)"
    } else { Write-Host "no scheduled task named '$TaskName'" }
}
