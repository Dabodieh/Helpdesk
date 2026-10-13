# Production hardening, phase 1 — 2026-10-13

Scope: prepare the existing application and the existing Windows Docker production-style stack (`docker/production/compose.yml`, `docker/windows/.env.example`, `scripts/windows`) for a secure production deployment. Nothing was deployed, restarted, exposed or committed; the development instance, its database and the E2E stack were not touched; no Microsoft 365 call was made. Status: **not production ready** (see section 4).

## 1. Safeguards already in place (verified by reading the code and its tests)

| Area | Existing control |
|---|---|
| Development sign-in | `Authentication:DevSignIn:Enabled` is refused outside Development/Testing at start-up (`AuthenticationOptions`, test `AuthConfigurationTests`); the route is not even mapped unless enabled. |
| Cookies | Session cookie `HttpOnly`, `SameSite=Lax`, `Secure=Always` outside Development/Testing, 8 h sliding. Antiforgery cookie `SameSite=Strict`, secure outside development. CSRF header required on unsafe requests. |
| Proxy | Forwarded headers honoured only from `Proxy:KnownProxies`/`KnownNetworks`; with none configured nothing is trusted and production refuses to start. `AllowedHosts` may not be `*`. |
| Headers / origin | CSP (page and API), nosniff, referrer policy, Permissions-Policy, COOP/CORP, `X-Frame-Options`, HSTS outside development; SignalR WebSocket origin check; no CORS. |
| Data Protection | Key ring persisted in PostgreSQL (`identity.data_protection_keys`) so sessions and the stored Entra secret survive container replacement; included in the encrypted backup. |
| Configuration validation | Production refuses: missing proxy, `AllowedHosts=*`, `Storage:Provider=InMemory`, `RequireCleanScan=false`, `Attachments:Enabled=true` (no scanner, D-08). Windows `helpdesk.ps1` validates the env file (names only, never values), TLS files, project name, subnet and port conflicts. |
| Throttling | Per-user limits on portal create, portal reply and search; request body limit 2 MB. |
| Operations | Compose: Postgres health check, `restart: unless-stopped`, explicit one-off `migrate` profile, TLS-terminating Nginx pinned at a fixed address; Windows scripts for encrypted verified backup, scheduled backup, restore, restore test and `update` with a mandatory backup first. |
| Logging | Serilog console; message bodies and tokens are not logged (project rule, tests for invitation token redaction). |
| Mail | Graph application access without mailbox passwords; delivery states distinguish queued/accepted/failed/uncertain; NDRs never become tickets; webhook validation-token handshake and client-state check; single-instance topology (ADR-021). |

## 2. Improvements made in this phase

| # | Change | Files |
|---|---|---|
| 1 | **Infrastructure rate limits (F-06).** Sign-in/OIDC (`/api/auth/*` except the cheap provider probe) 60/min per client address; Graph webhooks 600/min per address; invitation preview/accept 20/min per user; administrative writes (`/api/admin/*`, Department administration but not ticket work) 120/min per user. Always on outside Development/Testing with no switch to turn them off; opt-in in tests. 429 with `Retry-After` and the existing `rate_limited` problem code. Values are provisional and configurable under `RateLimits`. | `Hosting/AbuseControls.cs`, `Program.cs` |
| 2 | **More fail-closed start-up checks.** Production now also refuses an empty `Database:ConnectionString`, `Database:MigrateOnStartup=true` (migrations stay an explicit step), non-https `Realtime:AllowedOrigins`, and `Serilog:MinimumLevel:Default` of Debug/Verbose (SQL parameters and bodies must never reach logs). | `Hosting/ProductionHosting.cs` |
| 3 | **Compose hardening.** `cap_drop: [ALL]` and `no-new-privileges` on the application and migrate services, `no-new-privileges` on Nginx, and a `BIND_ADDRESS` variable (default `0.0.0.0` so existing deployments are unchanged) for the published proxy ports. | `docker/production/compose.yml` |
| 4 | **Windows private-by-default.** `docker/windows/.env.example` sets `BIND_ADDRESS=127.0.0.1`; `helpdesk.ps1` config check rejects a `BIND_ADDRESS` that is not an IP address. | `docker/windows/.env.example`, `scripts/windows/lib/Common.ps1` |
| 5 | Guide updated with the new refusals, limits and `BIND_ADDRESS`. | `docs/guides/PRODUCTION_DEPLOYMENT.md` |
| 6 | Tooling note: while the isolated E2E backend runs from source it locks `src/*/bin`; backend tests were built with `-p:ArtifactsPath=<scratch>` so the E2E stack was never touched. | none |

## 3. Security tests (targeted; development environment unaffected)

| Test | Result |
|---|---|
| Development sign-in refused in Production (`AuthConfigurationTests`) | pass (existing) |
| Production refuses: no proxy, `AllowedHosts=*`, in-memory storage, scan not required, attachments on, **MigrateOnStartup on, http realtime origin, Debug logging** (`ProductionHostingTests` theory) | pass |
| A refused start names settings but never prints their secret values (canary secret, new) | pass |
| Rate-limit partition rule: webhooks and sign-in by address, provider probe unlimited, invitations/admin writes by user, reads and ticket work not limited (new) | pass |
| Limits cannot be switched off in Production, opt-in in Development/Testing (new) | pass |
| Sign-in and webhook endpoints return 429 after the configured permits through the real pipeline; `/api/auth/providers` stays open (new) | pass |
| `Helpdesk.Host.Tests.Platform` and `.Identity` groups | 111 / 111 pass |
| Architecture suite (authorization metadata, module boundaries) | 76 / 76 pass |
| Compose renders with the new keys (`docker compose config`, no containers started) | pass |
| `Common.ps1` parses | pass |

Not run (outside the scope of these changes, per the testing rules): full backend suite, Playwright, image build, migration round-trip.

## 4. Outstanding blockers (production gates, unchanged unless noted)

1. **Live Graph/Entra validation (D-16)** has never been run against a real tenant; simulator results are not evidence.
2. **Malware scanning (D-08):** attachments stay disabled in production.
3. **Restore drill and backup custody:** restore has been proven on the development database and tooling, but there is no production drill, no named owner, no offsite copy of the production backup and no scheduled run (`backup-init`, `backup-schedule` not performed).
4. **Data Protection keys not encrypted at rest (F-04)** and **no CI dependency/secret/image/SBOM scan (F-05).**
5. **Retention/data lifecycle policy (D-09)** unapproved; **log retention and alert routing (F-09)** undecided.
6. **Absolute session lifetime and recent-authentication policy (F-08)** undecided (currently 8 h sliding, so an active session never expires).
7. **Production compose requires Entra variables** (`ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `ENTRA_CLIENT_SECRET`) although ADR-037 makes the application able to start without sign-in and to connect Entra from the product. The two paths disagree (decision below).
8. No Windows production `.env`, TLS material or tenant exists yet; the development instance is a Development-mode container and is not a production candidate.

## 5. Microsoft 365 requirements (all need a tenant administrator; nothing was requested or created)

- **Staff sign-in:** one single-tenant Entra application registration with the OIDC redirect URI `https://<public host>/api/auth/signin-oidc` and delegated `openid`, `profile`, `email` only. The guided setup (ADR-037) can create it by device-code sign-in as an administrator; it needs permission to create applications.
- **Mail application:** a separate Entra application with an app credential (certificate preferred, secret supported) and **Exchange RBAC for Applications**: `Mail.ReadWrite` and `Mail.Send`, scoped by a management scope to the Helpdesk shared mailboxes only. Tenant-wide Graph `Mail.*` consent must not be granted (it removes the scoping).
- **Mailboxes:** one Exchange Online shared mailbox per Department, created by a tenant administrator (`MailboxCreation` is manual; automatic creation is D-18 and needs a separate Exchange admin identity). The application never holds mailbox passwords.
- **Webhooks:** the public https host named in `Mail:NotificationBaseUrl` must be reachable by Microsoft Graph. This is the opposite of keeping the application private: D-19 (polling-only mail for private installs) is undecided.
- **Evidence still missing:** the 38 experiments in `docs/guides/GRAPH_TENANT_VALIDATION.md` (scoping, delta, subscriptions, reply threading, isolation). Department isolation of mailboxes is enforced in the application (mailbox row to Department, per-ticket stored mailbox, no fallback) and covered by tests, but not yet observed against Exchange.

## 6. Decisions needing your approval

1. **Session policy (F-08):** add an absolute maximum lifetime (for example 12 h) and a recent sign-in requirement for administration actions? Today a continuously active session never expires.
2. **Data Protection key protection (F-04):** certificate-wrapped keys (certificate file in a read-only mount), or accept database/backup protection as the boundary for a single-PC deployment?
3. **Compose vs guided Entra setup:** see 9.3. Recommendation: keep `ENTRA_*` required in production compose; with development sign-in forbidden nobody can sign in to run the wizard on a brand-new install, so server settings are the only safe bootstrap.
4. **`BIND_ADDRESS` default:** keep `0.0.0.0` for the Linux reference stack (as now), or default to `127.0.0.1` everywhere and require an explicit public bind?
5. **Rate-limit numbers:** accept the provisional defaults (section 2.1) for the first pilot, or give me expected user numbers to tune them?
6. **Public reachability:** a Graph-webhook design needs a public https endpoint (tunnel or reverse proxy). Which exposure model do you want for the pilot: public endpoint with Cloudflare Access/WAF in front of everything except the webhook paths, or the polling-only mode (D-19)?
7. **Log policy (F-09):** retention period and who reviews alerts.

## 7. Recommended next milestone

Stand up the Windows production-style stack **privately** (`BIND_ADDRESS=127.0.0.1`, own project name, own TLS certificate, a throw-away or approved test tenant) from the committed image: run `update`, confirm refusal of each unsafe setting once on purpose, take and restore-test the scheduled encrypted backup, then run the Graph validation runbook against a test tenant with a test shared mailbox. That closes gates 1, 3 and most of 8 without exposing anything.

## 8. Phase 2: private production validation (2026-10-14)

### 8.1 Isolated stack

`helpdesk.ps1` (existing tooling) with a git-ignored `docker\windows\.env`: project `helpdesk-pv`, `ASPNETCORE_ENVIRONMENT=Production`, image `helpdesk-host:pv`, own volumes `helpdesk-pv_pgdata` (database and Data Protection key ring) and `helpdesk-pv_objectdata`, own networks, fixed subnet 172.31.88.0/24, fake tenant/client GUIDs, throw-away random secrets, local self-signed certificate for `localhost`, SeaweedFS local object store, `BIND_ADDRESS=127.0.0.1`, published ports 8189 and 8543 on 127.0.0.1 only (nginx only; database, object store and application publish nothing). A new overlay `docker/windows/compose.no-egress.yml` (enabled by `NO_EGRESS=true`) places the application, database and object store on an internal Docker network with no route out. Nothing was shared with `hd-host`, `helpdesk-postgres-1`, the E2E stack or Cloudflare (not running). The real SAMH tenant, any test tenant and live mailboxes were not contacted; the fake Entra/Graph identifiers cannot authenticate anyone, so signed-in flows could not be exercised (a blocker for Entra validation).

### 8.2 Findings fixed during validation

| Finding | Fix |
|---|---|
| **The `MigrateOnStartup` refusal fired too late.** Start-up options validation runs when the host starts, but migrate-on-startup executes before that. In the real image `MigrateOnStartup=true` began applying migrations before the guard could refuse (the in-process test did not show it). | `Program.cs` now refuses `MigrateOnStartup` outside Development/Testing before it touches the database. Proven in the image with an unreachable database: `refused=True touchedDatabase=False`. |
| The application container had no health check (only PostgreSQL did). | Compose health check (bash TCP request to `/health/live`; `sh` has no `/dev/tcp`). The container reports `healthy`. |
| No way to guarantee a validation stack cannot call Microsoft. | `NO_EGRESS` overlay (above); verified: outbound TCP to 1.1.1.1:443 "Network is unreachable", no DNS for `login.microsoftonline.com`. |

### 8.3 Controls validated

| Control | Evidence | Result |
|---|---|---|
| Production environment, no development sign-in | `ASPNETCORE_ENVIRONMENT=Production`, no DevSignIn variable; `/api/auth/dev-login` returns 404; enabling it makes the image refuse to start | PASS |
| Fail-closed settings in the real image (database never contacted) | refusal observed for: DevSignIn on, defaults (Graph and https base URL missing), MigrateOnStartup, Debug logging, http realtime origin, attachments on, no trusted proxy with wildcard host | PASS |
| Unauthenticated access | `/api/me`, `/api/tickets`, `/api/departments`, `/api/admin/microsoft-365`, `/api/portal/forms`, `/hangfire` all 401; negotiate without CSRF 400; only the intentional `/api/auth/providers` and health answer anonymously | PASS |
| HTTPS and proxy | HTTP 8189 redirects to HTTPS; CSP, X-Frame-Options, nosniff, Referrer-Policy, Permissions-Policy present; CSRF cookie `Secure`, `HttpOnly`, `SameSite` | PASS |
| HSTS | Not emitted: the framework never sends HSTS for the host `localhost` | **Not verified** (needs a real host name) |
| Forwarded header trust | proxy address pinned and required; a spoofed `X-Forwarded-For` does not change the limit key (integration test, plus 75 requests with 75 different spoofed addresses through nginx: 16 x 429) | PASS |
| Rate limiting | `/api/auth/login` limited at 60/min per address; provider probe and ordinary pages unaffected | PASS |
| Data Protection persistence | one key in `identity.data_protection_keys`, unchanged after an application restart | PASS |
| Data Protection **encryption at rest** | the stored key XML contains the key value in clear | **Open gate (F-04)** |
| Migrations on an empty isolated database | all seven modules applied, latest ids equal the source; tickets and users 0 / 0 | PASS |
| Logs | no ERR/FTL/WRN; none of the 5 configured secret values appears in application or proxy logs | PASS |
| Health | live and ready 200 through the proxy; application container `healthy` | PASS |
| Neighbours | `hd-host`, `helpdesk-postgres-1`, E2E database unchanged; no cloudflared container running | PASS |

Behaviour to note: with the identity provider unreachable, `/api/auth/login` returns 500 rather than a friendly page (expected here because there is no egress; worth a graceful message before a pilot). A harmless logged `ERR` appears during the first migrate run when a new module's history table does not exist yet.

Tests this phase: `ProductionHostingTests`, `AuthConfigurationTests`, `HealthEndpointTests`, `AbuseControlTests` 29 passed (new: forwarded-header spoofing test); earlier in the work the Platform and Identity groups (111) and the Architecture suite (76) passed (section 3). The full backend suite, Playwright and the frontend suite were not rerun (no frontend change).

### 8.4 The eight requested checks

1. **Rate limits vs forged client address:** the key is the connection address after trusted-proxy processing only; with no trusted proxy nothing is read from headers; behind the pinned proxy the spoof test passes.
2. **Proxy trust explicit:** required at start-up, never defaulted.
3. **DevSignIn:** refused in Production at start-up; route absent.
4. **Fail closed:** see 8.3; the ordering defect found and fixed in 8.2.
5. **Authentication and permissions:** Architecture suite 76/76 and the authorization-metadata tests unchanged.
6. **Key encryption before real use:** required, not implemented; the mechanism needs your decision (9.2).
7. **ADR-037 bootstrap:** not weakened. The wizard requires a signed-in platform administrator; on a brand-new production install nobody can be signed in (DevSignIn is forbidden), so the first Entra settings must come from server settings, after which the wizard refuses by design (server settings always win). The guided flow therefore cannot bootstrap a production install; it fits rotation and installs that start without server settings, which production compose currently forbids.
8. **Webhook traffic:** 600/min per sender address is far above a small tenant's volume; Graph retries 429 and the reconcile job every 5 minutes recovers anything dropped, so limiting cannot lose mail. A mis-pinned proxy would make all webhook traffic share one key; the start-up proxy requirement prevents that configuration.

## 9. Policy proposals and decisions needing your approval

Nothing below was implemented as policy.

| # | Proposal | Status / what implementing it involves |
|---|---|---|
| 9.1 | Absolute session lifetime 12 h, inactivity timeout 60 min | Today: 8 h sliding. Needs an absolute-expiry value checked per request (identity module change) and agreement on the re-sign-in experience. **Approval required.** |
| 9.2 | Data Protection key encryption at rest | Options: certificate-wrapped key ring (certificate file mounted read-only, rotation procedure) or accept the database and its backups as the boundary for a single-PC pilot. **Approval and a certificate decision required.** |
| 9.3 | Entra bootstrap and compose | Keep `ENTRA_*` required (recommended, 8.4.7) or make them optional with a different first-admin bootstrap. **Approval required.** |
| 9.4 | `BIND_ADDRESS=127.0.0.1` | Already the Windows example default; compose default stays `0.0.0.0` for the Linux reference stack. Approval to change the compose default. |
| 9.5 | Existing rate-limit values | Retained, documented as provisional. |
| 9.6 | Polling-only Graph mode until webhooks are validated | Not available: D-19 is undecided and the host requires a public https `Mail:NotificationBaseUrl`. Implementing it is a feature and needs approval. |
| 9.7 | Log retention: operational 30 days, security 90 days | Policy only; nothing enforces retention (Serilog console to Docker). A log shipper or Docker log-driver rotation is a deployment decision. **Approval required.** |

## 10. Outstanding blockers after Phase 2

Unchanged gates: live Entra/Graph validation (D-16), malware scanner (D-08), retention policy (D-09), key encryption (F-04), CI scanning (F-05), production backup custody and restore drill, session policy (F-08), log policy (F-09). Sharpened: authenticated flows cannot be validated until a real or test tenant is connected; HSTS needs a real host name; polling-only mail does not exist; graceful handling of an unreachable identity provider.

## 11. Next steps for Entra/Graph validation

1. Decide the tenant (a Microsoft 365 developer or test tenant is recommended) and approve creating the sign-in and mail applications there.
2. In the private stack, replace the fake `ENTRA_*` and `GRAPH_*` values with the test tenant's, turn `NO_EGRESS` off (the application then needs `login.microsoftonline.com` and `graph.microsoft.com`), and list a test administrator as `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT`.
3. Run `docs/guides/GRAPH_TENANT_VALIDATION.md` with one test shared mailbox; the webhook experiments need a public https address, so decide 9.6 first.
4. Rehearse `helpdesk.ps1 backup-init`, `backup` and `restore-test` on this stack before any real data exists.

The private stack is left running on loopback (ports 8189 and 8543). Stop it with `.\scripts\windows\helpdesk.ps1 stop`; its volumes (`helpdesk-pv_*`) are removed only deliberately.

## 12. Phase 3: Data Protection encryption, sign-in failure handling, tenant preflight (2026-10-14)

### 12.1 Data Protection key encryption at rest (F-04)

**Recommendation:** certificate-wrapped key ring using the standard ASP.NET Core mechanism (`ProtectKeysWithCertificate`), because it works unchanged in Linux containers, needs no Windows-only API (DPAPI and DPAPI-NG are unavailable in the container), keeps the key ring in PostgreSQL (persistent, in the backup), and moves the secret to a PFX that the operator mounts read-only from outside Git and the image. A cloud key vault would be stronger but adds an external dependency and a tenant resource; Windows DPAPI on the host cannot be reached from the container.

**Implemented (opt-in, nothing enabled):** `DataProtection:CertificatePath`, `CertificatePasswordFile` (or `CertificatePassword` for non-production), `PreviousCertificatePaths` for rotation, `RequireCertificate` for a production refusal. Existing keys are never rewritten or revoked; new keys are encrypted. A missing, unreadable, expired, wrong-password or public-only certificate stops the start with a message that names the setting but never a password or file contents. Operator procedure (storage, ACLs, rotation, backup of the PFX separately from the database, recovery): `docs/guides/PRODUCTION_DEPLOYMENT.md`.

**Evidence:** 5 new tests (plain by default; encrypted when configured and still readable after a restart; unreadable without the certificate; rotation keeps old keys readable; failure modes never echo the password canary; password from a secret file) all pass. In the real image: `RequireCertificate=true` without a path and a certificate path that is not mounted are both refused before the database is touched.

**Not done and needs approval:** generating or choosing the production certificate, its validity and custody, enabling `RequireCertificate`, and re-protecting existing keys (which would revoke the ring and sign everyone out).

### 12.2 Identity provider failure (HTTP 500)

**Cause:** `/api/auth/login` returned the authentication challenge as a result; the OpenID Connect handler runs its discovery request (`login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration`) while executing that result, so an unreachable or unusable provider raised `InvalidOperationException` (IDX20803) outside the endpoint's control and surfaced as an unhandled 500.

**Fix:** the endpoint now performs the challenge itself and catches the failure. The user is redirected to the sign-in page with `authError=unavailable` and a reference (the request trace identifier); the page shows "Microsoft sign-in is temporarily unavailable. Try again in a few minutes…" with that reference. The exception and its detail are logged server-side under the same reference (`Microsoft sign-in could not be started`). No session or cookie is created and there is no fallback or bypass. Callback failures (`OnRemoteFailure`) keep their safe redirect and now log with the same kind of reference. Client cancellation is not treated as a provider failure.

**Evidence:** new test with an unreachable metadata manager (302 to `/?authError=unavailable&ref=…`, exception text absent from the response, no `helpdesk.session` cookie, `/api/me` still 401); in the private stack with no egress the real response is `302 /?authError=unavailable&ref=…` and the log line carries the same reference. Existing login tests (challenge parameters, return-URL sanitising, not-configured 404) pass.

### 12.3 Production configuration decisions (documented, not silently enforced)

| Topic | State |
|---|---|
| `ENTRA_*` and ADR-037 | Compose keeps them required. Consistent with ADR-037: development sign-in is forbidden in production, so the first Entra settings must be server settings; the wizard then refuses by design and is for rotation or installs that start without server settings. |
| Loopback binding | `BIND_ADDRESS` default stays `0.0.0.0` for the Linux reference stack (unchanged behaviour); the Windows example and the check default to `127.0.0.1`. Changing the compose default awaits approval. |
| Trusted proxy, no development authentication, secure cookies, rate limits | Unchanged and verified (sections 1, 3, 8). |
| No-egress overlay | `docker/windows/compose.no-egress.yml` is only applied when `NO_EGRESS=true`; the base compose files and the example default are unaffected (`NO_EGRESS=` empty). |
| Session lifetime proposal (12 h absolute, 60 min inactivity) | Not enforced; today 8 h sliding. Needs identity-module work and approval (9.1). |
| Log retention proposal (30 days operational, 90 days security) | Not enforced; no component applies retention. A deployment decision (9.7). |
| Polling-only vs reachable webhooks | Polling-only mail does not exist; webhooks need a public https address. Decision D-19 (9.6). |

### 12.4 Microsoft 365 test-tenant preflight

Checklist: `docs/guides/M365_TEST_TENANT_CHECKLIST.md` (entry criteria, tenant objects, redirect URI `https://<host><suffix>/api/auth/signin-oidc`, Exchange RBAC for Applications scope and consent rules, outbound `login.microsoftonline.com` and `graph.microsoft.com`, inbound webhook paths, and the ordered sign-in, inbound, outbound, threading, isolation, failure and outage tests). One correction found while writing it: `docker/windows/.env.example` named the redirect path `/signin-oidc`; the application's callback is `/api/auth/signin-oidc` (fixed in the example). No tenant was contacted and the no-egress configuration was not changed.

### 12.5 Validation summary

| Check | Result |
|---|---|
| Targeted backend tests (DataProtection certificate, auth configuration, production hosting, auth endpoints, Entra setup) | 56 passed |
| Architecture suite | 76 passed |
| Frontend unit tests (sign-in message added) and typecheck | 200 passed, clean |
| Real image, Production mode: DP guard refusals, healthy start, login failure response and log reference | verified in `helpdesk-pv` |
| Not run | full backend suite, Playwright, image push; private stack left running on loopback without egress |
