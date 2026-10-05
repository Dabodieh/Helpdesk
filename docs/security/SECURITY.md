# Security

## Principles
Secure defaults, server-side authorization on every operation, least privilege, defence in depth for department isolation, auditable changes, no secrets in the repo.

## Threat areas and controls
| Area | Control |
|---|---|
| Authentication | Entra ID OIDC (auth code + PKCE) → server-side cookie session (HttpOnly, Secure, SameSite=Lax). `IAuthProvider` seam for later providers. No local passwords in v1. |
| Authorization / IDOR / horizontal & vertical escalation | See PERMISSIONS.md: `IAuthorizer`, 404 on inaccessible objects, scoped query filters, role-escalation checks, isolation test matrix |
| Department/team isolation | `department_id` on all scoped rows, scoped filters, system-principal rule for jobs, optional PostgreSQL RLS (decision D-04) |
| CSRF | SameSite cookies + antiforgery token on state-changing requests; no cookie-authenticated GET mutations |
| XSS | React escaping; HTML email sanitised (allow-list, server-side) and rendered in a sandboxed iframe or sanitised container; strict CSP; remote content blocked by default |
| SQL injection | EF Core/parameterised only; raw SQL reviewed |
| Attachments | Size caps, extension + content-type + magic-byte checks, no execution, served with `Content-Disposition: attachment` and `nosniff` from a separate path/origin, AV scan hook (state tracked), hash recorded |
| Graph credentials / secrets | Certificate or secret via env/secret store, never in DB/logs/repo; app-access-policy scoped to department mailboxes; rotation runbook |
| Webhooks / replay | `clientState` validation, strict parsing, idempotent processing keyed on provider message id, delta query as source of truth |
| SSRF | No user-supplied URL fetches; outbound calls only to configured Graph/object-store endpoints; image proxying (if ever) via allow-listed fetcher |
| Rate limiting | ASP.NET rate limiter: per-user API, per-IP anonymous/webhook, per-sender on inbound mail |
| Audit | Append-only `AuditEvent`, app DB role without UPDATE/DELETE on it |
| Logging | Serilog structured; destructuring denylist; never log bodies, tokens, secrets, full addresses where avoidable |
| Background jobs | Run as explicit System principal with department from persisted data; idempotent; retry-safe; Hangfire dashboard restricted to PlatformAdmin |
| Supply chain | Central package management, lock files, Dependabot, minimal dependencies, pinned container base images |
| Transport/headers | HTTPS behind proxy, HSTS, forwarded headers trusted only from configured proxies, security headers |

## Mailbox access model (production target, not configured in this repository)
```
Helpdesk Entra application -> Microsoft Graph -> authorised helpdesk shared mailboxes only
```
- Department mailboxes are Exchange Online **shared mailboxes** (D-07). They are not interactive users; the application never stores or needs a mailbox password, never signs in as a mailbox, and never opens a mailbox user session.
- The application uses its own Entra identity (application permissions, certificate credential preferred). Production access must be **scoped to only the helpdesk mailboxes**, using Exchange Online RBAC for Applications (current supported mechanism; legacy Application Access Policies are the older equivalent) with a management scope/group containing exactly the helpdesk shared mailboxes. Verify the current Microsoft guidance when this is configured. Never grant tenant-wide `Mail.ReadWrite`/`Mail.Send` without that scoping.
- Agents need no Exchange FullAccess/SendAs/SendOnBehalf; helpdesk RBAC decides what they may do and the application sends as the mailbox.
- Exchange configuration is **not** performed by this project's code or repository; it is an operator runbook (to be written in Phase 3) and is never applied to production by an agent session.

## Attachment malware scanning (D-08)
Required before any production release that accepts user attachments. Engine undecided. Design (introduced with attachments in Phase 3): `IAttachmentScanner.ScanAsync(...)` returning `Clean | Infected | Suspicious | ScanFailed | Unsupported`; attachments are **quarantined** (not downloadable or rendered) until `Clean`; `ScanFailed` retries then stays quarantined and visible to admins.

## Secret handling
`.env` and user-secrets are for local dev only and git-ignored; `.env.example` contains placeholders. Production secrets come from the platform secret store/environment. CI runs secret scanning.

## Process
- QA/Security agent performs a focused RBAC/isolation review before each milestone closes (changed areas only).
- Severity: any cross-department leak = Critical, blocks release.
- Risks tracked in `docs/security/RISKS.md`.

## Phase 1 verified isolation guarantees
Proven by the integration suite through the real ASP.NET pipeline (`IsolationMatrixTests`, `AuthorizationDecisionTests`, `AuditTests`, architecture tests): anonymous requests are rejected; non-members, other-department members and other-department administrators receive an indistinguishable 404 (status, headers and body compared with a non-existent id) and change nothing in the database; mismatched or guessed department/team/user ids give the same 404; a platform admin has administrative access but no content access (probe 404); teams are not a visibility boundary; a department id in a body or query never overrides the route; lists, counts and audit never include other departments; unsafe calls without CSRF token or session are refused. Unauthenticated unsafe calls without a CSRF token get 400 (CSRF is validated first); with a token but no session, 401.
Known limitations: `GET /api/users/lookup` searches all active users for any department-members manager or platform admin (returns only id, name, email; privacy review before production); Entra OIDC has been exercised only through configuration/claims tests and dev sign-in, not against a real tenant; Data Protection keys are not persisted (D-13); rate limiting and separate DB roles are still open (BACKLOG).

## Phase 2 verified ticket isolation guarantees
Proven through the real ASP.NET pipeline by `TicketIsolationMatrixTests`/`TicketIsolationLeakageTests` (225 cases, see `PHASE2_THREAT_MODEL.md`): every ticket endpoint against anonymous, no-membership, platform-admin-without-membership, other-department member/admin and all in-department roles returns the contract status and changes nothing when denied; every 404 is byte-identical to the 404 for a random id or reference (no existence oracle via bodies, headers, Location, counts); internal notes appear only to callers holding `tickets.notes.read`, never in lists, search, activity, audit rows or logs, and cannot change kind; invalid team/assignee assignment is refused in the service and by composite foreign keys; queues, filters, search, cursors, requester lookup/counts and queue-counts never include foreign tickets; stored markup is returned verbatim as JSON text; mass-assignment, department-id tampering and missing CSRF are neutralised; revocation (removal, demotion, deactivation) applies on the next request; concurrent writers yield exactly one success and 409 `version_conflict`.
API responses carry `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, and on `/api` `Cache-Control: no-store` and a restrictive CSP; HSTS is enabled outside Development/Testing.
Known limitations: message rows are append-only by convention (the application DB role can still UPDATE/DELETE; DB-role hardening is backlog); removing a department membership silently unassigns that member's tickets; Phase 3 mail must sanitise CR/LF in subjects and angle brackets in requester emails before using them in headers or templates.

## Phase 3 (email) controls and limits
Verified by tests: department derived only from the receiving mailbox (composite FKs; cross-department evidence discarded); webhooks authenticate by per-subscription client state (hash stored, constant-time compare), reveal nothing in responses, are size-limited and are the only CSRF-exempt routes (architecture-tested); loop/noise guard and per-sender rate limit; idempotent ingestion; attachments validated (size, blocked types incl. double extensions, signature check), downloaded only as attachments through ticket authorization, scan gate (`Attachments:RequireCleanScan`); HTML sanitised with the original kept immutable and shown only in a sandboxed frame; no message content in logs or audit; Hangfire dashboard read-only and platform-admin only. Threat notes: `docs/security/PHASE3_THREAT_MODEL.md`.
Open: no malware engine (D-08), no webhook rate limiting, raw bodies stored in `mail.messages` (retention D-09), Graph behaviour unvalidated against a real tenant (ADR-011 gate), Data Protection keys not persisted (D-13).

## Phase 4 (realtime, search, canned responses) controls
- Realtime hub: authenticated, server-decided groups (department, and notes only with tickets.notes.read), content-free payloads, group refresh on membership change and every 60 s, clients cannot subscribe or invoke state changes. Tests: Productivity/RealtimeTests.
- Search: scoped by the same department filter as lists; internal notes searched only with tickets.notes.read; snippets are plain text from messages the caller may read; wildcards escaped; query length 2 to 100.
- Saved views and personal canned responses are owner-only (404 otherwise); department canned responses need department.canned.manage; canned text is plain text inserted by the client, never sent automatically.
- Bulk change authorises and audits every ticket individually; inaccessible and missing tickets are indistinguishable (not_found).
- Audit records the canned-response title (never its text) and view names; logs and audit never contain message bodies or search snippets.
- Limits: single host instance (no backplane), no rate limiting on search or the hub yet (see BACKLOG).
