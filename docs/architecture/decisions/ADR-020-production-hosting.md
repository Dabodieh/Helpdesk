# ADR-020: Production hosting and response security headers

Status: Accepted (post-Refinement-10 remediation, WS-5; decision D1; findings SRE-001, SRE-006, SRE-010, SRE-014, SEC-009, SEC-010, SEC-011)

## Decision
1. **One deployable.** The ASP.NET Core host serves the built SPA (`wwwroot`, produced by the Node stage of `docker/host.Dockerfile`), the API and SignalR. Client routes fall back to `index.html`; `/api`, `/hubs`, `/health`, `/hangfire` and `/openapi` are never answered with the page. Development keeps Vite. `Spa:Root` overrides the web root (tests).
2. **Nginx is the supported reverse proxy** (`docker/production/`): TLS, HTTP and WebSocket proxying, `Host`, `X-Forwarded-For` and `X-Forwarded-Proto` overwritten from the connection. The application depends on nothing Nginx-specific.
3. **The application owns the response headers**: API `default-src 'none'`; the job dashboard `frame-ancestors 'none'`; everything else the page policy (same-origin scripts and connections, `frame-ancestors 'none'`) plus `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy: no-referrer`. The proxy adds none.
4. **Forwarded headers (SRE-006, verified).** With both `KnownProxies` and `KnownNetworks` empty the framework trusts every caller. The host now clears the loopback default, reads `Proxy:KnownProxies` and `Proxy:KnownNetworks` (CIDR), and enables the middleware only when something is configured. Since S-03 it also fixes `ForwardLimit` at 1 (only the rightmost X-Forwarded-For entry), and rate-limit buckets use the IPv4 address or the IPv6 /64 of the client.
5. **Start-up guards** outside Development/Testing: a proxy must be configured, `AllowedHosts` must not be `*`, `Storage:Provider` must not be `InMemory`, `Attachments:RequireCleanScan` must be true.
6. **Realtime origin (SEC-009, verified).** A SignalR WebSocket carrying an `Origin` must come from the site's own host or `Realtime:AllowedOrigins`; otherwise 403. Before this the hub accepted any origin.
7. `data:` URLs survive the mail sanitiser only on images (SEC-010). Compose images are pinned (PostgreSQL 18.6, SeaweedFS 4.48, Nginx 1.29.8).

## Consequences
- F-10 follow-up (2026-10-12): every application response also carries `Permissions-Policy: camera=(), microphone=(), geolocation=()`, `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Resource-Policy: same-origin`. These deny unused device capabilities, isolate cross-origin opener relationships and restrict cross-origin no-cors embedding. Authentication uses full-page redirects; any future popup login or cross-origin resource consumer requires review. See the security remediation plan for browser-reference research. COEP is not enabled.
- Frontend work that needs inline scripts, remote scripts or cross-origin connections must change the page policy deliberately.
- Windows/Linux container behaviour behind a real Nginx and a real browser CSP check is a deployment verification, not covered by the simulator tests.
