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
