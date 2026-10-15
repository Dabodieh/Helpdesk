<#
.SYNOPSIS
  Creates the dedicated Helpdesk test resources in the personal test tenant (checklist K2). Idempotent; refuses any other tenant.
.DESCRIPTION
  Without -Execute it only reads and prints what it would create. With -Execute it creates, skipping anything that already exists:
    Entra apps "Helpdesk sign-in" (redirect https://helpdesk.amb-it.co.uk/api/auth/signin-oidc, 30-day secret, no API permissions) and
    "Helpdesk mail" (certificate graph-public.cer, no Graph application permissions, no consent);
    cloud-only unlicensed users helpdesk-test-admin / helpdesk-test-employee (no directory roles);
    shared mailboxes helpdesk-test-a / -b (CustomAttribute1=HelpdeskTest) and helpdesk-test-outside (control, no attribute);
    Exchange RBAC for Applications: scope HelpdeskTestMailboxes + roles Application Mail.ReadWrite and Application Mail.Send.
  Existing users, apps and mailboxes are never modified. Secrets (client secret, user passwords) are written only to git-ignored files
  and never printed: .env (ENTRA_*, GRAPH_*, BOOTSTRAP_PLATFORM_ADMIN_SUBJECT) and %USERPROFILE%\HelpdeskSecrets\m365-test-users.txt.
  Sign-in is interactive (browser). Run as a Global Administrator of the test tenant (-Admin).
#>
param(
    [switch]$Execute,
    [string]$TenantId = '',
    [string]$Domain = 'amb-it.co.uk',
    [string]$Admin = '',
    [string]$CertCer = (Join-Path $env:USERPROFILE 'HelpdeskSecrets\production\graph-public.cer')
)
$ErrorActionPreference = 'Stop'
# Exchange Online cmdlets report most failures as NON-terminating errors; make every cmdlet terminating (explicit -ErrorAction on lookups still wins).
$PSDefaultParameterValues['*:ErrorAction'] = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$envFile = Join-Path $repo '.env'
if (-not $TenantId) {
    $line = Get-Content $envFile -ErrorAction SilentlyContinue | Where-Object { $_ -match '^ENTRA_TENANT_ID=' } | Select-Object -First 1
    if ($line) { $TenantId = ($line -split '=', 2)[1].Trim() }
}
if (-not $TenantId) { throw 'give -TenantId (or set ENTRA_TENANT_ID in .env)' }
if (-not $Admin) { throw 'give -Admin (the sign-in name of the Global Administrator)' }
$redirect = 'https://helpdesk.amb-it.co.uk/api/auth/signin-oidc'
$signinName = 'Helpdesk sign-in'; $mailName = 'Helpdesk mail'
$users = @('helpdesk-test-admin', 'helpdesk-test-employee')
$boxes = [ordered]@{ 'helpdesk-test-a' = 'Helpdesk Test A'; 'helpdesk-test-b' = 'Helpdesk Test B'; 'helpdesk-test-outside' = 'Helpdesk Test Outside' }
$scopeName = 'HelpdeskTestMailboxes'

function Set-EnvValue([string]$Name, [string]$Value) {
    $lines = if (Test-Path $envFile) { @(Get-Content $envFile) } else { @() }
    $hit = $false
    $lines = $lines | ForEach-Object { if ($_ -match "^$Name=") { $hit = $true; "$Name=$Value" } else { $_ } }
    if (-not $hit) { $lines += "$Name=$Value" }
    Set-Content -Path $envFile -Value $lines
}
function New-Secret([int]$Bytes = 24) {
    $b = New-Object byte[] $Bytes; [Security.Cryptography.RandomNumberGenerator]::Fill($b)
    # 'Aa1!' guarantees the four character classes Entra requires.
    'Aa1!' + (([Convert]::ToBase64String($b)) -replace '[^A-Za-z0-9]', 'x')
}

if (-not (Test-Path -LiteralPath $CertCer)) { throw "missing $CertCer" }
$cer = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertCer)
if ($cer.HasPrivateKey) { throw 'the .cer must be the public file only' }

Connect-MgGraph -TenantId $TenantId -NoWelcome -Scopes 'Application.ReadWrite.All', 'User.ReadWrite.All' | Out-Null
$ctx = Get-MgContext
if ($ctx.TenantId -ne $TenantId) { throw "connected to tenant $($ctx.TenantId), expected $TenantId" }
$adminUser = Get-MgUser -UserId $Admin -Property Id
"tenant verified: $TenantId ; platform admin object id: $($adminUser.Id)"

$signin = Get-MgApplication -Filter "displayName eq '$signinName'"
$mail = Get-MgApplication -Filter "displayName eq '$mailName'"
$existingUsers = foreach ($u in $users) { Get-MgUser -Filter "userPrincipalName eq '$u@$Domain'" }
"plan: sign-in app $(if ($signin) { 'EXISTS (skipped)' } else { 'create' }); mail app $(if ($mail) { 'EXISTS (skipped)' } else { 'create' }); users to create: $($users.Count - @($existingUsers).Count)"
if (-not $Execute) { 'dry run only; re-run with -Execute'; return }

# ---- Entra ----
if (-not $signin) {
    $signin = New-MgApplication -DisplayName $signinName -SignInAudience AzureADMyOrg -Web @{ RedirectUris = @($redirect) }
    New-MgServicePrincipal -AppId $signin.AppId | Out-Null
    $sec = Add-MgApplicationPassword -ApplicationId $signin.Id -PasswordCredential @{ DisplayName = 'helpdesk production'; EndDateTime = (Get-Date).AddDays(30) }
    Set-EnvValue 'ENTRA_CLIENT_SECRET' $sec.SecretText
    $sec = $null
}
if (-not $mail) {
    $kc = @{ Type = 'AsymmetricX509Cert'; Usage = 'Verify'; Key = [IO.File]::ReadAllBytes($CertCer); DisplayName = 'helpdesk production graph client' }
    $mail = New-MgApplication -DisplayName $mailName -SignInAudience AzureADMyOrg -KeyCredentials @($kc)
    New-MgServicePrincipal -AppId $mail.AppId | Out-Null
}
$mailSp = Get-MgServicePrincipal -Filter "appId eq '$($mail.AppId)'"
Set-EnvValue 'ENTRA_TENANT_ID' $TenantId
Set-EnvValue 'ENTRA_CLIENT_ID' $signin.AppId
Set-EnvValue 'GRAPH_TENANT_ID' $TenantId
Set-EnvValue 'GRAPH_CLIENT_ID' $mail.AppId
Set-EnvValue 'BOOTSTRAP_PLATFORM_ADMIN_SUBJECT' $adminUser.Id

$pwFile = Join-Path $env:USERPROFILE 'HelpdeskSecrets\m365-test-users.txt'
foreach ($u in $users) {
    $upn = "$u@$Domain"
    if (Get-MgUser -Filter "userPrincipalName eq '$upn'") { "user exists, skipped: $upn"; continue }
    $pw = New-Secret
    New-MgUser -AccountEnabled -DisplayName ($u -replace '-', ' ') -UserPrincipalName $upn -MailNickname $u `
        -PasswordProfile @{ Password = $pw; ForceChangePasswordNextSignIn = $true } | Out-Null
    Add-Content -Path $pwFile -Value "$upn=$pw"
    $pw = $null
}
if (Test-Path $pwFile) { & icacls $pwFile /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null }

# ---- Exchange ----
Connect-ExchangeOnline -UserPrincipalName $Admin -ShowBanner:$false | Out-Null
$org = (Get-OrganizationConfig).Name
foreach ($k in $boxes.Keys) {
    $addr = "$k@$Domain"
    if (Get-Mailbox -Identity $addr -ErrorAction SilentlyContinue) { "mailbox exists, skipped: $addr"; continue }
    New-Mailbox -Shared -Name $boxes[$k] -DisplayName $boxes[$k] -Alias $k | Out-Null
}
Start-Sleep 10
foreach ($k in 'helpdesk-test-a', 'helpdesk-test-b') { Set-Mailbox -Identity "$k@$Domain" -CustomAttribute1 'HelpdeskTest' }
if (-not (Get-ServicePrincipal -Identity $mailSp.Id -ErrorAction SilentlyContinue)) {
    New-ServicePrincipal -AppId $mail.AppId -ObjectId $mailSp.Id -DisplayName $mailName | Out-Null
}
if (-not (Get-ManagementScope -Identity $scopeName -ErrorAction SilentlyContinue)) {
    New-ManagementScope -Name $scopeName -RecipientRestrictionFilter "CustomAttribute1 -eq 'HelpdeskTest'" | Out-Null
}
foreach ($role in 'Application Mail.ReadWrite', 'Application Mail.Send') {
    $have = Get-ManagementRoleAssignment -RoleAssignee $mailSp.Id -ErrorAction SilentlyContinue | Where-Object { $_.Role -eq $role }
    if (-not $have) { New-ManagementRoleAssignment -App $mailSp.Id -Role $role -CustomResourceScope $scopeName | Out-Null }
}

# ---- Fail unless the grants really exist and isolate correctly ----
$assigned = @(Get-ManagementRoleAssignment -RoleAssignee $mailSp.Id | Where-Object { $_.CustomResourceScope -eq $scopeName } | ForEach-Object Role)
foreach ($role in 'Application Mail.ReadWrite', 'Application Mail.Send') {
    if ($assigned -notcontains $role) { throw "role assignment missing after creation: $role (scope $scopeName)" }
}
foreach ($k in $boxes.Keys) {
    $want = $k -ne 'helpdesk-test-outside'
    $r = @(Test-ServicePrincipalAuthorization -Identity $mailSp.Id -Resource "$k@$Domain")
    $inScope = ($r | Where-Object { $_.InScope }).Count -eq 2
    if ($inScope -ne $want) { throw "authorization for $k@$Domain is $inScope, expected $want" }
}

# ---- Verify (no secrets printed) ----
"== verification"
$thumbApp = ($mail.KeyCredentials | ForEach-Object { ($_.CustomKeyIdentifier | ForEach-Object { $_.ToString('X2') }) -join '' })
"mail app $($mail.AppId): certificate thumbprint $($cer.Thumbprint) ; on app: $($thumbApp -join ',') ; match: $($thumbApp -contains $cer.Thumbprint)"
"sign-in app $($signin.AppId): redirect $redirect"
foreach ($k in $boxes.Keys) {
    $r = Test-ServicePrincipalAuthorization -Identity $mailSp.Id -Resource "$k@$Domain"
    "RBAC $k@${Domain}: " + (($r | ForEach-Object { "$($_.RoleName) InScope=$($_.InScope)" }) -join '; ')
}
Get-ManagementRoleAssignment -RoleAssignee $mailSp.Id | ForEach-Object { "assignment: $($_.Role) scope=$($_.CustomResourceScope)" }
"identifiers written to $envFile (git-ignored); user passwords in $pwFile"
