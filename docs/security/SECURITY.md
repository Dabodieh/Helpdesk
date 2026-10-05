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

## Secret handling
`.env` and user-secrets are for local dev only and git-ignored; `.env.example` contains placeholders. Production secrets come from the platform secret store/environment. CI runs secret scanning.

## Process
- QA/Security agent performs a focused RBAC/isolation review before each milestone closes (changed areas only).
- Severity: any cross-department leak = Critical, blocks release.
- Risks tracked in `docs/security/RISKS.md`.
