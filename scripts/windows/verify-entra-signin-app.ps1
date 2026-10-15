# Read-only check of the Helpdesk sign-in app registration (application ID, single tenant, web redirect URI, secret expiry). Prints no secret values.
# Run it yourself: it uses a device-code sign-in (open the shown URL, enter the code, sign in as an admin who can read app registrations). Application.Read.All only.
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$m=@{}
foreach($l in Get-Content (Join-Path $root '.env')){ if($l -match '^([A-Z_0-9]+)=(.*)$'){ $m[$Matches[1]]=$Matches[2].Trim() } }
$tenant=$m['ENTRA_TENANT_ID']; $client=$m['ENTRA_CLIENT_ID']
if(-not $tenant -or -not $client){ throw 'ENTRA_TENANT_ID and ENTRA_CLIENT_ID must be set in the git-ignored .env' }
Connect-MgGraph -TenantId $tenant -Scopes 'Application.Read.All' -UseDeviceCode -NoWelcome
$c=Get-MgContext
"tenant match: $($c.TenantId -eq $tenant)"
$a=Get-MgApplication -Filter "appId eq '$client'"
if(-not $a){'app NOT FOUND';exit 1}
"displayName: $($a.DisplayName)"
"signInAudience: $($a.SignInAudience)  (single tenant = AzureADMyOrg)"
"web redirect URIs: $($a.Web.RedirectUris -join ' | ')"
"expected URI present: $($a.Web.RedirectUris -contains 'https://helpdesk.amb-it.co.uk/api/auth/signin-oidc')"
"spa/publicClient redirects: $(@($a.Spa.RedirectUris).Count)/$(@($a.PublicClient.RedirectUris).Count)"
"implicit grant id/access: $($a.Web.ImplicitGrantSettings.EnableIdTokenIssuance)/$($a.Web.ImplicitGrantSettings.EnableAccessTokenIssuance)"
foreach($p in $a.PasswordCredentials){ "secret '$($p.DisplayName)' expires $($p.EndDateTime.ToString('yyyy-MM-dd'))" }
"requiredResourceAccess count: $(@($a.RequiredResourceAccess).Count)"
Disconnect-MgGraph | Out-Null
