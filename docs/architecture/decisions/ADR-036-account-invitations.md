# ADR-036: Account invitations

Status: accepted

## Context

Administrators need to set up a person before that person has ever signed in, and the person should be told by email. Helpdesk identities come from Microsoft Entra ID (accounts are created on first sign-in); Helpdesk holds no passwords. A department membership needs an existing user row.

## Decision

- A **platform administrator** (permission `platform.users.manage`) creates an invitation: name, email, department, role. Nothing is granted yet and no user row is created.
- Helpdesk emails a link `<base>/activate#<token>` from the dedicated **system mailbox** (`Mail:SystemMailbox`), never from a department mailbox. The token is 256 random bits; only its SHA-256 hash is stored and it is never logged or audited. The link is shown to the administrator once (create or resend) because email may be unavailable locally.
- The person opens the link, signs in with **Microsoft 365** (the normal sign-in), and presses *Activate*. The server adds the membership only when the signed-in account's address equals the invited address (case-insensitive) and the invitation is open: a forwarded link grants nothing to someone else. Email remains a mutable contact and the sign-in remains the proof of identity, so this does not restore email-based claiming of history.
- Activation and any new membership commit in one Organisation transaction with their audit records. Membership creation shares the existing Department lock and validation; an existing member keeps their role even during concurrent administration. The claim rechecks the token hash so a concurrently replaced link cannot be accepted. Cache invalidation and best-effort access notifications follow the commit.
- The browser removes the fragment (or legacy token path) with history replacement before rendering sign-in, retains the token in tab-scoped session storage through Microsoft sign-in, and sends it only in the CSRF-protected preview/accept POST bodies. The login return URL is `/activate` without the secret. Clear storage after success or an invalid preview. Legacy path links remain supported; the application request logger redacts their path. Reverse proxies must redact legacy `/activate/…` paths separately; past log entries are not removed by this change.
- Single use, expires (`Invitations:ExpiryDays`, default 7), revocable, resend issues a new link and invalidates the old one. At most one open invitation per address and department. An existing membership keeps its role. An unusable link is a plain 404.
- Sending is one attempt with no automatic retry (`ISystemMailer`, SharedKernel, implemented in Mail). The outcome (accepted / failed / uncertain / not configured) is stored and shown; an uncertain send is never repeated by the system. Production needs `Invitations:PublicBaseUrl`; Development and Testing fall back to the address the administrator is using.
- Schema: `organisation.invitations` (additive migration `AddInvitations`).

## Not included

Passwords or any local sign-in, creating accounts in Microsoft 365, department administrators inviting for their own department, bulk import. The Graph send path is unvalidated against a real tenant (D-16), like all Graph behaviour.
