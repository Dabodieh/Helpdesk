# Pre-production master audit — 2026-10-15

Single-agent, read-only audit of the SAMH Helpdesk working tree. No application code, infrastructure, Production, Cloudflare, Entra or Exchange was modified. No commit, push or deploy. No secret values appear in this report.

## 1. Executive summary and verdict

**Verdict (revised): NOT READY for organisational launch; sound for the current single-administrator private pilot once availability is restored.** The application code is in good shape (no confirmed CRITICAL vulnerability; 1 107 backend + 200 frontend + 13 browser + 9 script = **1 329** tests pass; authorization and isolation are systematically enforced) and the live Entra and Graph scenarios listed in section 11 have passed in Production. Readiness is blocked by **operational and evidence gaps**, not code defects:

1. What is deployed matches no Git commit (it contains uncommitted code).
2. Production is down after a Windows Update reboot and cannot restart on its own; it shares one Postgres container with Development on one personal PC.
3. Production backups exist but are manual, on this PC only, not scheduled and not fully restore-proven.
4. Live Entra/Graph validation passed for the core scenarios; NDR, lifecycle, renewal and several negative paths remain untested.
5. No user deactivation workflow (currently masked by a single-account Cloudflare Access allow-list).

| Severity | Count |
|---|---|
| CRITICAL | 0 |
| HIGH | 3 |
| MEDIUM | 13 |
| LOW | 13 |
| IMPROVEMENT | 2 |

Not claimed: that the application is vulnerability-free; ASVS L2 certification; live-tenant behaviour. See section 8 for the limits of this audit.

## 2. Revision and environment audited

| Item | Evidence |
|---|---|
| Git HEAD | `7838f35acf322caa590f599ce4fd6ea9e4993585`, branch `claude/optimistic-keller-a6385x` (only branch, local = GitHub `origin`, verified with `git ls-remote`) |
| Working tree | 19 tracked files modified (+525/−8 lines, includes 8 files under `src/`, Mail module and web), 16 untracked paths (Windows scripts, compose overlays, `tests/Helpdesk.Scripts.Tests/`, guides) |
| "Production" runtime | Container `helpdesk-backend`, image `helpdesk-host:local` (`sha256:3509b9cd…`, built 2026-10-14 20:24 +01:00, 20 h after the last commit), .NET/ASP.NET 10.0.13, `ASPNETCORE_ENVIRONMENT=Production`, DevSignIn off, attachments off, Graph mail, S3 store, DP certificate required, migrate-on-start off, no published ports |
| Production state at audit time | `helpdesk-backend` restart count 20, health `starting`, crashing in `KeyRingReadabilityCheck` (cannot reach Postgres). `helpdesk-postgres-1` **Exited (255)** at 08:31 UTC, restart policy `no`. `helpdesk-cloudflared-1` Exited (255). Only `objectstore-prod` is up. |
| Database migration state | **Not verified**: the Postgres container is stopped and starting it would have modified Production state. Migration files in the image were not compared against the DB. |
| Test host | Throw-away `postgres:18.6` container (tmpfs, removed afterwards), isolated E2E stack on loopback (stopped afterwards). Production data untouched. |

### Divergence: source vs GitHub vs running image
- Local HEAD = GitHub HEAD (no divergence).
- Running image ≠ HEAD: `Helpdesk.Modules.Mail.dll` in the running image contains `RetrySubscriptionAsync`; the previous image `helpdesk-host:pre-subretry` and HEAD do not. That symbol exists only in the **uncommitted** diff of `MailEndpoints.cs`/`MailboxService.cs`/`MailSync.cs`. The deployed binary therefore cannot be reproduced from any commit (finding H-01).

## 3. Security findings

Status key: **CONFIRMED** (reproduced or directly evidenced), **SUSPECTED** (code reading, not exercised), **ASSUMPTION** (depends on an untested external fact).

| ID | Sev | Status | Blocks Prod | Finding |
|---|---|---|---|---|
| S-01 | MEDIUM (was HIGH) | CONFIRMED | Org launch | **Leaver keeps access.** The session cookie is stateless (`AuthenticationSetup.cs:36-37`, 8 h sliding, no absolute cap). `CurrentUser.LoadAsync` (`Services/CurrentUser.cs:27-37`) re-checks only `users.is_active`, and nothing ever clears it: no code sets `IsActive = false` and there is no endpoint (D-14). Disabling the account in Entra does not end an active session; a used cookie renews indefinitely. Logout (`AuthEndpoints.cs:58-63`) only drops the local cookie. **Mitigation found in revision 2:** the whole hostname sits behind a Cloudflare Access allow-list of one named account with an 8 h session (independently re-confirmed: 302 to Access on `/` and `/api/me`), so today a leaver is also stopped by Access within 8 h. That is a pilot-only control; it returns to HIGH the moment Access is widened to the organisation. **Fix:** deactivation endpoint + audit + unassign; absolute session cap (about 12 h) via sign-in-time claim. **Verify:** deactivated user's next request is 401; cookie older than the cap is rejected. |
| S-02 | MEDIUM | CONFIRMED | P1 | **Platform Admin can self-grant department membership with no second control or notice** (`MembershipService.cs:59,90,191`, D-11). Role Admin→DeptAdmin gives Internal Note and content access in one step; it is audited with `selfGrant` but nobody is notified. Violates the intent of "Platform Admin has no implicit ticket access". **Fix:** notify Department admins/other platform admins or require dual control. **Verify:** integration test of notification/approval. |
| S-03 | MEDIUM | ASSUMPTION | P1 | **Per-IP rate limits depend on the real client address.** `AbuseControls.cs:92-103` keys webhook and sign-in limits on `RemoteIpAddress`. Production trusts forwarded headers from `172.16.0.0/12` (`docker-compose.yml` `Proxy__KnownNetworks__0`) and relies on Cloudflare appending the client IP as the rightmost `X-Forwarded-For`. If cloudflared/Cloudflare do not present it that way, all users share one 60/min sign-in bucket (self-inflicted lock-out) or an attacker evades it. The reference `nginx.conf` sets `X-Forwarded-For $remote_addr`, which would hide the client behind a tunnel. **Verify:** one request through the real tunnel and inspect the address the host sees (via logs of a test endpoint, not by exposing Development). |
| S-04 | MEDIUM | CONFIRMED | P1 | **Runtime uses the database owner/superuser.** One `POSTGRES_USER` (`helpdesk`) and password serve migrations, runtime and all databases on the shared server; Development host and Production app share it, so a Development-mode process can read `helpdesk_prod`. Backlog "dedicated DB roles" open. **Fix:** separate migration role and a least-privilege runtime role; separate Postgres instance for Production. |
| S-05 | MEDIUM | CONFIRMED | P1 | **Key ring and database not encrypted at rest** (F-04). Mitigation exists (certificate-protected key ring, `RequireCertificate=true` in compose, validated 2026-10-14) but custody of the certificate/password and backup encryption of the live DB are undecided; plain keys already in a ring are not migrated until they roll. |
| S-06 | MEDIUM | CONFIRMED | P1 | **CI not effective and no dependency/secret/image scanning** (F-05). `.github/workflows/ci.yml` runs only on pull requests and pushes to `main`; the repository has no `main` branch (work is on `claude/optimistic-keller-a6385x`), so CI never ran on this code, and as committed it would now fail on NU1903 (B-01). It has no vulnerability, secret, image or SBOM step. `cloudflare/cloudflared:latest` (`docker-compose.yml:149`) and `ubuntu/squid:latest` (egress overlay) are unpinned. NuGet and npm audits were clean today (see section 8) but nothing enforces it. |
| S-07 | LOW | CONFIRMED | No | CSP allows `style-src 'unsafe-inline'` (`ProductionHosting.cs:16`). Script injection is still blocked (`script-src 'self'`); inline styles can be used for UI redress/exfil of limited value. |
| S-08 | LOW | CONFIRMED | No | Webhook endpoints are anonymous by necessity and are now publicly reachable through a path-limited Cloudflare Access bypass (re-confirmed: the path reaches the origin; HTTP 530 only because the tunnel is down); `validationToken` is echoed from any POST (`MailEndpoints.cs:119-122`, text/plain, nosniff: not exploitable) and each request may trigger up to 200 DB lookups (`MailSync.cs:228`) at 600 req/min/address. Cheap authenticated-by-secret check only happens after the lookup. DB amplification risk is low but real once the webhook is public. |
| S-13 | LOW | CONFIRMED | No | `/hangfire` answers HTTP 500 to an unauthenticated request (recorded in `PRODUCTION_INTEGRATION_VALIDATION.md` section 9, not investigated). It fails closed and sits behind Access, but a 401/404 is expected; unexplained until reproduced. |
| S-14 | LOW | CONFIRMED | Before commit | The untracked guides (`CLOUDFLARE_PUBLIC_ACCESS.md`, `PRODUCTION_INTEGRATION_VALIDATION.md`) contain the Cloudflare account, tunnel, Access application and policy ids, Entra app ids and an operator email. Not secrets, but reconnaissance data; review or redact before committing to a remote whose visibility is unconfirmed. |
| S-09 | LOW | CONFIRMED | No | `GET /api/auth/dev-login` (`AuthEndpoints.cs:70`) changes session state on GET: a malicious page can sign the developer's browser in as `dev-admin` on the Development instance (login CSRF). Development only; route does not exist in Production (verified: option validated at startup). |
| S-10 | LOW | CONFIRMED | No | Saved analytics settings are kept in `localStorage` under one global key (`web/src/lib/analytics.ts:134`), shared by all users of a browser profile. |
| S-11 | LOW | SUSPECTED | No | `RichText` sets `innerHTML` from the (server-sanitised) signature (`RichText.tsx:24`). Only the author's own content, so self-XSS at worst, but it depends entirely on the server sanitiser. |
| S-12 | IMPROVEMENT | CONFIRMED | No | Container hardening: app container has `cap_drop` and `no-new-privileges` but no read-only root filesystem and no memory/CPU/pids limits (`docker-compose.yml:69-122`). |

### Verified-good controls (sampled, evidence-based)
- **Authorization:** every one of 179 mapped endpoints carries `RequirePermission`, `RequireResourcePermission<TicketResolver>`, `RequireAuthenticatedOnly` (with handler checks, e.g. `/api/users/lookup`) or an explicit `AllowAnonymous`. Anonymous list is minimal: health, `/api/csrf`, `/api/auth/*`, two Graph webhooks, SPA fallback. Architecture tests (76) enforce the metadata.
- **Ticket sub-routes that need only `tickets.read` at route level** (messages, mailbox, participants, followers, macros) re-check the specific permission in the service (`TicketService.cs:419,587,633`, `TicketFollowers.cs:72,121`, `Macros.cs:452`, `TicketMerge.cs:85`).
- **Realtime:** one client-callable hub method; groups server-decided, notes group joined only with `tickets.notes.read`, payloads content-free (`Realtime.cs`). Origin check on WebSocket upgrade.
- **Entra OIDC:** code + PKCE, state/nonce defaults, issuer and `tid` pinned, `oid` required, return URL sanitised (`ReturnUrl.cs`), redirect URI taken from registered origins, failures redirect with a reference only.
- **CSRF:** antiforgery on every unsafe request except the two webhooks; SameSite Lax session cookie, Secure always outside Development.
- **Injection:** only 5 raw-SQL sites; all parameterised (`AuditWriter.cs:50`, `OutboundMail.cs:66,128,165`, `TicketLifecycle.cs:116`, `TicketingSupport.cs:167`). Search uses `ILike` with escape and is notes-aware.
- **Webhook:** per-subscription client state stored as hash, constant-time compare, body size capped, never trusts payload mailbox.
- **Graph HTTP:** absolute next/delta links restricted to the configured Graph host (`MicrosoftGraphMailProvider.cs:406-420`).
- **Output safety:** HTML sanitiser allow-list, remote images blocked, rendered only in sandboxed iframe with CSP; Markdown renderer builds React nodes only and restricts links (`markdown.tsx`); attachments are always downloads.
- **Secrets:** no `.pfx/.pem/.key/.env/.dump` ever in history; no token/key/connection-string patterns across all 174 commits; `.env` and `.tunnel.env` ignored. Logs: no message bodies/tokens found by grep; request-log redaction for invitation paths.
- **Headers:** nosniff, no-referrer, COOP/CORP, Permissions-Policy, framing denied, HSTS outside Development, API `no-store`.
- **Fail-closed production config:** `RequireSafeProductionSettings` refuses wildcard hosts, missing proxy, attachments on, migrate-on-start, debug logging, missing certificate (`ProductionHosting.cs:76-135`).

## 4. Functional bugs and reproducible failures

No application behaviour defect was reproduced by the test suites. Findings:

| ID | Sev | Status | Blocks Prod | Finding |
|---|---|---|---|---|
| B-01 | MEDIUM | CONFIRMED | No (dev) / P1 | **Solution does not build/test as committed.** `dotnet test Helpdesk.slnx` fails with `error NU1903` (warnings-as-errors, `Directory.Build.props`): `Microsoft.AspNetCore.SignalR.Client 10.0.12` (`Directory.Packages.props:24`, used by `tests/Helpdesk.Host.Tests`) pulls `SignalR.Common 10.0.12`, advisory GHSA-334f-vvp4-7g6p (CVE-2026-70159, DoS, fixed in 10.0.13). Production is not exposed (image runs 10.0.13; the vulnerable package is test-only). Tests were run only with `-p:WarningsNotAsErrors=NU1903`. **Fix:** bump the package set to 10.0.13. **Verify:** `dotnet test` without override. |
| B-02 | — | — | — | Merged into H-02 (root cause now established, section 12). |
| B-03 | LOW | CONFIRMED | No | Frontend lint: 0 errors, 6 warnings (`set-state-in-effect` in `AnalyticsReportPage.tsx:61,63`; refs read during render in `Composer.tsx:357`; unsafe optional chaining in `workspace.test.tsx:426`; two fast-refresh warnings). The `Composer.tsx:357` ref read can show stale UI. |
| B-04 | LOW | CONFIRMED | No | Dead code `SlaStrip` (`web/src/components/Sla.tsx`) noted in the E2E report. |
| B-05 | LOW | CONFIRMED | No | No TODO/FIXME/NotImplementedException in first-party source (only the word "HACK" as test data). |

## 5. Missing and partially implemented features

Classification against PRODUCT.md, MVP.md, DEVELOPMENT_PLAN.md, BACKLOG.md, OPEN_DECISIONS.md and the delivery reports. Items not re-exercised in a browser are marked "per docs".

| Feature | State | Note |
|---|---|---|
| Entra sign-in, provisioning, bootstrap admin | IMPLEMENTED | Live sign-in and bootstrap admin passed in Production (section 11); non-admin, inactive-user and secret-rotation paths not live-tested |
| Departments, memberships, roles, invitations | IMPLEMENTED | Self-grant notification missing (S-02) |
| Tickets, notes, types, fields, request forms, audiences | IMPLEMENTED | |
| Portal, requester history, identity linking | IMPLEMENTED / PARTIAL | Admin relink/merge tool MISSING (Refinement 9/10) |
| Canned responses, macros, views, bulk, search | IMPLEMENTED | Bulk undo/reply not built |
| SLA, business hours, automation, dry run | IMPLEMENTED | SLA re-evaluation on priority change, chained dry run: backlog |
| Notifications, realtime, presence, mentions, followers | IMPLEMENTED | Follower notifications per docs partial |
| Knowledge | IMPLEMENTED / PARTIAL | No approval workflow or versions |
| Reporting and dashboard | IMPLEMENTED | |
| Mail inbound/outbound/threading/NDR | IMPLEMENTED | Live: subscription Active, delta sync, inbound ticket, outbound reply delivered (section 11). Not live-tested: NDR/bounce, lifecycle events, renewal over time, uncertain-send, second mailbox inbound |
| Attachments in Production | DEFERRED / DISABLED | No scanner (D-08); inline image paste also off |
| User deactivation / offboarding | MISSING | D-14, S-01 |
| Retention, export, anonymisation | MISSING | D-09 policy not approved |
| Break-glass audited access | MISSING | D-12 |
| Automatic shared-mailbox creation | DEFERRED | D-18 |
| Un-merge, cross-department move | MISSING / EXCLUDED (merge across departments) | Backlog |
| Queue filter by ticket type; agent create dialog with type/fields | PARTIAL (per BACKLOG Refinement 2; not re-verified) | |
| SLA warning notification channel | MISSING | Backlog Phase 5 |
| Log shipping/retention, alert routing | MISSING | F-09 |
| Excluded by design (tags, forwarding, time tracking, leaderboards, CRM, personal From, teams boundary, cross-dept merge, external search) | EXCLUDED | Respected; none found built |

Counts: MISSING 7, PARTIAL 3, DEFERRED/DISABLED 2.

Documentation defects: `docs/plans/BACKLOG.md` still lists items that were built (rate limiting, merge/link, SignalR push, security headers, DB-level roles partially), `MVP.md` lists attachments as MVP while Production disables them, `docs/guides/PRODUCTION_DEPLOYMENT.md` describes the Nginx topology whereas the Windows deployment uses the tunnel directly.

## 6. Performance and architecture concerns

| ID | Sev | Finding |
|---|---|---|
| P-01 | MEDIUM | **Single instance by design** (ADR-021): in-memory SignalR groups, presence, setup sessions and Hangfire server in the web process; no backplane. Any restart drops realtime state; fine at stated scale, a hard ceiling otherwise. |
| P-02 | LOW | Search is trigram `ILIKE` over message bodies; measured only for knowledge. Scale check for 50k+ tickets/requester is open (backlog). |
| P-03 | LOW | Web bundle about 540 kB unsplit (backlog). |
| P-04 | IMPROVEMENT | Vitest creates a jsdom per file (96 s cumulative); config tuning only. |

No N+1 or unbounded query was demonstrated; I did not profile. Treat as untested.

## 7. Production infrastructure and recovery gaps

| ID | Sev | Status | Blocks Prod | Finding |
|---|---|---|---|---|
| H-01 | HIGH | CONFIRMED | **Yes** | **Deployed image is built from uncommitted code** (section 2). The Production guide itself records (section 11 of `PRODUCTION_INTEGRATION_VALIDATION.md`) that the subscription-retry change was deployed by recreating `app`, but it is uncommitted. No commit identifies what runs; rollback target is a mutable tag (`helpdesk-host:local`, same tag as Development). **Fix:** review/commit, build from a clean tree with an immutable tag, record the digest. |
| H-02 | HIGH | CONFIRMED (cause established) | **Yes** | **Production is not resilient: one PC, one shared Postgres, nothing that restarts it.** Root cause of the current outage (section 12): Windows Update force-rebooted the PC at 00:17-00:18 BST on 15 Oct and the machine stayed off until 08:52; Docker Desktop (`AutoStart=False`) only started at 09:31, and the shared `postgres` (restart policy `no`) and `cloudflared` (`no`) were not brought back while the app (`unless-stopped`) crash-looped on its missing database. Production and Development share one Postgres container, volume, role and project. **Fix:** milestone section 13. |
| H-03 | HIGH | PARTIALLY CLOSED | **Yes** | **Backups exist but are manual, single-location, stale and not restore-proven.** Verified: encrypted `helpdesk_prod` dump (19:48Z 14 Oct) hash re-computed and equal to its `.sha256`; two certificate backups on external `I:` hash-equal to their records. Missing: schedule, off-PC copy of the DB dump (only on `C:`), post-backup data (the dump predates the config sync and later activity), full `pg_restore` of the Production dump (only `--list`), object-store backup. Detail in section 11. |
| H-04 | MEDIUM (was HIGH) | PARTIALLY CLOSED | Org launch | **Live validation is substantial but not complete.** PASSED (documented, section 11): Entra sign-in + bootstrap admin, Exchange RBAC A/B 200 and outside/personal 403, webhook validation and Access path protection, Active subscription + delta sync, inbound ticket, delivered outbound reply. STILL UNVERIFIED: NDR/bounce handling, lifecycle events (`reauthorizationRequired`, `missed`, `subscriptionRemoved`), renewal and recovery after an outage, uncertain-send behaviour, second mailbox (B) inbound isolation, non-admin sign-in and the leaver path, sign-in secret rotation (30-day secret expires 2026-11-13), and sustained operation. The earlier statement that Access "denies everyone" and that a webhook exception was still required was out of date. |
| O-01 | MEDIUM | CONFIRMED | P1 | No monitoring or alerting: console logs only, `json-file` rotation by size, no retention by age, no alert routing; metrics only via optional OTLP. A stopped Postgres went unnoticed for hours. |
| O-02 | MEDIUM | CONFIRMED | P1 | Secrets live in a plain `.env` plus a SECRETS_DIR bind mount on the host disk; database not encrypted at rest; BitLocker/host controls unverified. |
| O-03 | MEDIUM | CONFIRMED | P1 | Certificate/credential rotation is documented for Data Protection (previous certificates must share the password) but Graph certificate rotation and Entra secret expiry alerting are manual; wizard shows expiry in UI only. |
| O-04 | MEDIUM | CONFIRMED | P1 | Image updates are manual (`helpdesk-host:*` set of 6 local tags, SDK/aspnet base tags float on `10.0`); no vulnerability scan; no owner named for patching (F-11). |
| O-05 | LOW | CONFIRMED | No | Docker Desktop on a PC: sleep, Windows Update and shutdown stop the service; `keep-awake` and auto-start are not part of the stack. |
| O-06 | LOW | CONFIRMED | No | Offboarding data lifecycle: leaver tickets stay assigned; mailbox/requester data retained indefinitely (D-09). |

Production Compose strengths: no published ports, only cloudflared reaches the app, Entra/Graph required, mode guard prevents Development and Production running together (9 Pester tests pass), fail-closed validators, certificate-protected key ring with refusal on unreadable keys (validated in disposable environment 2026-10-14).

## 8. Test results, coverage and limitations

| Suite | Result |
|---|---|
| Backend `Helpdesk.Host.Tests` (integration, isolation matrix, mail, SLA, portal, identity) | **988 passed / 0 failed / 0 skipped** (5 m 54 s, throw-away Postgres 18.6) |
| Architecture tests | 76 passed |
| GraphSpike tests | 43 passed |
| Frontend Vitest | 200 passed (34 files) |
| TypeScript `tsc -b` | clean |
| `oxlint` | 0 errors, 6 warnings |
| Playwright (isolated stack, fake mail, in-memory store, dev sign-in) | 13 passed, 0 failed, 9 skipped (opt-in screenshot/visual specs) |
| Pester `ModeGuard` | 9 passed |
| `npm audit --omit=dev` | 0 vulnerabilities |
| NuGet vulnerable (transitive) | 1 high, test project only (B-01) |
| **Totals executed** | **1 329 tests, 0 failures** (988 + 76 + 43 backend, 200 Vitest, 13 Playwright, 9 Pester; the earlier 1 337 was an addition error) |

Flaky tests: none observed (single run each). Skips: the 9 Playwright specs are opt-in captures with no baselines.

Coverage gaps: no coverage measurement; browser-level Internal Note leakage not tested; no Entra setup, People/invitations, analytics or Microsoft 365 page E2E; phone layout only on the portal; no load/scale test run; no live Graph; no backup/restore of Production; the full Department isolation matrix ran (included in the 988) but only against the fake provider.

Limitations of this audit: about 28 000 lines of first-party C# and the web client were **sampled by risk area** (authentication, authorization metadata, webhook, mail provider, realtime, hosting, compose, identity, search), not read line by line; no dynamic penetration test; Production DB, Cloudflare, Entra and Exchange were not touched, so their live configuration is unverified; ASVS L2 was used as a checklist, not a certification.

## 9. Outstanding decisions and previously documented findings now closed

**Closed or verified in code and tests today**
- F-06 rate limiting for sign-in, invitations, administration, webhooks and portal (`AbuseControls.cs`; fail-closed outside Development/Testing).
- F-07 invitation links in fragments, redaction in request logs.
- F-10 Permissions-Policy, COOP/CORP (`ProductionHosting.cs:156-157`).
- D-13 Data Protection key persistence and certificate encryption (refusal of unreadable keys).
- PR10-D22 mailbox-less tickets; no-fallback mailbox behaviour present in tests.
- Production settings guard (SRE-006/010/014).

**Still open (carried, not re-litigated)**: D-04 RLS (not adopted), D-08 scanner, D-09 retention policy, D-11 self-grant control, D-12 break-glass, D-14 deactivation, D-16 live tenant, D-18 mailbox creation, D-19 polling vs reachable webhooks; F-01, F-02, F-03 (tunnel never to front Development), F-04, F-05, F-08, F-09, F-11, F-13; certificate custody and session policy values.

## 10. Prioritised remediation roadmap

Estimates are working days for one engineer, excluding approvals and tenant lead time.

### P0 — must fix immediately
| # | Item | Findings | Effort |
|---|---|---|---|
| 1 | Review and commit the working tree; rebuild from a clean tree with an immutable tag and record the digest; keep the previous image as rollback | H-01 | 0.5 |
| 2 | Restore availability safely (section 13 step A), then give Production its own Postgres with `restart: unless-stopped`, restart policy for the tunnel and Docker Desktop auto-start | H-02 | 1.5 |
| 3 | Scheduled encrypted backup of `helpdesk_prod` + object store + certificates, off-PC copy, full restore drill of the Production dump | H-03 | 1.5 |

### P1 — before organisational launch
| # | Item | Findings | Effort |
|---|---|---|---|
| 4 | User deactivation/offboarding workflow, absolute session lifetime, session revocation on deactivation | S-01, D-14 | 3 |
| 5 | Complete the live validation list in H-04 (NDR, lifecycle, renewal, second mailbox, non-admin and leaver paths, secret rotation) | H-04 | 2 + tenant |
| 6 | Verify client-IP handling through the tunnel; fix XFF trust or rate-limit key | S-03 | 0.5 |
| 7 | NuGet 10.0.13 bump; make CI run on the real branch and add `dotnet list --vulnerable`, `npm audit`, secret and image scan steps | B-01, S-06 | 2 |
| 8 | Self-grant notification or dual control | S-02, D-11 | 1.5 |
| 9 | Separate DB roles; separate Production DB instance | S-04 | 2 |
| 10 | Monitoring and alert routing (Postgres/app down, job lag, subscription expiry, secret expiry) and log retention | O-01, F-09 | 2 |
| 11 | Decide certificate custody, host disk encryption, rotation runbook and patch owner | S-05, O-02..04 | 1 + decisions |
| 12 | Approve retention policy (D-09) and attachment/scanner decision (D-08) | | decisions |

### P2 — before broader rollout
Absolute-session/step-up policy refinement; webhook amplification limit and pre-filter (S-08); CSP without inline styles (S-07); container read-only and resource limits (S-12); search/queue scale test at 50k+ tickets (P-02); admin requester relink/merge tool; SLA warning channel; browser tests for Internal Note leakage, Entra setup, People, analytics; wire Pester tests into the existing CI; clear lint warnings; refresh BACKLOG/MVP/deployment docs; analytics store per user (S-10).

### P3 — optional
Bundle splitting, vitest environment tuning, SignalR backplane only if multi-instance is ever required, knowledge versions/approval, bulk undo, per-department audiences by group, break-glass design (D-12).

**Estimated remediation effort:** P0 about 3 days; P1 about 15 working days plus tenant and decision lead time; P2 about 10 days.

**Recommended first milestone:** "Reproducible and recoverable Production" — P0 items 1-3 (clean committed image, self-restarting isolated database, verified Production backup) before any further feature or tunnel activation.


## 11. Reconciliation of live evidence (revision 2, 2026-10-15)

Source of the claims: `docs/guides/PRODUCTION_INTEGRATION_VALIDATION.md` (untracked; I listed it in revision 1 but did not read it, which is why H-03, H-04 and the Cloudflare statement were out of date), `docs/guides/CLOUDFLARE_PUBLIC_ACCESS.md`, the backup files and the independent checks below. "Doc-recorded" means the guide states it and I did not re-run it.

| # | Claim | Status | Evidence |
|---|---|---|---|
| 1 | Entra sign-in succeeded in Production, account bootstrapped as Platform Administrator | **DOC-RECORDED** | Guide section 9: one active user, platform administrator, subject equals the bootstrap subject, `/api/me` 200, `/hubs/tickets` 101. One earlier callback without `state` failed safely and the retry succeeded. The database is currently unreachable, so not re-queried. |
| 2 | Exchange RBAC: A/B 200, outside and personal 403 | **DOC-RECORDED** | Guide section 7 item 1, run 2026-10-14 18:50Z by `check-graph-mailbox-access.ps1`. |
| 3 | Graph webhook validation and Access path protection passed | **PARTLY INDEPENDENT** | Guide section 10 lists the exact outside checks. Today I re-confirmed only: `GET /` and `/api/me` return 302 to the Access login; `GET /api/mail/graph/notifications` is not redirected to Access (answers 530 because the tunnel is down), consistent with a path-limited bypass. |
| 4 | Subscription Active, delta sync successful | **DOC-RECORDED** | Guide section 12 preservation list ("mailbox helpdesk-test-a (Active subscription)"); the delta job runs every 5 min. Current state unknown while the DB is down. |
| 5 | Real inbound mail created IT-1 | **PARTLY DOC-RECORDED** | Guide section 12 counts 1 ticket and 2 messages after the sync; the reference "IT-1" is not written in any document I could find. |
| 6 | Outbound reply from helpdesk-test-a reached Yahoo | **UNVERIFIED** | No document or log I could read records the Yahoo delivery; the 2 messages are consistent with one inbound plus one outbound. Treat as your observation, not repository evidence. |
| 7 | Production encrypted DB backup exists and was checked | **PARTLY INDEPENDENT** | `C:\Users\abode\HelpdeskBackups\prod\helpdesk-prod-20261014T194859Z.dump.enc` (220 704 bytes): I re-hashed it and it equals the recorded SHA-256. The guide records a decrypt round trip and `pg_restore --list` of 469 entries. I did not decrypt it (passphrase is DPAPI-held). A listing is not a restore. |
| 8 | Certificate backup on external `I:` passed 11 checks | **PARTLY INDEPENDENT** | `I:` is a USB-attached 1.8 TB disk ("New Server Backup"). `I:\HelpdeskCertBackup\...-1803.zip.enc` and the older `...20261014.zip.enc` both re-hash equal to their `.sha256` files. The 11 checks are doc-recorded and were not re-run. |

### Corrections to revision 1
- **H-04** downgraded HIGH to MEDIUM and split into PASSED and STILL UNVERIFIED (table row H-04).
- **H-03** rewritten: backups exist; scheduling, off-PC copy, freshness and full restore are missing.
- **Cloudflare:** revision 1 said Access "denies everyone" and that a webhook exception was still required. Current state (doc-recorded, partly re-confirmed): Access application `SAMH Helpdesk` with an Allow policy for exactly one named account over the whole hostname, plus a separate bypass application restricted to the two Graph webhook paths. The deny-everyone placeholder in `PRODUCTION-HARDENING.md` section 14 is superseded.
- **Test totals:** 988 + 76 + 43 = 1 107 backend; + 200 + 13 + 9 = **1 329**. The 1 337 figure was an arithmetic error.
- **S-01** reduced to MEDIUM while Access is a one-account allow-list (back to HIGH when widened).
- **B-02** merged into H-02 (cause known).
- **S-08** annotated: webhook paths are now public.
- **Unchanged by the new evidence:** H-01 (the guide itself confirms the retry change was deployed uncommitted), D-14 deactivation, S-02 to S-06, B-01 (NU1903), O-01 to O-04, CI never runs, feature gaps.
- **True launch blockers now:** H-01, H-02, H-03, S-01 (deactivation) and the open H-04 items, plus decisions D-08 and D-09.

### New observations from the reconciliation
| ID | Sev | Finding |
|---|---|---|
| R-01 | MEDIUM | **The Production dump predates later data.** The guide shows the backup (19:48Z) was taken before the config synchronisation (5 departments, 79 ticket types) and any later mail activity. The live database holds data that exists in no backup. A fresh dump is the first action once the database is reachable. |
| R-02 | MEDIUM | **`I:\pw.txt` (20 bytes) sits at the root of the backup disk beside the encrypted certificate backups.** I did not open it. If it is the backup passphrase, encryption is defeated for anyone holding the drive. The owner must check it and move it to the password manager. |
| R-03 | LOW | The superseded certificate backups (`E:\HelpdeskCertBackup\...20261014` and the same file on `I:`) still contain the PFX whose password the guide says was exposed in a transcript; the guide's own to-do (replace, then delete) is open. |
| R-04 | LOW | Every backup is made by hand. `dev.ps1` only checks a recent *Development* backup before `update`; there is no Production equivalent and no Production backup script. |
| R-05 | LOW | The sign-in secret has a 30-day life and expires 2026-11-13 (guide section 7); no alert exists. |

## 12. Production outage investigation (read-only)

**Confirmed cause of loss of service:** a Windows Update restart took the whole PC (and the Docker Desktop VM) down without stopping containers, and nothing brought the stack back afterwards.

| Time (BST unless stated) | Evidence |
|---|---|
| 15 Oct 00:13 (23:13 UTC, 14 Oct) | Last Postgres log line (a routine checkpoint). No shutdown message follows: the database was killed, not stopped. |
| 00:17:10 and 00:18:17 | System log, `User32` 1074: `winlogon.exe` initiated shutdown on behalf of SYSTEM; at 00:18:14 Windows installed KB5127648 (2026-10 security update) and KB5127690 (.NET Framework). Kernel-Power 109/577: system-initiated reboot. EventLog 6006 at 00:18:20. |
| 00:18 to 08:52 | No boot events: the PC stayed off (about 8.5 h). Boot at 08:52:49. |
| 08:52 to 09:31 | Docker Desktop not running. Its setting `AutoStart` is `False` (read from `settings-store.json`). |
| 09:31:04 (08:31:04 UTC) | WSL/Hyper-V switch recreated (Docker Desktop VM started). At 08:31:08 UTC Docker marks `postgres` and `cloudflared` as `Exited (255)`, the state it assigns to containers whose VM died. |
| 08:31:09 UTC | `objectstore-prod` (restart `unless-stopped`) started; `helpdesk-backend` (`unless-stopped`) started and began crash-looping. |

Other results:
- **Crash-loop cause:** `Npgsql` cannot connect during `KeyRingReadabilityCheck.StartAsync` (`DataProtectionKeyStore.cs:132`); the host fails closed by design. More than 20 restarts, exit code 139 in Docker events (the managed exception is the cause; the code itself is unexplained).
- **Restart policies:** `postgres` `no`, `cloudflared` `no`, `app` and `objectstore-prod` `unless-stopped`. `app` has `depends_on postgres: service_healthy`, which is evaluated only when the stack is started by Compose, not on a Docker-initiated restart.
- **OOM / resources:** `OOMKilled=false` on all containers; C: has 53 GB free; no resource event precedes the failure. The only trigger is the host reboot.
- **Docker event history:** not retained across the Docker Desktop restart (only the backend restarts since 09:17 UTC were available).
- **Persistence:** `postgres` mounts named volume `helpdesk_pgdata` at `/var/lib/postgresql`; container created 2026-10-09 and never recreated. Production `helpdesk_prod` and Development `helpdesk` live in the same cluster, volume and role (separation by database name only).
- **Data integrity:** the cluster must run crash recovery (WAL replay) on next start. That is normal; `fsync` was on and there is no evidence of corruption, but there is no evidence of success either until it is started. A cold copy of the volume first removes the risk.
- **Recoverable backups:** Production dump (14 Oct 19:48Z, see section 11), Development dumps (13 Oct, also copied to `D:`), `pv` rehearsal dump, certificate backups on `E:` and `I:`.
- **Exposure while down:** unavailable since about 00:18 BST. Graph retries notifications and delta reconciliation covers the gap once the host runs; subscriptions last about 3 days, so renewal or re-creation by the six-hourly job (or the manual retry) is needed if the outage extends beyond about 17 Oct.

Remaining uncertainty: why the PC stayed off after the update (firmware/power or manual), whether exit code 139 means anything, and the actual state of `helpdesk_prod` until the database is started.

## 13. First remediation milestone: "Reproducible and Recoverable Production"

Plan only; nothing here has been executed. Each step needs your approval. Backups use the existing format: `<name>-<UTC timestamp>.dump.enc` + `.sha256`, AES-256-CBC, PBKDF2 600 000.

### Decisions needed (no assumption made)
| ID | Decision | Options |
|---|---|---|
| M-D1 | How the stack starts unattended after a reboot | (a) Windows auto sign-in + Docker Desktop `AutoStart=true`; (b) accept manual start and add an external uptime alert; (c) move Production to a supervised host |
| M-D2 | Real off-PC backup target | second machine/NAS, cloud storage, or accept USB disk `I:` as "second medium, same PC" |
| M-D3 | CI branch configuration | add the real branch pattern to `ci.yml`; or create and adopt a `main` branch (default-branch decision) |
| M-D4 | Least-privilege DB roles | include in the separation, or the next milestone |
| M-D5 | Windows Update policy for this PC | active hours / restart window / pause during business hours |
| M-D6 | Maintenance window for steps B and C | time and who is told (mail is delayed, not lost) |
| M-D7 | `I:\pw.txt` | owner confirms what it is and relocates it |

### Step A: restore availability (current image, no code change)
| | |
|---|---|
| Changes | A1 read-only cold copy of volume `helpdesk_pgdata` into `C:\HelpdeskBackups\prod\volume-cold\` using a throw-away `alpine` container with the volume mounted read-only; A2 `docker start helpdesk-postgres-1`; A3 let `helpdesk-backend` recover by itself (policy `unless-stopped`); A4 immediate encrypted `pg_dump` of `helpdesk_prod` to `C:` and `I:`; A5 `dev.ps1 tunnel-start -ConfirmPublic` (separate approval). |
| Risk to live data | A1 none (read-only mount, new file only). A2 crash recovery, protected by A1. A3 none. A5 re-exposes the app behind Access, the already accepted state. |
| Backup prerequisite | A1 is the prerequisite (the 14 Oct dump is stale, R-01). |
| Verification | tar listing and SHA-256 recorded; Postgres log shows automatic recovery complete and "ready"; `pg_isready`; live counts (users, tickets, messages, departments, ticket types, mailboxes) match the guide (1 / 1 / 2 / 6 / 79 or more / 1); `/health/ready` 200 inside the container; startup log without key-ring warnings; mailbox subscription Active or recoverable, no backlog; external `GET /` still 302 to Access. |
| Rollback | If recovery fails: stop Postgres, restore the tar into an empty volume, second option the 14 Oct dump. Tunnel: `tunnel-stop`. |
| Downtime | already down; about 10 to 15 minutes to healthy, plus tunnel start. |

### Steps G then B: dependency fix, review and clean versioned build
| | |
|---|---|
| Changes | G: `Directory.Packages.props` `10.0.12` to `10.0.13` for EF Core (3 entries), OIDC, SignalR.Client, OpenApi, Mvc.Testing (all exist on nuget.org, verified). B: review the 19 modified and 16 untracked paths (diff already read: the Mail change is the subscription-retry feature with a test; compose, `Common.ps1`, `ModeGuard` are Production-mode tooling; the rest is docs/e2e); commit in three groups (mail feature; Windows production tooling and tests; docs, after the S-14 redaction); add `org.opencontainers.image.revision`/`version` labels via build args in `docker/host.Dockerfile`; build as `helpdesk-host:<version>-<sha7>` and never use `local` for Production. |
| Risk to live data | None for G and the commits. Deploy recreates `app` only; no migration is pending (migration set unchanged). |
| Backup prerequisite | A4 dump. **Before building, tag the running image `sha256:3509b9cd...` as `helpdesk-host:rollback-20261015`** because the `local` tag is about to move. |
| Verification | `dotnet build`; `dotnet list package --vulnerable --include-transitive` clean; targeted tests only (Mail namespace, identity auth/configuration, architecture suite); image revision label equals HEAD; after deploy `/health/ready`, `/api/auth/providers`, mailbox state, one sign-in. |
| Rollback | recreate `app` from the rollback tag. |
| Downtime | about 1 minute (recreate `app`). |

### Step C: separate Production PostgreSQL from Development
| | |
|---|---|
| Changes | New compose service `postgres-prod` (postgres:18.6 pinned by digest, new volume `pgdata_prod`, own role and password, healthcheck, `restart: unless-stopped`, no published port) used only by `app` and `migrate`. Data moves by `pg_dump -Fc helpdesk_prod` then `pg_restore` into the new cluster (all schemas including `hangfire`). The Data Protection key ring, Hangfire state and the Graph subscription id, client-state hash and expiry are rows in that database and move with it; the same certificate files stay mounted. Update `dev.ps1`, `ModeGuard.ps1` and its Pester tests. The Development cluster and database are not touched. |
| Risk to live data | Moderate while writes are frozen: bad restore or a missed table. Mitigated by a verified dump, per-table row counts both sides, and leaving the old database untouched. |
| Backup prerequisite | Fresh encrypted dump after `app` and the tunnel are stopped (final state), plus A1 and A4. |
| Verification | per-schema row counts equal; `__ef_migrations_history` equal (`migrate` reports nothing pending); key ring readable (host starts, no warning); recurring jobs registered; subscription Active with the same id (manual "Retry subscription" only if not); sign-in; one outbound test; external Access check. |
| Rollback | Stop `app`; point `Database__ConnectionString` back at the old server (old `helpdesk_prod` untouched = state at cut-over); if new writes happened, dump the new DB and restore into the old before reverting. Keep the old database 14 days, drop only with approval. |
| Downtime | about 10 to 15 minutes (database is small; mostly orchestration). Inbound mail waits at Exchange and is fetched by delta afterwards. |

### Step D: restart and recovery behaviour
| | |
|---|---|
| Changes | `restart: unless-stopped` and healthchecks for `postgres`, `postgres-prod`, `cloudflared`; pin `cloudflared` to a version; Docker Desktop `AutoStart=true`; M-D1 and M-D5 outcomes; an external uptime probe of the public health path is the only alert that works while the PC is down. |
| Risk to live data | None. |
| Backup prerequisite | A4 dump. |
| Verification | `docker restart` of each service; a controlled Docker Desktop restart in a window; after a reboot the stack is healthy without intervention (subject to M-D1). |
| Rollback | revert the compose file; `docker compose up -d`. |
| Downtime | seconds per service; one planned Docker Desktop restart (about 5 minutes). |

### Step E: scheduled encrypted backups and off-PC recovery
| | |
|---|---|
| Changes | New `scripts/windows/backup-production.ps1`: `pg_dump -Fc helpdesk_prod` via `docker exec`, OpenSSL encryption with the DPAPI-held passphrase (as for dev), `.sha256`, automatic verification (decrypt plus `pg_restore --list`), copy to `I:` and the M-D2 target, retention (for example 14 daily, 8 weekly), failure written to the Windows Event Log; Task Scheduler jobs daily and pre-change; `dev.ps1 prod-migrate` and `prod-start` gain an `Assert-RecentBackup` for the Production folder; object-store snapshot only when attachments are enabled; the certificate backup script scheduled monthly and after rotation. |
| Risk to live data | None (read-only dump, one short extra connection). |
| Backup prerequisite | none. |
| Verification | first run yields dump + hash + verification entry; task result 0; a deliberate failure raises the Event Log entry; every copy hash-equal. |
| Rollback | disable the task; no application impact. |
| Downtime | none. |

### Step F: restore test on isolated infrastructure
| | |
|---|---|
| Changes | Throw-away `postgres:18.6` with no published ports and no volume (as in `DEV-DISASTER-RECOVERY.md`): decrypt the newest Production dump inside it, full `pg_restore --exit-on-error`, compare per-table counts with the live database (read-only queries), run the image's `migrate` against the copy, then start the Production image on an internal no-egress network with the DP certificate mounted read-only and confirm the key ring reads and `/health/ready` is 200. Record RPO and RTO. |
| Risk to live data | None (only read-only count queries against the live cluster). |
| Backup prerequisite | step E output. |
| Verification | counts equal; nothing pending; host healthy; elapsed time recorded. |
| Rollback | remove the throw-away container and network. |
| Downtime | none. |

### Step H: CI branch configuration
`.github/workflows/ci.yml` triggers on `pull_request` and pushes to `main`; the repository has no `main`. After M-D3 set the trigger to the real branch pattern (or create `main`), keep `dotnet test` without the NU1903 override (works after step G) and add `dotnet list package --vulnerable --include-transitive` and `npm audit --omit=dev`. Risk none; verification is one green run on the branch; rollback is reverting the file.

### Order and estimate
A (with A4) first, then G, B, C, D, E, then F against the first scheduled dump, then H. About 4 to 5 working days excluding decisions; Production downtime in total roughly 30 minutes across A, B, C and D.

### Approval gate
No step above has been performed. Please approve step A first and answer M-D1 to M-D7 as each becomes relevant.

## 14. Step A execution record (2026-10-15, 09:33-09:37 UTC)

Approved by the owner; executed exactly as in section 13 step A, except that no copy was placed on `I:` and the tunnel was not started.

| Check | Result |
|---|---|
| Postgres container stopped before start | Yes (`Exited (255)`, policy `no`) |
| Cold copy of `helpdesk_pgdata` (volume mounted read-only) | `C:\Users\abode\HelpdeskBackups\prod\volume-cold\pgdata-cold-20261015T093411Z.tgz` (113 339 864 bytes, SHA-256 in `.tgz.sha256`) + per-file manifest (2 471 files). Verified: extracted into a throw-away container, manifest identical; source volume file count unchanged. |
| PostgreSQL start | Normal crash recovery: "not properly shut down; automatic recovery in progress", redo done, checkpoint complete, ready. No corruption or recovery errors. Healthy at 09:34:39 UTC. |
| `helpdesk_prod` | 14 MB. Users 1 (active, platform admin), identities 1, departments 6 (ACP, CONN, FAC, FUND, HR, IT), ticket types 79, tickets 1 (IT-1, Open), messages 2, requesters 1, memberships 1, mailboxes 1, mail messages 2, Data Protection keys 1, audit events 24, migration histories 4/3/2/3/26/1/2. |
| Graph subscription | `Active`, id present, client-state hash present, delta token present, expires 2026-10-17 19:27Z, 0 consecutive failures; delta sync to Graph returned 200 at 09:34:49 and 09:35:03 with no error. |
| Backend | Restarted itself (restart policy), healthy; `/health/ready` 200, `/api/auth/providers` `{"entra":true}`, `/api/auth/dev-login` 404. Only errors in the log are from 09:32 (before Postgres was started). |
| Development database `helpdesk` | untouched: 398 users, 14 tickets, 6 departments (as recorded 13 Oct). |
| Fresh encrypted backup | `C:\Users\abode\HelpdeskBackups\prod\helpdesk-prod-20261015T093532Z.dump.enc` (286 544 bytes) + `.sha256`. SHA-256 equals record; decrypts; `pg_restore --list` 484 entries; full `pg_restore --exit-on-error` into a throw-away no-network Postgres succeeded and counts equal live (1/6/79/1/2/1/1/24, subscription Active). Suitable for the step F isolated restore test. The earlier 14 Oct dump was not overwritten. |
| Disk | C: 53 GB free (88 % used); Docker local volumes 7.4 GB. |

Remaining: Cloudflare tunnel stopped on purpose (production not publicly reachable; Access/webhook path currently returns 530); `postgres` still has restart policy `no` and Docker Desktop `AutoStart=False` (steps C/D); the new dump exists only on `C:` (no off-PC copy yet, M-D2); the throw-away test container was removed.

**Decision M-D7 (2026-10-15, owner):** `I:\pw.txt` stays where it is, unopened and unmodified. The risk that it may hold a backup passphrase beside encrypted backups is explicitly accepted for this pilot and is not to be raised again as a blocker.

## 15. Steps G and B execution record (2026-10-15)

| Item | Result |
|---|---|
| Backup copy | `helpdesk-prod-20261015T093532Z.dump.enc` + `.sha256` copied to `I:\HelpdeskBackups\prod`; copy hash equals the sidecar and the source; nothing overwritten; `I:\pw.txt` untouched (accepted pilot risk, M-D7). |
| NU1903 | Seven PackageVersion entries 10.0.12 to 10.0.13 (EF Core x3, OpenIdConnect, SignalR.Client, OpenApi, Mvc.Testing). Release build 0 warnings / 0 errors; `dotnet list package --vulnerable --include-transitive`: none in any project; SignalR.Common resolves to 10.0.13. |
| Tests (no NU1903 override) | Host.Tests Mail + Identity namespaces 264 passed; architecture 76 passed. Full suite not re-run (targeted per scope). |
| Commits | `099653e` deps; `fdbe6e6` mail subscription retry; `f7e7f77` Windows Production tooling (hard-coded tenant/app/tunnel ids and a personal mailbox replaced by values from the git-ignored `.env`: `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `CLOUDFLARE_TUNNEL_ID`, `GRAPH_CHECK_MAILBOXES`); `49bbb31` docs (identifiers redacted); `315c605` image revision labels. Not pushed. |
| Secret scan | No credentials in any changed or untracked file (one placeholder `SECRET_KEY=change-me-local-only` in `.env.example`). |
| Rollback image | `helpdesk-host:rollback-20261015-3509b9cd` = `sha256:3509b9cdef6a9630a903fe15d71d9ea31b16f82addfa652176a265956e75ea5b` (the image running in Production). |
| New image | `helpdesk-host:1.0.0-rc1-315c605`, id/digest `sha256:d9244686644c5d6003fae4c2dd7af9344b549c9247b8018ed27416e7ff917206`, label revision `315c60598cd7a5446624ef0feeaf4c809d829e3a`, version `1.0.0-rc1`, 105 MB. Built from a clean tree. Contains `RetrySubscriptionAsync`, EF Core and OpenIdConnect 10.0.13. Later commits are documentation only. |
| Isolated verification | Throw-away Postgres on an `--internal` network, no live database. `migrate` applied all modules; Production-mode boot: live 200, `/api/auth/providers` 200, `/api/auth/dev-login` 404, `/api/me` 401, SPA served with CSP/X-Frame-Options; Mail:Provider=Fake correctly refused in Production. `/health/ready` 503 only because the object store was deliberately unreachable. Containers and network removed. |
| Production | Not touched: `helpdesk-backend` still on the rollback image, healthy; Postgres healthy; tunnel stopped. |

Note: `local` still points at the old image; Compose must be given `HELPDESK_VERSION=1.0.0-rc1-315c605` at deploy time.

## 16. RC1 deployment record (2026-10-15, 10:01 UTC)

`helpdesk-host:1.0.0-rc1-315c605` (`sha256:d9244686...b0f`, revision `315c605`) replaced the Production `app` container only, via `docker compose --profile production up -d --no-deps --no-build app` with `HELPDESK_VERSION=1.0.0-rc1-315c605` added to the git-ignored `.env`. PostgreSQL, the object store and cloudflared were not touched (dry run first showed only `app` recreated). No migration was run (source counts 4/3/2/3/26/1/2 equal the database). Verified: environment Production, DevSignIn off, `/health/live` and `/health/ready` 200, Entra provider configured, `dev-login` 404, 0 restarts, 0 errors in the startup log, Hangfire server started with 5 recurring jobs and 16 jobs succeeded within the first minutes (0 failed). Data: 1 user (platform administrator), 1 identity, 1 Data Protection key, 6 departments, 79 ticket types, IT-1 and 2 messages, 1 active mailbox, subscription Active (expires 2026-10-17 19:27Z), 0 consecutive failures. Rollback: set `HELPDESK_VERSION=rollback-20261015-3509b9cd` and repeat the same command (image `sha256:3509b9cd...` retained). Cloudflare remains stopped; Development not started.

## 17. Step C execution record: Production PostgreSQL separated (2026-10-15)

| Item | Result |
|---|---|
| New container / volume | `helpdesk-postgres-prod-1` (postgres:18.6, `restart: unless-stopped`, healthcheck, no published port) with new volume `helpdesk_pgdata_prod`; own administrator role (migrations, restore, backup) and a least-privilege application role (`scripts/ops/prod-db-roles.sql`: row DML and sequence use only; cannot create/alter/drop/truncate or create roles; verified denied). Credentials are in the git-ignored `.env` (names `PROD_PG_ADMIN_*`, `PROD_APP_DB_*`). |
| Rehearsal first | Throw-away copy on an internal network: restore, roles, RC1 booted as the application role with the real key-ring certificate (read-only mount); Hangfire installed/ran under the least-privilege role, 0 permission errors. |
| Maintenance window | App stopped 10:07:44 UTC (0 sessions on the old database confirmed), encrypted backup of the final state, `pg_dump | pg_restore --no-owner --exit-on-error`, roles applied, app recreated healthy 10:08:28 UTC. **Downtime about 44 s.** |
| Preservation | All 71 tables: row counts AND per-table content checksums identical between source and destination; 3 extensions/sequences/triggers rows (15) identical. Live checks after cutover: 1 active Platform Administrator, 6 departments, 79 ticket types, IT-1 and 2 messages, 1 membership, 24 audit events, 1 Data Protection key (decrypted by the application, no key-ring warning), 1 active mailbox, subscription Active (expires 2026-10-17 19:27Z, 0 failures), Hangfire 5 recurring jobs, 9 succeeded in the first 3 minutes, 0 errors. |
| Application | Image unchanged (`helpdesk-host:1.0.0-rc1-315c605`, `sha256:d9244686...`); connects only to `postgres-prod` as the application role (old server: 0 sessions); Production, DevSignIn off, `/health/live` and `/health/ready` 200, `dev-login` 404, Entra configured. |
| Development isolation | `helpdesk-postgres-1` untouched: 398 users, 14 tickets, 6 departments; `postgres-prod` contains only `helpdesk_prod`. The original `helpdesk_prod` and the `helpdesk_pgdata` volume are untouched and must be kept at least 14 days (rollback source). |
| Scripts | `docker-compose.yml` (postgres-prod, app/migrate connection, `pgdata_prod`), `scripts/windows/dev.ps1` (`prod-start` starts postgres-prod then the app with `--no-deps`; `prod-migrate` migrates postgres-prod as administrator), `.env.example`, `scripts/ops/prod-db-roles.sql`. |
| Backups | Backups taken before (10:07:48 UTC, from the old server) and after cutover from the NEW container: `helpdesk-prod-20261015T100902Z.dump.enc` (301 984 bytes), hash, decrypt, 484 entries and a full restore verified, copied to `I:\HelpdeskBackups\prod` and hash-equal. **Finding:** the first post-cutover dump (`...T100845Z`) contains `GRANT ... TO helpdesk_app` and fails a standalone restore (`role does not exist`) because `--no-owner` does not drop privileges; it is kept (not deleted) but superseded. The backup command must use `--no-acl` (then run `prod-db-roles.sql` after a restore). There is still no scheduled backup (step E); the manual command now targets the new container. |

Rollback (database unchanged, writes since cutover would be lost, so dump the new database first if anything meaningful was written): stop the app; in `.env` and `docker-compose.yml` revert the app `Database__ConnectionString` to `Host=postgres;...Username=${POSTGRES_USER}` (git: `git revert` of the step C commit); `docker compose --profile production up -d --no-deps app` with the old `postgres` running.
