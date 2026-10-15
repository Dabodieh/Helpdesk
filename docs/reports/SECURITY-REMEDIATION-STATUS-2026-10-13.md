# Security audit remediation status — 2026-10-13

Tracks the 2026-10-09 audit without replacing its original findings. Single-agent work; no production deployment.

## Completed code work

- F-07: new invitation links use fragments, survive sign-in in tab-scoped storage and are redacted in application request paths. Atomic acceptance and Entra cancellation recovery were also fixed. Evidence: `TARGETED-HARDENING-2026-10-12.md`. Legacy proxy logs still require operator handling.
- F-10: the shared host middleware now adds Permissions-Policy denying camera, microphone and geolocation, plus same-origin COOP and CORP. Extended the existing hosting test across SPA HTML, static assets, API errors and a development sign-in redirect. Focused test passed: 1 passed, 0 failed. No database, API payload, UI or authorization changes. ADR-020 and deployment guidance updated. Real proxy/browser verification and HSTS evidence remain open; inline styles remain supported by the existing CSP.

## Security follow-up evidence from 2026-10-12

The prior targeted security pass completed 88 backend tests covering auth/configuration, hosting, administrative content probes, mail rules and selected portal ownership/Internal Note boundaries. All passed. Dependency checks reported no known vulnerabilities across 12 .NET projects (including transitive packages) and 179 npm dependencies. A heuristic scan of 404 tracked source/configuration files found no matching secret candidates; this was not a full repository-history secret scan. No new critical/high vulnerability was confirmed within that scope. These results do not close operational gates or constitute penetration-test evidence.

## Remaining work

| Finding | State / next action |
|---|---|
| F-01 | Retention and data-subject tooling open; D-09 policy approval required before destructive retention. |
| F-02 | Real-host restore drill, scheduled backups and named owner still need evidence. |
| F-03 | The public development tunnel was stopped on 2026-10-13 at the user's request to stop all Docker containers. Vite binds loopback only and the backend listens on localhost. Keep development sign-in off public networks and do not restart the tunnel against this development instance. |
| F-04 | External Data Protection key protection and host/backup encryption decisions open. |
| F-05 | CI dependency, secret, image and SBOM checks open; existing-tool dependency checks are a bounded next slice. |
| F-06 | Administration and webhook rate limiting remain the next application-security slice. |
| F-07 | Application fixes complete; legacy upstream logs/resending remain operational work. |
| F-08 | Absolute session lifetime and recent-authentication policy need a decision. |
| F-09 | Log retention, alert routing and review owner need decisions. |
| F-10 | Missing headers implemented and targeted test passed; deployment evidence remains open. |
| F-11 | Single-instance recovery assumptions need operational acknowledgment. |
| F-12 | Test-only package maintenance remains separate; no urgent vulnerability identified in the recorded scans. |
| F-13 | Live Graph/Entra evidence and scanner decision remain open; attachments stay disabled in production. |

## Interrupted development environment

On resumption, no frontend process listened on 5173. The existing `hd-host` container served HTML and passed readiness on 5080, but returned 404 for `/api/auth/providers`, which exists in current source. It is an older backend image. Started the existing Vite installation on `127.0.0.1:5173`; HTML, transformed entry module and proxied readiness returned 200. Full functionality remains unverified because the backend/frontend versions differ. The public tunnel returned 502 and points to `http://<dev-pc-lan-address>:5173`.

Recovery update (2026-10-13): the user requested stopping all Docker containers. All six running containers were stopped, including the unrelated `itrepairtracker-web`; only `helpdesk-postgres-1` was restarted. The current backend was started from source on localhost:5080 against that PostgreSQL instance, with startup migrations applied. Vite remained on 127.0.0.1:5173. Readiness and auth-provider API requests through Vite returned 200. A headless Edge check rendered the local sign-in screen without page errors; development sign-in reached the dashboard and showed existing ticket counts without page errors. The public tunnel remains stopped. The previous approval-review rejection applied to the earlier background-start attempts; this foreground startup succeeded.

Public route check: the user reported Cloudflare 1033 after the connector was stopped and stated Cloudflare Access is enabled. A brief connector restart while the local origin remained unavailable yielded HTTP 502 to an unauthenticated request, with no observed Access redirect. The connector was stopped again. Access enforcement for the exact hostname still needs verification before connecting the Development app, because its dev sign-in grants platform administrator access. No public origin traffic was restored.
