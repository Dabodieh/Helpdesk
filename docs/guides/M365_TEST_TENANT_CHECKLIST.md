# Microsoft 365 test-tenant validation checklist

Purpose: a short, ordered checklist for the first supervised validation of staff sign-in and shared-mailbox mail against a **non-production Microsoft 365 test tenant**. The experiment detail is in `GRAPH_TENANT_VALIDATION.md` (E-1..E-38); this page is the order and the entry conditions. Nothing here has been run: no tenant is configured, and the private stack (`helpdesk-pv`) is deliberately cut off from the internet.

## A. Entry criteria (all must be true; none may be invented)

- [ ] A non-production tenant (a Microsoft 365 Developer Program tenant or an approved separate tenant) and a named operator with Global Administrator, or Application Administrator plus Exchange Administrator.
- [ ] Decision on webhook reachability recorded (`OPEN_DECISIONS.md` D-19): a public https address Microsoft Graph can reach, or polling-only mail (not built yet).
- [ ] Two shared mailboxes (A, B) and one control mailbox outside the scope, plus an external address you own for sending test mail.
- [ ] A test staff account (and a second, ordinary employee account) in the tenant.
- [ ] A restore-tested backup of the private stack's database (empty is fine) and the isolated stack stopped from any real data.

## B. What must exist in the tenant (operator creates; the application never creates mailboxes)

| Item | Requirement |
|---|---|
| Sign-in app registration | Single tenant. Redirect URI exactly `https://<PUBLIC_HOST><PUBLIC_PORT_SUFFIX>/api/auth/signin-oidc` (web). Delegated `openid`, `profile`, `email` only; no application permissions. One client secret (or created by the guided wizard, ADR-037). |
| Mail app registration | Single tenant, certificate credential preferred (secret supported). **No** tenant-wide `Mail.*` admin consent in Entra. |
| Exchange RBAC for Applications | `New-ServicePrincipal` for the mail app; management scope limited to mailboxes A and B (custom attribute or administrative unit); role assignments `Application Mail.ReadWrite` and `Application Mail.Send` with that scope. `Test-ServicePrincipalAuthorization`: A and B `InScope = True`, control `False`. Allow 30 min to 2 h for grants. |
| Shared mailboxes | One per test Department (A, B), created by the administrator in Exchange Online. |
| Admin consent | Sign-in app: user consent to the three basic scopes (admin consent optional). Mail app: none in Entra; authorisation is the Exchange role assignment above. |
| Guided Entra setup (optional) | Needs a signed-in platform administrator and an account allowed to consent `Application.ReadWrite.All` for its own session (device code, Microsoft's public client); not usable on a brand-new production install (ADR-037), so for the first run supply `ENTRA_*` as server settings. |

## C. Network (the private stack must be given egress deliberately)

Outbound from the application container (set `NO_EGRESS=` empty, nothing else changes):

| Endpoint | Used for |
|---|---|
| `login.microsoftonline.com:443` | OIDC discovery and signing keys, authorisation code exchange, client-credential tokens |
| `graph.microsoft.com:443` | Mail read/send, subscriptions, delta (and guided setup) |

Inbound: Microsoft Graph must reach `https://<public host>/api/mail/graph/notifications` and `/api/mail/graph/lifecycle` (TLS 1.2+, valid certificate from a public CA: a self-signed certificate is rejected). The operator's own PC needs `outlook.office365.com` for Exchange PowerShell; the application does not.

## D. Order of tests

1. **Preconditions on the stack:** `helpdesk.ps1 check`, `migrate`, `start`, `verify` green; `/api/auth/providers` returns `{"entra":true}`.
2. **Sign-in (staff):** open `/`, choose Microsoft sign-in as the test administrator listed in `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT`. Expect: redirect to Microsoft, return signed in, platform administrator, audit event. Then an ordinary employee: signed in without staff access, lands in the portal. A tenant guest or other-tenant account must be refused. Wrong redirect URI or a blocked consent shows the safe sign-in message with a reference, not a 500.
3. **Mailbox registration:** Administration > Microsoft 365 > register mailbox A for Department A; probe passes, subscription created, first sync healthy. Register B for Department B. Mailbox on the control address is refused or unhealthy, never silently empty (E-1..E-4).
4. **Inbound:** external address mails A: exactly one ticket in Department A, none in B (E-5..E-11). Move the message between folders: same ticket, no duplicate. Replay the notification (or wait for reconcile): no duplicate.
5. **Outbound:** public reply on that ticket: arrives from mailbox A, Reply-To A, agent address absent, agent signature once, state progression queued, accepted (not claimed delivered) (E-12, E-21..E-26). Reply again after changing assignee: same From.
6. **Threading:** reply from the external address attaches to the same ticket; edited subject does not split it (E-15, E-19, E-23).
7. **Isolation:** a Department B agent cannot see ticket, count, requester or mailbox of A; a ticket of A never sends from B; a mailbox-less ticket cannot send (blocked, no fallback).
8. **Failure handling:** invalid recipient produces an NDR that is not a ticket and marks the message failed/bounced (E-27..E-29); block webhooks 15 minutes, send, restore: mailbox shows Attention then catches up by reconcile; revoke and restore consent on B: health Failed then Healthy (recovery row of the runbook).
9. **Sign-in outage:** stop outbound access to `login.microsoftonline.com` again (re-enable `NO_EGRESS`): login shows the "temporarily unavailable" message with a reference and the log holds the same reference.

## E. Evidence to record (identifiers, status codes, headers; never bodies or secrets)

Fill the table in `GRAPH_TENANT_VALIDATION.md` section 3; mark E-1..E-38 confirmed, amended or not run; update ADR-011, risk R-04, D-16 and the production readiness report. Simulator results are not evidence. Anything not run stays open.

## F. After the session

- [ ] Remove the test app registrations, mailboxes and role assignments, or record why they stay.
- [ ] Turn egress off again; stop the stack; keep or destroy `helpdesk-pv_*` volumes deliberately.
- [ ] Record which of F-04 (key encryption), F-08 (session policy) and F-09 (logs) the session informed.

## G. Exact steps for the live session (run by the operator, in this order; nothing here has been executed)

Replace `<tenant>` with the test tenant's domain, `<host>` with `PUBLIC_HOST` and `<suffix>` with `PUBLIC_PORT_SUFFIX` of the private stack. Record every object id you create in the results table so section H can remove them.

### G1. Sign-in app registration (Microsoft Graph PowerShell, Application Administrator)

```powershell
Connect-MgGraph -Scopes "Application.ReadWrite.All" -TenantId <tenant>
$signin = New-MgApplication -DisplayName "Helpdesk TEST sign-in" -SignInAudience AzureADMyOrg `
  -Web @{ RedirectUris = @("https://<host><suffix>/api/auth/signin-oidc") }
New-MgServicePrincipal -AppId $signin.AppId | Out-Null
$secret = Add-MgApplicationPassword -ApplicationId $signin.Id -PasswordCredential @{ DisplayName = "validation"; EndDateTime = (Get-Date).AddDays(30) }
# ENTRA_TENANT_ID = tenant id, ENTRA_CLIENT_ID = $signin.AppId, ENTRA_CLIENT_SECRET = $secret.SecretText (copy once into docker\windows\.env; never into chat, Git or a ticket)
```

Redirect URI: exactly the https URL above (the application builds it from the host the browser used; `http` is accepted by Entra only for `localhost`). Add no API permissions.

### G2. Mail app registration and credential

```powershell
$mail = New-MgApplication -DisplayName "Helpdesk TEST mail" -SignInAudience AzureADMyOrg
New-MgServicePrincipal -AppId $mail.AppId | Out-Null
$mailSecret = Add-MgApplicationPassword -ApplicationId $mail.Id -PasswordCredential @{ DisplayName = "validation"; EndDateTime = (Get-Date).AddDays(30) }
# GRAPH_TENANT_ID, GRAPH_CLIENT_ID = $mail.AppId, GRAPH_CLIENT_SECRET = $mailSecret.SecretText
```

Certificate credentials are now wired through the Windows tooling (`GRAPH_CERT_PATH` and `GRAPH_CERT_PASSWORD` with the PFX mounted via `SECRETS_DIR`); a short-lived client secret (`GRAPH_CLIENT_SECRET`) still works for a quick first run. Do **not** add Graph application permissions or grant admin consent in Entra for this app. Section I lists the minimum set for the personal-tenant run.

### G3. Test shared mailboxes and scoping (Exchange Online PowerShell, Exchange Administrator)

```powershell
Connect-ExchangeOnline
New-Mailbox -Shared -Name "Helpdesk Test A" -Alias helpdesk-test-a      # helpdesk-test-a@<tenant>
New-Mailbox -Shared -Name "Helpdesk Test B" -Alias helpdesk-test-b
New-Mailbox -Shared -Name "Helpdesk Test Outside" -Alias helpdesk-test-outside   # control, never registered
Set-Mailbox helpdesk-test-a -CustomAttribute1 HelpdeskTest
Set-Mailbox helpdesk-test-b -CustomAttribute1 HelpdeskTest
New-ServicePrincipal -AppId <mail AppId> -ObjectId <mail service principal object id> -DisplayName "Helpdesk TEST mail"
New-ManagementScope -Name "HelpdeskTestMailboxes" -RecipientRestrictionFilter "CustomAttribute1 -eq 'HelpdeskTest'"
New-ManagementRoleAssignment -App <mail service principal object id> -Role "Application Mail.ReadWrite" -CustomResourceScope "HelpdeskTestMailboxes"
New-ManagementRoleAssignment -App <mail service principal object id> -Role "Application Mail.Send"      -CustomResourceScope "HelpdeskTestMailboxes"
Test-ServicePrincipalAuthorization -Identity <mail service principal object id> -Resource helpdesk-test-a@<tenant>        # InScope True
Test-ServicePrincipalAuthorization -Identity <mail service principal object id> -Resource helpdesk-test-outside@<tenant>  # InScope False
```

Wait up to 2 hours for grants; the cmdlet bypasses the cache. Nothing in the tenant gives the application a mailbox password.

### G4. Connect the private stack (the only configuration change to the stack)

1. Stop the stack: `.\scripts\windows\helpdesk.ps1 stop`.
2. In `docker\windows\.env`: replace the fake `ENTRA_*` and `GRAPH_*` values with G1/G2 values; set `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT` to the test administrator's Entra **object id**; set `NO_EGRESS=` (empty); keep `BIND_ADDRESS=127.0.0.1`.
3. For webhook tests only (decide D-19 first): the host must be reachable by Microsoft at a public https name with a CA-issued certificate (tunnel or reverse proxy in front); set `PUBLIC_HOST` and `PUBLIC_PORT_SUFFIX` to that name. Do not publish the loopback ports. Without it only the sign-in and outbound parts (D2, D5 without webhooks) can be run.
4. `helpdesk.ps1 check`, `start`, `verify`; confirm `GET https://<host><suffix>/api/auth/providers` returns `{"entra":true}`.

### G5. Verification order

Run section D. Stop at the first unexpected result and record it; do not change tenant objects to make a test pass.

### H. Rollback and clean-up (after the session, or at any stop)

```powershell
# Exchange
Remove-ManagementRoleAssignment -Identity <assignment identities from Get-ManagementRoleAssignment -App <sp object id>> -Confirm:$false
Remove-ManagementScope -Identity HelpdeskTestMailboxes -Confirm:$false
Remove-ServicePrincipal -Identity <mail service principal object id>
Remove-Mailbox helpdesk-test-a, helpdesk-test-b, helpdesk-test-outside -Confirm:$false
# Entra
Remove-MgApplication -ApplicationId <signin object id>; Remove-MgApplication -ApplicationId <mail object id>
```

Stack: `.\scripts\windows\helpdesk.ps1 stop`, restore `NO_EGRESS=true` and the fake values in `docker\windows\.env` (or delete the file), and remove the `helpdesk-pv_*` volumes only deliberately (`docker volume rm`, never `down -v`). If sign-in misbehaves during the session, the instant stop is: stop the stack, then disable the sign-in app (`Update-MgApplication -ApplicationId <id> -Web @{ RedirectUris = @() }`) or remove its secret.

## I. Personal-tenant run (2026-10-14): minimum configuration, approvals and environment status

### I1. What the tenant must be

- A **business** Microsoft 365 tenant with Exchange Online (Business Basic/Standard/Premium, E1/E3/E5 or a Developer tenant) and a verified domain or the `*.onmicrosoft.com` domain. A consumer *Microsoft 365 Personal/Family* subscription is not a tenant (no Entra tenant, no Exchange administration) and cannot be used.
- **Licences:** app registrations need none (Entra ID Free). Shared mailboxes need **no licence** while they stay under 50 GB without an archive or litigation hold. The two test users need no mailbox licence to sign in to the application (it uses the identity only). Your existing administrator's licence is untouched.
- **Roles (yours, for the session):** Application Administrator (or Global Administrator) for the app registrations, Exchange Administrator for the mailboxes and RBAC.
- **Your own accounts and mailboxes are not used or modified.** New, clearly named objects only.

### I2. Minimum set of objects (all new; names are suggestions)

| # | Object | Purpose | Setting that matters |
|---|---|---|---|
| 1 | User `helpdesk-test-admin@<domain>` (cloud-only) | Signs in as platform administrator | Its Entra **object id** becomes `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT` |
| 2 | User `helpdesk-test-employee@<domain>` | Proves a normal employee gets no staff access | none |
| 3 | App registration "Helpdesk TEST sign-in" | Staff sign-in (OpenID Connect, confidential client) | Single tenant; Web redirect URI `https://localhost:8643/api/auth/signin-oidc`; **no API permissions**; one client secret (30 days) |
| 4 | App registration "Helpdesk TEST mail" | Graph application access to mail | Single tenant; **certificate** credential (public file `graph-public.cer`, prepared); **no Graph API permissions and no admin consent** |
| 5 | Shared mailboxes `helpdesk-test-a@`, `helpdesk-test-b@` | The two Department mailboxes | Custom attribute 1 = `HelpdeskTest` on these two only |
| 6 | Shared mailbox `helpdesk-test-outside@` | Control that must stay unreachable | Registered nowhere; no attribute |
| 7 | Exchange service principal pointer, management scope `HelpdeskTestMailboxes` (`CustomAttribute1 -eq 'HelpdeskTest'`), roles `Application Mail.ReadWrite` and `Application Mail.Send` scoped to it | Least-privilege, mailbox-scoped access | `Test-ServicePrincipalAuthorization`: A and B `InScope=True`, outside `False` |

Not touched: existing users, mailboxes, mail-flow rules, connectors, accepted domains, DNS, existing app registrations, tenant-wide consent, Conditional Access. No tenant-wide mailbox access is requested (that would defeat the scoping).

### I3. Application-side facts that decide the configuration

- **Sign-in:** authorisation-code flow with PKCE, the tenant's v2.0 authority, callback path `/api/auth/signin-oidc`. The callback is a browser redirect, so Microsoft never calls the application: `https://localhost:8643` works privately. The self-signed localhost certificate produces a one-time browser warning (or trust `docker\windows\tls\fullchain.pem`). Plain `http` cannot be used: the application issues Secure cookies and the antiforgery system requires TLS. The sign-in secret is a client secret (OpenID Connect client certificates are not supported by the application); only the Graph mail app uses a certificate.
- **Graph:** client-credential token for `https://graph.microsoft.com/.default`, certificate preferred (`Mail:Graph:CertificatePath` and password; now wired through `GRAPH_CERT_PATH` / `GRAPH_CERT_PASSWORD`; a secret is optional but one of the two is required).
- **Webhooks without a public address:** `Mail:NotificationBaseUrl` only has to be https; with `https://localhost:8643` Microsoft cannot validate the subscription, so creating it fails. Registration treats the subscription as best effort: the mailbox is registered, the first delta pass runs, health shows Attention (`subscription_failed`), and the 5-minute reconcile finds new mail. So registration, inbound (up to 5 minutes' latency), outbound, threading and isolation can all be tested privately; **only the webhook experiments (E-30..E-35) need a public address** and are deferred (decision D-19 remains).
- **Outbound network:** only `login.microsoftonline.com:443` and `graph.microsoft.com:443`. The integration stack enforces this with an allowlist forward proxy; the application has no other route out.

### I4. Exact changes that need your approval (nothing has been run)

Reply with the numbers you approve; I will give you the commands for exactly those (they are in G1 to G3, adapted to the names above) to run in your own admin session.

1. Create users 1 and 2 (cloud-only, strong passwords you keep, no licences, no roles).
2. Create app registration 3 with the redirect URI above and one 30-day client secret; no API permissions.
3. Create app registration 4 and upload `C:\Users\abode\HelpdeskSecrets\m365\graph-public.cer` (thumbprint `C93077A06A317152A5EA5F11D809920E3E716648`, expires 2027-10-14, public key only); no API permissions, no consent.
4. Create shared mailboxes 5 and 6.
5. Set custom attribute 1 on mailboxes A and B only.
6. Create the management scope, the Exchange service principal pointer and the two role assignments; verify with `Test-ServicePrincipalAuthorization` (A and B in scope, outside not).
7. Give me the values (tenant id, the two application ids, the object id of user 1) and handle the two secrets yourself: put the sign-in client secret into `docker\windows\.env.m365` (`ENTRA_CLIENT_SECRET=`) on this PC; never paste it into chat.

### I5. Integration environment status

| Item | State |
|---|---|
| Project/ports | `helpdesk-m365`, `127.0.0.1:8289` (redirect) and `127.0.0.1:8643` (HTTPS), subnet 172.31.99.0/24; separate volumes, database, object store; `helpdesk-pv` untouched and kept |
| Env file | `docker\windows\.env.m365` (git-ignored) prepared: random database and object-store secrets, Production mode via the production compose file, `BIND_ADDRESS=127.0.0.1`, no development sign-in, fake tenant values removed (empty until step I4.7) |
| Data Protection | Certificate `dp.pfx` + password file generated into `C:\Users\abode\HelpdeskSecrets\m365\` (ACL: this user only), mounted read-only through the new `compose.secrets.yml` overlay; `DP_REQUIRE_CERT=true`, so a missing or unreadable certificate stops the start |
| Graph certificate | `graph.pfx` + public `graph-public.cer` generated in the same folder (private key never leaves it) |
| Egress | `NO_EGRESS=true` + `EGRESS_ALLOWLIST=true`: internal network, only the two Microsoft hosts through the squid proxy (`compose.egress-allowlist.yml`, `egress/squid.conf`). The proxy image is `ubuntu/squid:latest` and has **not been pulled or tested** (Docker Desktop was not running: the PC restarted this morning); pin a digest in `EGRESS_PROXY_IMAGE` after the first pull |
| Config check | `Test-HelpdeskConfig` against the file: only the six tenant values are missing (expected) |
| Not done | image build, migrate, start and the proxy test (Docker was down); everything tenant-side |
| Cloudflare / public exposure | stopped / none |

Commands once Docker Desktop is running and the tenant values are in the file:

```powershell
$env:HELPDESK_ENV_FILE = "F:\Software Development\Helpdesk\docker\windows\.env.m365"
.\scripts\windows\helpdesk.ps1 check
.\scripts\windows\helpdesk.ps1 build
.\scripts\windows\helpdesk.ps1 migrate
.\scripts\windows\helpdesk.ps1 start
.\scripts\windows\helpdesk.ps1 verify
# allowlist self-test (expects: Microsoft host allowed, anything else refused)
docker run --rm --network helpdesk-m365_internal curlimages/curl -sI -x http://egressproxy:3128 https://example.com   # expect a proxy refusal (403)
```

### I6. Next actions

1. Start Docker Desktop (or tell me to) so I can build and test the stack and the allowlist.
2. Approve the numbered changes in I4 (or amend them), then run them in your admin session.
3. Send me only the non-secret values (tenant id, application ids, object id); I fill the env file, start the stack and walk section D with you.

## J. Public hostname addendum (2026-10-14)

For the Access-protected hostname the sign-in app registration (G1) uses redirect URI `https://helpdesk.amb-it.co.uk/api/auth/signin-oidc` (no port suffix). Values go into the root `.env` (`ENTRA_*`, `GRAPH_*`, `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT`, `SECRETS_DIR`, `DP_CERT_*`; see `.env.example`) rather than `docker\windows\.env`, because Production now runs inside the `helpdesk` project (`scripts\windows\dev.ps1 prod-check`, `prod-migrate`, `prod-start`). G3 (mailboxes and scoping) and the I4 approvals are unchanged and still pending. Cloudflare Access sits in front: only emails on its allow-list reach the app, then Entra sign-in applies.

## K. Production preparation for the personal test tenant (2026-10-14; NOTHING has been run)

**Tenant type.** I1 applies: a consumer Microsoft 365 Personal/Family subscription is not a tenant. Confirm you have a business or Developer tenant with Exchange Online before any command below.

**Harmless local preparation (done or safe to do now):** certificate script `scripts\windows\new-production-certs.ps1` (refuses without `-Approved`), `.env.example` production keys, `dev.ps1 prod-check`, redirect URI fixed below.

**Needs your approval (tenant changes; run in your own admin session, minimum roles in brackets):**
1. Sign-in app registration [Application Administrator]: `New-MgApplication -DisplayName "Helpdesk sign-in" -SignInAudience AzureADMyOrg -Web @{ RedirectUris = @("https://helpdesk.amb-it.co.uk/api/auth/signin-oidc") }`, service principal, one client secret (30 days). No API permissions. Values: `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `ENTRA_CLIENT_SECRET`.
2. Mail app registration [Application Administrator]: `New-MgApplication -DisplayName "Helpdesk mail" -SignInAudience AzureADMyOrg`, service principal, upload the NEW `graph-public.cer` (public key only) as its certificate. No Graph API permissions and no admin consent. Values: `GRAPH_TENANT_ID`, `GRAPH_CLIENT_ID`.
3. Test users `helpdesk-test-admin@<domain>` and `helpdesk-test-employee@<domain>` [User Administrator]; the admin object id becomes `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT`.
4. Shared mailboxes A, B and a control "outside" [Exchange Administrator], CustomAttribute1=HelpdeskTest on A and B only (G3).
5. Exchange RBAC for Applications [Exchange Administrator with Role Management]: `New-ServicePrincipal`, `New-ManagementScope HelpdeskTestMailboxes`, role assignments `Application Mail.ReadWrite` and `Application Mail.Send` scoped to it, then `Test-ServicePrincipalAuthorization` (A and B in scope, outside not) (G3).

Nothing in this section is run by the application or by Claude; secrets are typed locally into `.env` and never pasted into chat or Git.

### K1. Status after local preparation (2026-10-14)

- Production certificates generated (not yet backed up offline; do that before first use): Data Protection `CN=Helpdesk production key ring`, thumbprint `A03A390F17518D6F9EA5BF3B9ED09390DE7178F8`, expires 2031-10-14; Graph client `CN=Helpdesk production Graph client`, thumbprint `7858DEA062F0CEC2F35805BC2447ADD3EF7352BF`, expires 2027-10-14. Public file to upload to the mail app registration: `C:\Users\abode\HelpdeskSecrets\production\graph-public.cer` (public key only; do NOT use the disposable `m365\graph-public.cer`).
- `helpdesk_prod` created and migrated (59 tables, empty); the development database was not touched.
- Waiting for you: tenant type confirmation (business or Developer tenant with Exchange Online) and approval of K steps 1 to 5.

## K2. Verified tenant preflight (2026-10-14, read-only; nothing created)

Signed in interactively as `<operator-account>` (Microsoft Graph PowerShell with read-only delegated scopes Organization, Domain, Application, User and RoleManagement.Read; Exchange Online PowerShell for read-only queries).

| Check | Result |
|---|---|
| Tenant | `AMB IT`, id `<tenant-id>`, Entra (AAD), GB, created 2022-07-09 |
| Domains | `amb-it.co.uk` (default, verified), `ambit94.onmicrosoft.com` |
| Subscriptions | `O365_BUSINESS_ESSENTIALS` (1 of 1 licence used; includes Exchange Online Plan 1 and 50 GB mailbox), `FLOW_FREE` |
| This is a business tenant | YES: Exchange Online and Entra available (not a consumer Personal/Family subscription) |
| Your roles | Global Administrator; Exchange role groups include Role Management and Mail Recipient Creation |
| Exchange RBAC for Applications | AVAILABLE: `New-ServicePrincipal`, `New-ManagementScope`, `New-ManagementRoleAssignment`, `Test-ServicePrincipalAuthorization` present; roles `Application Mail.Read`, `.ReadBasic`, `.ReadWrite`, `.Send` exist; no management scopes or Exchange service principals exist yet |
| Existing objects (not to be touched) | Apps: other unrelated applications. Users: `<operator-account>`, `<second-admin-account>`. Mailboxes: <operator> (user), system discovery mailbox. No shared mailboxes |
| Name conflicts | none (no `Helpdesk` apps, users or `helpdesk-*` mailboxes) |

**Licensing.** Every proposed object is free on this plan: app registrations and Exchange RBAC need no licence; cloud-only users without a licence cannot sign in to mail but can authenticate to the app (identity only); shared mailboxes need no licence under 50 GB with no archive or litigation hold. The one licence is already used by your account and is not needed. No cost expected.

**Resources proposed (all new, `helpdesk-test-*` names; nothing exists until you approve):**
1. App registration `Helpdesk sign-in`: single tenant, Web redirect `https://helpdesk.amb-it.co.uk/api/auth/signin-oidc`, one 30-day client secret, NO API permissions, no consent.
2. App registration `Helpdesk mail`: single tenant, certificate credential = `C:\Users\abode\HelpdeskSecrets\production\graph-public.cer` (thumbprint `7858DEA062F0CEC2F35805BC2447ADD3EF7352BF`), NO Graph API permissions, no consent.
3. Users `helpdesk-test-admin@amb-it.co.uk` (platform administrator candidate) and `helpdesk-test-employee@amb-it.co.uk`: cloud-only, unlicensed, no directory roles, strong passwords you set.
4. Shared mailboxes `helpdesk-test-a@amb-it.co.uk` and `helpdesk-test-b@amb-it.co.uk` (CustomAttribute1 = `HelpdeskTest`), and control `helpdesk-test-outside@amb-it.co.uk` (no attribute, never registered).
5. Exchange RBAC: service principal pointer for `Helpdesk mail`, management scope `HelpdeskTestMailboxes` (`CustomAttribute1 -eq HelpdeskTest`), role assignments `Application Mail.ReadWrite` and `Application Mail.Send` scoped to it; then `Test-ServicePrincipalAuthorization` (A, B in scope; outside not). No tenant-wide mailbox access, no Graph application permission, no admin consent, no changes to your or <second-admin>s mailbox.

**Decision for you:** which account is the first platform administrator (`BOOTSTRAP_PLATFORM_ADMIN_SUBJECT` = Entra object id): the dedicated `helpdesk-test-admin` (recommended; needs its own sign-in) or your own account (its object id is read from Entra when you decide).

**Open gate (certificate recovery).** The production certificates are stored under an ACL limited to your Windows user, but no off-PC encrypted backup exists, and independent recoverability needs a passphrase you choose and keep away from this PC. Run `scripts\windows\backup-production-certs.ps1 -Destination <drive>` yourself, copy the output off the PC, and confirm. Uploading `graph-public.cer` to the tenant waits for this.

## K3. Provisioning results (2026-10-14, tenant <tenant-id>)

Run by `scripts\windows\provision-m365-test-tenant.ps1 -Execute` (idempotent; tenant id verified before every write). No secrets recorded here; they are in the git-ignored `.env` and `%USERPROFILE%\HelpdeskSecrets\m365-test-users.txt` (ACL: this user only).

| Resource | Result |
|---|---|
| App `Helpdesk sign-in` | created, appId `<sign-in-app-id>`, single tenant, redirect `https://helpdesk.amb-it.co.uk/api/auth/signin-oidc`, 30-day secret (expires about 2026-11-13), no API permissions |
| App `Helpdesk mail` | created, appId `<mail-app-id>`, certificate `graph-public.cer` (thumbprint `7858DEA062F0CEC2F35805BC2447ADD3EF7352BF`); no Graph application permissions, no consent |
| Certificate proof | a client-credentials token was obtained with `graph.pfx` (cert works; Entra stores a different key identifier string than the thumbprint, which is normal) |
| Users | `helpdesk-test-admin@amb-it.co.uk`, `helpdesk-test-employee@amb-it.co.uk`: cloud-only, unlicensed, no roles, must change password at first sign-in |
| Shared mailboxes | `helpdesk-test-a@`, `helpdesk-test-b@` (CustomAttribute1=HelpdeskTest), `helpdesk-test-outside@` (no attribute): created |
| Exchange service principal pointer | created for `Helpdesk mail` (object id `<object-id>`) |
| Platform administrator | `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT` = <operator> object id `<object-id>` |
| Management scope, role assignments | NOT CREATED: Exchange refuses `New-ManagementScope` and `New-ManagementRoleAssignment` until `Enable-OrganizationCustomization` is run once for the tenant (one-time, irreversible, organisation-wide). Needs approval. |
| Isolation test | with the app token, mailboxes A, B and outside all return HTTP 403 (no grant exists yet). The in-scope test (A and B succeed, outside 403) runs after the RBAC step and its propagation (up to about 2 hours). |

Next: approve `Enable-OrganizationCustomization`, then rerun the provisioning script (it skips everything that exists and creates the scope and the two role assignments), wait for propagation, then verify A and B return 200 and outside 403. No test mail has been sent. Production and the tunnel remain stopped.

### K3 update (2026-10-14, later)

- `Enable-OrganizationCustomization` run after tenant verification (domains `amb-it.co.uk` and `ambit94.onmicrosoft.com`, organisation `ambit94.onmicrosoft.com`); `IsDehydrated` True to False.
- Management scope `HelpdeskTestMailboxes` (`CustomAttribute1 -eq HelpdeskTest`) created.
- BLOCKED: `New-ManagementRoleAssignment` for `Application Mail.ReadWrite` is refused: the signed-in account has no delegating role assignment for the Application roles (the Organization Management role group holds it; its members are the TenantAdmins and ExchangeServiceAdmins groups, and `<operator>` is not listed directly). Fix needs your approval: add `<operator-account>` to the `Organization Management` role group (a privilege change to your own account), then rerun the RBAC step. No assignment exists, so the mail app still has no mailbox access (A, B and outside return 403).

### K3 update 2 (2026-10-14)

- The approved temporary add to `Organization Management` did NOT happen: `Add-RoleGroupMember` returned "only performed by a manager of the group" (the group manages itself). Verified afterwards: members are still only `TenantAdmins_-304240555` (which contains `<operator>`) and `ExchangeServiceAdmins_17318745`; no direct membership exists, so nothing needed removal.
- Diagnostics: `Organization Management` holds an unscoped delegating assignment for all 25 Application roles and `<operator>` is a member of `TenantAdmins` (nested in it), so the refusal looks like permission propagation after `Enable-OrganizationCustomization`, not a missing grant. Retry `New-ManagementRoleAssignment` later (the scope already exists); no further privilege change is expected to be needed. If it still fails after a few hours, open the Exchange admin center role-group page or a Microsoft support case.

### K3 update 3 (2026-10-14): RBAC assignments created

- Fresh Exchange session, tenant verified by domains; effective Application Mail.ReadWrite delegation for the admin: 2 (propagation had completed). Existing service principal `Helpdesk mail` and scope `HelpdeskTestMailboxes` (`CustomAttribute1 -eq HelpdeskTest`) confirmed.
- Created (terminating errors) and read back: `Application Mail.ReadWrite` and `Application Mail.Send`, both scoped to `HelpdeskTestMailboxes`. No group membership was changed.
- `Test-ServicePrincipalAuthorization`: helpdesk-test-a ALLOWED, helpdesk-test-b ALLOWED, helpdesk-test-outside DENIED (both roles).
- Graph with the mail certificate, immediately after creation: A, B, outside and the personal mailbox all HTTP 403, i.e. the grant has not propagated to Graph yet (up to about 2 hours). Outside and the personal mailbox must stay 403; A and B must become 200. Re-test once later; do not loop.
- `provision-m365-test-tenant.ps1` now sets `$PSDefaultParameterValues[*:ErrorAction] = Stop` and throws unless both assignments exist and A/B are in scope and outside is not.

### K3 update 4 (2026-10-14 16:46Z): first Graph validation

- Exchange read-back: `Application Mail.ReadWrite` and `Application Mail.Send` on `HelpdeskTestMailboxes`, enabled, created 16:44:02Z; `Test-ServicePrincipalAuthorization` A and B in scope, outside not. Service principal `Helpdesk mail` appid `<mail-app-id>`.
- Token (certificate, `graph.pfx`): `tid` <tenant-id>, `appid` <mail-app-id>, audience Graph, no application roles (expected: access comes from Exchange RBAC, not Entra consent).
- Graph `GET /users/<mailbox>/mailFolders/inbox`, about 2 minutes after the assignments were created: A 403, B 403, outside 403, personal mailbox 403, all `ErrorAccessDenied`. A and B are not yet effective, but the assignments were only about 2 minutes old (Microsoft documents up to about 2 hours), so this is inconclusive, not a defect. Re-test once, not before about 16:45Z + 30 min and preferably after 2 hours; if A or B is still 403 after 2 hours, investigate the error body and the app identity (not by granting tenant-wide Graph permissions).

### K3 update 4 (2026-10-14, 17:00Z): preflight status

- `graph.pfx` password rotated (same certificate and thumbprint; certificate sign-in to the tenant verified). The old password was exposed in a transcript; the earlier encrypted backups (`E:` and `I:\HelpdeskCertBackup\...20261014.zip.enc`) still contain the old PFX and must be superseded: run `backup-production-certs.ps1 -Destination I:\HelpdeskCertBackup` (it now writes a time-stamped file and includes only the five live files), then `verify-production-certs-backup.ps1 -Backup <file>`. Keep the older backups until the new one verifies.
- Graph A/B/outside/personal check NOT yet run: RBAC assignments were created 16:44:02Z, the window ends 18:44Z. Run `scripts\windows\check-graph-mailbox-access.ps1` once afterwards (expected 200, 200, 403, 403).
- Sign-in app registration: not yet read from the tenant (needs an interactive admin sign-in): run `scripts\windowserify-entra-signin-app.ps1`. Already confirmed: the application ID, secret and tenant authenticate, and the authority discovery document matches the tenant.
- Sign-in client secret (30-day lifetime) rotation plan: see `PRODUCTION_INTEGRATION_VALIDATION.md`, section 6.

### K3 update 5 (2026-10-14): sign-in registration and backup verified

- Sign-in app read from tenant <tenant-id> (read-only, device-code sign-in, Application.Read.All): PASS. Application <sign-in-app-id> "Helpdesk sign-in"; `signInAudience` AzureADMyOrg (single tenant); web redirect URI exactly `https://helpdesk.amb-it.co.uk/api/auth/signin-oidc` and no other; no SPA/public-client redirects; implicit grant off; no API permissions requested. Client secret `helpdesk production` expires 2026-11-13.
- Certificate backup `I:\HelpdeskCertBackup\helpdesk-production-certs-20261014-1803.zip.enc`: 11/11 PASS (checksum, decryption, five live files, no retired material, certificates and password files equal live). Older backups (`...20261014.zip.enc` on `I:` and `E:`) still hold the superseded PFX and are NOT yet deleted.
- Graph mailbox check (A 200, B 200, outside 403, personal 403): NOT yet run; allowed from 18:44Z.

### K3 update 6 (2026-10-14, 17:11Z): Graph mailbox check, early run

- `check-graph-mailbox-access.ps1` run once with the rotated certificate, 27 minutes after the RBAC assignments (16:44:02Z), i.e. BEFORE the 18:44Z window end: helpdesk-test-a 403 ErrorAccessDenied, helpdesk-test-b 403 ErrorAccessDenied, helpdesk-test-outside 403, anton.bodell 403.
- Reading: certificate authentication works (token issued); A and B still denied, consistent with Exchange RBAC not yet effective for Graph (earlier note: up to about 2 hours). Not a failure of the configuration. No permissions changed, no retry.
- Still to do: the single decisive run after 18:44Z; if A or B are still 403 then, read the effective grants with `Test-ServicePrincipalAuthorization` (needs an interactive Exchange session).

### K3 update 7 (2026-10-14, 18:50Z): Graph mailbox isolation VERIFIED

- `check-graph-mailbox-access.ps1`, run once with the rotated mail certificate, 2h06m after the RBAC assignments (16:44:02Z), read-only GET of each Inbox folder resource:
  - helpdesk-test-a@amb-it.co.uk: HTTP 200
  - helpdesk-test-b@amb-it.co.uk: HTTP 200
  - helpdesk-test-outside@amb-it.co.uk: HTTP 403 ErrorAccessDenied
  - <operator-account>: HTTP 403 ErrorAccessDenied
- Result: the Exchange RBAC scope (`HelpdeskTestMailboxes`) grants exactly A and B; the control and the personal mailbox are unreachable. The earlier 403s at 17:11Z were propagation delay. No permissions were changed.
