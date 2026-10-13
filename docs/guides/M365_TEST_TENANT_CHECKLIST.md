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

A short-lived client secret is used for the first run because the Windows compose file passes `GRAPH_CLIENT_SECRET` only; certificate credentials are supported by the application (`Mail:Graph:CertificatePath`) but are not wired into the compose file yet (add a mounted PFX and the setting before any production use). Do **not** add Graph application permissions or grant admin consent in Entra for this app.

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
