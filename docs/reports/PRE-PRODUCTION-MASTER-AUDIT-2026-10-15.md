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
| S-01 | MEDIUM (was HIGH); **IMPLEMENTED and DEPLOYED to local Production 2026-10-16 (sections 21 and 22)** | CONFIRMED | Org launch until deployed | **Leaver keeps access.** The session cookie is stateless (`AuthenticationSetup.cs:36-37`, 8 h sliding, no absolute cap). `CurrentUser.LoadAsync` (`Services/CurrentUser.cs:27-37`) re-checks only `users.is_active`, and nothing ever clears it: no code sets `IsActive = false` and there is no endpoint (D-14). Disabling the account in Entra does not end an active session; a used cookie renews indefinitely. Logout (`AuthEndpoints.cs:58-63`) only drops the local cookie. **Mitigation found in revision 2:** the whole hostname sits behind a Cloudflare Access allow-list of one named account with an 8 h session (independently re-confirmed: 302 to Access on `/` and `/api/me`), so today a leaver is also stopped by Access within 8 h. That is a pilot-only control; it returns to HIGH the moment Access is widened to the organisation. **Fix:** deactivation endpoint + audit + unassign; absolute session cap (about 12 h) via sign-in-time claim. **Verify:** deactivated user's next request is 401; cookie older than the cap is rejected. |
| S-02 | **RECLASSIFIED: intentional permission policy, not a vulnerability** (decision 2026-10-16, ADR-039; implemented, section 23) | CLOSED BY POLICY | No | **Platform Admin access model.** Originally reported as "Platform Admin can self-grant department membership with no second control or notice" (`MembershipService.cs`, D-11, D-02). The product owner decided that the Platform Administrator is the global super-administrator with full access to every department, ticket, Internal Note, user, membership, setting and report, and that self-assignment needs no approval. The permission model was corrected centrally and the privileged changes stay audited (`selfGrant`, `viaPlatformAdmin`). Residual risk (accepted): the Platform Administrator account is the most sensitive account; its protection is sign-in security, S-01 session revocation and keeping the number of holders small. |
| S-03 | MEDIUM (was ASSUMPTION); **PASS for IPv4 on 2026-10-16 (sections 24 to 26); live IPv6 untested** | CONFIRMED SAFE, one defect fixed | No | **Per-IP rate limits depend on the real client address.** Resolved by evidence: the application honours only the rightmost X-Forwarded-For entry from the trusted proxy network; the IPv6 /64 and mapped-IPv4 bucket defect was fixed; the controlled live-tunnel check (section 26) showed through the real Cloudflare Access and tunnel path that each client address has its own bucket and that forged X-Forwarded-For and X-Real-IP values are ignored (CF-Connecting-IP is rejected by Cloudflare itself). Residual: no live IPv6 client was available; the bypass reaches the origin for sub-paths of the two webhook paths (no route exists there); the Graph subscription is Failed (pre-existing). |
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
| Departments, memberships, roles, invitations | IMPLEMENTED | Platform Administrator global access by policy (S-02, section 23) |
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
| 8 | ~~Self-grant notification or dual control~~ withdrawn: S-02 is an intentional policy (section 23) | S-02, D-11 | 0 |
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
| 7 | Production encrypted DB backup exists and was checked | **PARTLY INDEPENDENT** | `%USERPROFILE%\HelpdeskBackups\prod\helpdesk-prod-20261014T194859Z.dump.enc` (220 704 bytes): I re-hashed it and it equals the recorded SHA-256. The guide records a decrypt round trip and `pg_restore --list` of 469 entries. I did not decrypt it (passphrase is DPAPI-held). A listing is not a restore. |
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
| Cold copy of `helpdesk_pgdata` (volume mounted read-only) | `%USERPROFILE%\HelpdeskBackups\prod\volume-cold\pgdata-cold-20261015T093411Z.tgz` (113 339 864 bytes, SHA-256 in `.tgz.sha256`) + per-file manifest (2 471 files). Verified: extracted into a throw-away container, manifest identical; source volume file count unchanged. |
| PostgreSQL start | Normal crash recovery: "not properly shut down; automatic recovery in progress", redo done, checkpoint complete, ready. No corruption or recovery errors. Healthy at 09:34:39 UTC. |
| `helpdesk_prod` | 14 MB. Users 1 (active, platform admin), identities 1, departments 6 (ACP, CONN, FAC, FUND, HR, IT), ticket types 79, tickets 1 (IT-1, Open), messages 2, requesters 1, memberships 1, mailboxes 1, mail messages 2, Data Protection keys 1, audit events 24, migration histories 4/3/2/3/26/1/2. |
| Graph subscription | `Active`, id present, client-state hash present, delta token present, expires 2026-10-17 19:27Z, 0 consecutive failures; delta sync to Graph returned 200 at 09:34:49 and 09:35:03 with no error. |
| Backend | Restarted itself (restart policy), healthy; `/health/ready` 200, `/api/auth/providers` `{"entra":true}`, `/api/auth/dev-login` 404. Only errors in the log are from 09:32 (before Postgres was started). |
| Development database `helpdesk` | untouched: 398 users, 14 tickets, 6 departments (as recorded 13 Oct). |
| Fresh encrypted backup | `%USERPROFILE%\HelpdeskBackups\prod\helpdesk-prod-20261015T093532Z.dump.enc` (286 544 bytes) + `.sha256`. SHA-256 equals record; decrypts; `pg_restore --list` 484 entries; full `pg_restore --exit-on-error` into a throw-away no-network Postgres succeeded and counts equal live (1/6/79/1/2/1/1/24, subscription Active). Suitable for the step F isolated restore test. The earlier 14 Oct dump was not overwritten. |
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

## 18. Step D execution record: recovery hardening (2026-10-15)

| Item | Result |
|---|---|
| Restart policies (live) | `postgres-prod`, `app`, `objectstore-prod`: `unless-stopped`; cloudflared `no` (kept, tunnel never auto-starts); Development `postgres` `no`. Healthchecks: postgres-prod and app; object store has none (adding one recreates the container, not done); dependency ordering `app` after `postgres-prod` healthy. |
| Cloudflared | Pinned to `cloudflare/cloudflared:2026.7.3@sha256:e39ee8da...91bf` (the build the tunnel was validated with); stopped; token, route and Access/webhook applications untouched. |
| Live stack | No container recreated; compose dry run shows no change; no downtime. |
| Windows/Docker findings | Windows Update restarted the PC at 00:17-00:18 BST; the user profile loaded at 08:53:32 but Docker Desktop was started by hand at 09:31:00 (its startup entry was **disabled**, `AutoStart=False`). Changed (user-level, reversible): `AutoStart=true` in `%APPDATA%\Docker\settings-store.json` (backup in the audit scratch folder) and the Startup entry approved. Automatic sign-in was **not** enabled; Windows Update policy was not changed (active hours 08:00 to 02:00, no policy). Without a sign-in Docker does not start: documented as a hosting limitation, not claimed solved. |
| Scripts | `scripts/windows/health-check-production.ps1` (read-only, 19 checks, exit code), `scripts/windows/recover-production.ps1` (start-only, ordered, `-WhatIf`), `ModeGuard.ps1` made quote-safe on Windows PowerShell 5.1 (Pester 9/9). Guide: `docs/guides/PRODUCTION_RECOVERY.md`. |
| Live health check | 19 checks: 0 FAIL, 1 WARN (restore test not automated). Counts: 1 platform admin, 6 departments, 79 ticket types, IT-1 and 2 messages, 1 key, subscription Active (expires in 57 h). Development 398 users / 14 tickets / 6 departments. |
| Recovery tests (isolated, RC1 image, internal network, throw-away data) | App started while PostgreSQL was down, database returned: application recovered by itself, jobs succeeding 55-68 s later, with and without the key-ring certificate; database crash (`pg_ctl stop -m immediate`): Docker restarted PostgreSQL in 5 s and jobs resumed within 53 s, application never restarted. Test-design findings: `docker kill` counts as a manual stop (not restarted, as documented); a first run failed only because of a malformed test argument. Live evidence: the morning outage recovered by itself once PostgreSQL was started (ready 09:34:39, app ready 09:35:14 UTC). |
| Not tested (needs approval) | Windows reboot, Docker Desktop restart, live database interruption. |

## 19. Step E execution record: portable backup and recovery (2026-10-15)

| Item | Result |
|---|---|
| Design | Existing `scripts/ops` tooling reused and made portable: `lib-backup.sh` (PG_CONTAINER or compose mode, one PBKDF2 setting, Git Bash safe), `backup-database.sh` (`pg_dump --format=custom --no-owner --no-acl`, streamed into encryption, `.sha256`, `.meta.json`), `verify-backup.sh` (also rejects archives with ACL/OWNER entries), `backup-objects.sh` (new `OBJECT_VOLUME` mode, S3 mode kept), `backup-config.sh` (new encrypted configuration bundle), `backup-all.sh` (lock, verification, objects, config, verified copies to `BACKUP_COPY_DIRS`, status file, exit 0/1/75), `restore-database.sh`, `restore-test.sh`, new `restore-test-isolated.sh`; Windows wrapper `scripts/windows/backup-production.ps1`. Guide: `docs/guides/PORTABLE_BACKUP_RECOVERY.md`. |
| Fresh backup | `helpdesk-prod-20261015T123457Z.dump.enc` (344 592 B), `helpdesk-prod-objects-20261015T123459Z.tar.enc` (83 488 B, 92 entries, volume currently holds no attachments), `helpdesk-prod-config-20261015T123501Z.tar.enc` (30 752 B); each with `.sha256` (dump also `.meta.json`); all copied hash-equal to `I:\HelpdeskBackups\prod`. Existing backups and `I:\pw.txt` untouched. |
| Isolated restore | Disposable `postgres:18.6` container (no network, no volume): restore OK in 9 s, roles re-applied, application role can read but cannot create objects or truncate, all stable tables and the migration history identical to live (71 tables restored; 16 differences in continuously changing Hangfire/mail-sync tables, reported not failed). |
| Portability | The object bundle decrypted with plain `openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000` outside the scripts (92 entries); no DPAPI involved. |
| Failure scenarios | Wrong passphrase: verification exit 1, message without secrets. Missing PostgreSQL container: `backup-all.sh` exit 1, `"status":"failed"`, 0 partial files. Overlap: a younger lock whose owner is unknown exits 75; a stale lock is taken over; the lock is released after each run. A first run failed because native `openssl` received a non-Windows path (MSYS conversion): fixed in `lib-backup.sh` (and itself demonstrated the clean-failure path). |
| Live data | Unchanged: 1 platform admin, 6 departments, 79 ticket types, IT-1 and 2 messages, subscription Active; application not restarted (0 restarts), health check 0 FAIL. |
| Not done by design | No scheduler configured, no automatic deletion, no new external service, Cloudflare stopped. |

## 20. Step H execution record: CI and automated security validation (2026-10-15)

Workflow `.github/workflows/ci.yml` rewritten (triggers: PRs, `main`, `release/**`, `claude/**`, manual; `contents: read`; actions pinned to SHAs, scanners to digests): jobs `backend`, `migrations`, `web`, `scripts`, `ops-scripts`, `compose`, `dependencies`, `secrets`, `image`. Added `.gitleaks.toml`, `.github/dependabot.yml`, `scripts/ci/{validate-compose.py,check-migrations.sh,backup-roundtrip.sh}`, `docs/guides/CI.md`; `dotnet-tools.json` dotnet-ef 10.0.12 to 10.0.13; ShellCheck-clean traps in the backup scripts. Local results: actionlint clean; Release build 0 warnings; isolation 309/309 pass (filters partition the 988 tests as 309 + 679); architecture 76/76; migrations 7 modules, no drift, second run no-op; backup round trip identical; compose policies 0 problems; NuGet 0 vulnerable, npm audit 0; gitleaks 183 commits, 2 reviewed false positives allowlisted narrowly, a planted token is detected; Trivy image 0 HIGH/CRITICAL (including unfixed) and 0 secrets, config scan clean, CycloneDX 165 components. Not run locally: the workflow on GitHub-hosted runners.

## 21. S-01 implementation record: deactivation, offboarding and session revocation (2026-10-15)

Implemented locally, tested, committed; **not pushed and not deployed**. Production still runs RC1 without it.

| Item | Result |
|---|---|
| Workflow | `PUT /api/platform/users/{id}/active` `{ value, reason? }`, `platform.users.manage` only; refuses self and the last active platform administrator (409 `self_deactivation` / `last_admin`); repeating the state is a no-op without audit; reason at most 200 characters. People page: Accounts section with two-step Deactivate and Reactivate. Audit: `user.deactivated`, `user.reactivated` (actor, previous/next, reason, sessionVersion) and one `ticket.assignee_changed` (reason `user_deactivated`) per released ticket. |
| Session revocation | `identity.users.session_version` (new column, default 0); the cookie carries the version and the sign-in time; every request compares both; deactivation and reactivation increment the version, so old sessions never return. A cookie without these claims (issued before this release) is refused: everyone signs in once after deployment. |
| Absolute lifetime | `Authentication:Session:AbsoluteLifetimeHours` (1 to 168, default 12) measured from the sign-in time in the cookie; `IdleTimeoutMinutes` (5 to 1440, default 480, not above the absolute limit) is the sliding window. Validated at start-up. |
| No silent reactivation | The provisioner returns an inactive account untouched (no attribute refresh, no sign-in time); Entra and dev sign-in refuse it. |
| Work released | Tickets assigned to the account are unassigned in every department in the same transaction (new `IUserDeactivationListener`, Ticketing handler, reviewed in the bypass test); memberships, messages, audit and requester records preserved; realtime groups refreshed. |
| Privilege changes | Reviewed: no invalidation needed, they are read per request (test added). |
| Migration | `AddUserSessionVersion` (identity): additive NOT NULL column with default 0; backward compatible with the running RC1 (ignores it); Down drops it; up/down/up verified; no data rewrite. |
| Tests | 20 new (`UserDeactivationTests`) + 318 Identity/Platform/Organisation/Audit/Hosting regression passed; architecture 76/76; web 202/202 (2 new); migration check clean. |
| Residual | The People page lists every account (50 per search); no bulk offboarding; a deactivated person's Entra account must still be disabled in Entra separately; deployment forces one re-login. |

### 21.1 Final security review (realtime and concurrency)

- **SignalR (finding, fixed):** a SignalR connection authenticates only when it opens. After deactivation the department and note groups were already dropped by the access refresh, but the connection itself stayed open and kept the user group (`u:`) and the requester-portal groups (`r:`), which deliver notification prompts and `requestChanged` events (ticket reference and coarse kind). The same held for a session that outlived its absolute lifetime. Fix: each connection records the session stamp of its cookie; on every access refresh (immediately on deactivation, and every 60 s) `IUserSessionValidator` applies the HTTP rule (active account, same session version, inside the absolute lifetime); a failing connection loses every group and is closed by the server and is never re-added, so reactivation restores nothing and the old cookie cannot open a new connection. Tests: `RealtimeRevocationTests` (3).
- **Last administrator (verified, no defect):** both administrator mutations take a transaction-scoped PostgreSQL advisory lock before counting the other active administrators, so concurrent requests are serialised and the count is read after the earlier commit. Tests: two administrators deactivating each other, and demotion racing deactivation, 6 rounds each: exactly one request wins, the loser gets 409 (guard) or 401/400 (its session was already revoked), and at least one active administrator always remains. Direct SQL remains outside the guard by nature.

## 22. S-01 deployment record (2026-10-16)

| Item | Result |
|---|---|
| Source / image | `master` @ `fc712d90a38834a76991775b54edc1a67e4b6c17` (CI 9/9) built as `helpdesk-host:1.1.0-s01-fc712d9`, `sha256:dd73231db0b841229534f6e244436cb00be6141bb15192a947c01b9bee108c41`, revision label equal to the commit. RC1 `helpdesk-host:1.0.0-rc1-315c605` (`sha256:d9244686...`) unchanged. |
| Disposable pre-test | Copy of the live database on an internal network: the new image applied only `AddUserSessionVersion` (identity 3 to 4 migrations; other modules unchanged); data counts identical; RC1 AND the new image both booted healthy against the migrated copy as the application role (0 errors, jobs succeeding), so RC1 is compatible with the additive migration. S-01 smoke test of the new image on an empty Development database over loopback: self-deactivation 409; deactivate 200; old session 401; new sign-in 403; reactivate 200; old session still 401; fresh sign-in 200; non-administrator 403; audit events written. |
| Backup (app stopped 08:53:33 UTC, 0 application sessions) | `helpdesk-prod-20261016T085337Z.dump.enc` (637 584 B), objects bundle (92 entries), configuration bundle (15 entries), all copied hash-equal to `I:\HelpdeskBackups\prod`; checksum, decryption, archive and isolated restore passed (71 tables, identical to the quiescent live database, roles re-applied, least privilege verified). Existing backups untouched. |
| Migration (08:54:32) | `dev.ps1 prod-migrate` as the Production administrator role (not `helpdesk_app`); PostgreSQL not recreated. Identity history 4 (`20261015203432_AddUserSessionVersion`); column `session_version integer NOT NULL DEFAULT 0`; existing user at version 0; other modules 4/2/3/26/1/2 unchanged; the application role can use the column and cannot create objects. |
| Deployment (08:54:43) | `HELPDESK_VERSION=1.1.0-s01-fc712d9` in the git-ignored `.env`; `up -d --no-deps --no-build app` recreated only the application. Healthy 08:54:51. **Downtime about 78 s** (08:53:33 to 08:54:51). |
| Validation | Image id and revision as above; `/health/live` and `/health/ready` 200; Entra provider configured; dev-login 404; 1 active platform administrator; 1 user; 6 departments; 79 ticket types; IT-1 (Open) with 2 messages; 1 Data Protection key; 24 audit events; mailbox subscription Active (0 failures, synced 08:55); 5 recurring jobs, 10 succeeded and 0 failed since start; 0 errors or permission denials in the log; health check 0 FAIL; Development 398 users / 14 tickets / 6 departments and no `session_version` column. Session policy at defaults (absolute 12 h, idle 8 h). |
| Not testable without public access | Browser sign-in through Cloudflare Access and Entra, and the pre-deployment cookie rejection against the live host (no live sessions exist: the tunnel is stopped; every RC1 cookie lacks the new claims and is refused by design). |
| Rollback | Set `HELPDESK_VERSION=1.0.0-rc1-315c605` in `.env` and run `docker compose --project-directory . -p helpdesk --profile production up -d --no-deps --no-build app`. RC1 ignores the additive column, so the migration is not reversed. Before any rollback, account for writes since 08:54:51 (none expected: tunnel stopped). The pre-migration encrypted backup (`...20261016T085337Z`) exists for database recovery only with separate approval. The `Down` migration (drop column) exists but is not needed. |

## 23. S-02 Platform Administrator access policy (2026-10-16, local commit; not deployed)

| Item | Result |
|---|---|
| Decision | The Platform Administrator is the global super-administrator: full access to all departments, tickets, Internal Notes, users, memberships, settings, reporting and administration, with no department membership needed; self-assignment is allowed without separate approval. Recorded as ADR-039; supersedes D-02, D-11 and D14. |
| Review result | The only restriction that limited the Platform Administrator was the *content* classification: `PermissionEvaluator`, the authorization handler and `IAuthorizer.AccessibleDepartmentsAsync` / `HasInAnyDepartmentAsync` let the flag satisfy administrative permissions only (ticket queries, search, queues, Internal Notes, reports, mail attention, automation history, knowledge drafts, realtime groups were therefore 404 or empty for it). |
| Change | One rule in the central evaluator and the matching department lists; `IOrganisationDirectory.GetDepartmentsWithPermissionAsync` (realtime groups, notification eligibility) includes every department for an active platform administrator; `/api/me` hints and the web `canTicket` hint follow. No endpoint check was added or removed. |
| Preserved | Strict isolation for Department Administrators, Team Leads, Agents and Viewers (another department is 404, no role carries a platform permission, a Department Administrator cannot grant the flag); assignment, pickers, followers, mentions and lead notifications stay membership-based; platform-only operations (department creation and activation, mailbox registration, user deactivation, platform audit) unchanged. |
| Audit | Membership add, role change and removal events now carry `next.viaPlatformAdmin` next to `selfGrant`; the flag change keeps `user.platform_admin_changed` with `selfChange`; ticket changes record the acting user as always. |
| Tests | New `PlatformAdministratorPolicyTests` (4); updated evaluator, authorizer, decision and probe tests (the probe now proves the administrator of ANOTHER department sees no content); about twenty test files whose expectations encoded the old rule were updated (isolation matrices, realtime, dashboard, reporting, mail, portal). Targeted .NET groups all passed on a disposable PostgreSQL (policy, authorizer and probe 50; isolation, realtime and dashboard 314; ticketing, mail and portal 212; identity, platform, organisation and audit 369; Production and Development databases untouched), architecture 76/76, web queue and people 24/24. |
| Schema / deployment | No migration. Not deployed, not pushed. Production keeps running the S-01 image until a deployment is approved. |
| Follow-ups | Closed in 23.1: `CLAUDE.md` aligned, queue UI finished. Open: privacy approval (D-20), optional logging of Platform Administrator reads of ticket content. |

### 23.1 S-02 finalisation (2026-10-16, local commits; not deployed)

| Item | Result |
|---|---|
| Queue and pickers | `/api/me` returns `departments` (every department) for a platform administrator only; the queue department filter, dashboard and analytics pickers, New ticket dialog and the "has tickets" navigation use `departmentsWith`, so a Platform Administrator selects any department, or All, without a membership. Search, saved views and queue filters were already server-side and follow the widened scope. |
| Participation | Unchanged and tested: no membership, assignment, follow or notification is created by global access; assigning a ticket to a non-member platform administrator is refused until it joins. Signatures stay per agent and membership-based. |
| Blocking routes and controls | Reviewed every `IsPlatformAdmin`, membership and `tickets.*` use in `src`: all department checks go through `IAuthorizer` or the organisation directory; the remaining membership-only paths are the deliberate participation lists above. UI hints (`canTicket`, `departmentsWith`) follow the flag. No further blocker found. |
| Privacy | `docs/guides/TICKET_CONTENT_ACCESS.md` (draft) and decision D-20 record that approval is outstanding. No approval assumed. |
| Security | Authentication and session code untouched; audit unchanged. |

## 24. S-03 client address and rate limits (2026-10-16, local commit; not deployed)

| Item | Result |
|---|---|
| Scope | Review of `ProductionHosting.ConfigureForwardedHeaders`, `AbuseControls.InfrastructurePartition`, middleware order, Compose networking, Cloudflare documentation and the existing tests. Full write-up: `docs/guides/CLIENT_ADDRESS_AND_RATE_LIMITS.md`. |
| Confirmed safe | Forwarded headers honoured only from `Proxy:KnownProxies`/`KnownNetworks` (nothing by default, loopback not trusted); forwarding runs before authentication and the limiter; only the rightmost X-Forwarded-For entry is used; forged, missing, empty and malformed headers cannot dodge a limit; other forwarding headers are ignored; scheme comes from a trusted proxy only. |
| Defect fixed | Rate-limit identity: IPv6 reduced to /64, IPv4-mapped IPv6 mapped to IPv4 (`AbuseControls.ClientKey`); `ForwardLimit = 1` made explicit; `PROXY_KNOWN_NETWORKS` makes the trusted network configurable for other hosts (default unchanged `172.16.0.0/12`). |
| Not changed | Production network layout and the default trusted range (a deployment change); Cloudflare Access, tunnel, Entra and Graph untouched; no Access bypass for the Graph webhook was created. |
| Tests | New `ClientAddressTrustTests` (14 cases: two failed before the fix), `AbuseControlTests` and `ProductionHostingTests` unchanged and passing (32 in the three classes); architecture 76/76; compose validation clean; Release build. Synthetic evidence only. |
| Outstanding | Controlled live-tunnel check (guide section 5, needs approval); optional pinning of the exact Compose subnet; none for the webhook path: the approved path-limited Access bypass already exists (corrected in section 25). |

## 25. S-02 and S-03 deployment to local Production (2026-10-16)

| Item | Result |
|---|---|
| Source / image | `master` @ `b505868687c17744cb124023341d5947981040f1` (CI 9/9) built as `helpdesk-host:1.2.0-s02s03-b505868`, `sha256:4b22b647bebbc7bad133044e94f9b0437b71af8f3a403b1c2fca5bf09b905faf`, revision label equal to the commit. S-01 image `helpdesk-host:1.1.0-s01-fc712d9` (`sha256:dd73231d...`) and RC1 are unchanged on disk. |
| Migrations | None: no migration file changed since S-01; the new image's `migrate` against a disposable copy of the live database applied nothing (identity history stayed at 4, all counts identical). No schema change in Production. |
| Disposable tests | New image booted healthy against the copy as the application role (0 errors, jobs succeeding); S-01 smoke test on an empty Development database repeated (409 / 200 / 401 / 403 / 200 / 401 / 200); 327 targeted tests (Platform Administrator, client address, abuse controls, production hosting, deactivation, realtime revocation, content probe, isolation matrices) passed on a disposable PostgreSQL. |
| Backup (app stopped 17:04:07 UTC, 0 application sessions) | `helpdesk-prod-20261016T170409Z.dump.enc`, objects bundle (92 entries) and configuration bundle (15 entries): checksums match, decrypt, archive readable; isolated restore into disposable PostgreSQL identical to the quiescent live database (71 tables, roles applied, least privilege verified); hash-equal copies on `I:\HelpdeskBackups\prod`. All earlier backups untouched. |
| Deployment | `HELPDESK_VERSION=1.2.0-s02s03-b505868` in the git-ignored `.env`; Compose resolved to that image and the unchanged trusted network `172.16.0.0/12` (the new `PROXY_KNOWN_NETWORKS` default); `up -d --no-deps --no-build app` recreated only the application. Healthy 17:04:50 on the host clock, so downtime was under a minute. PostgreSQL, object store, volumes, secrets and Cloudflare were not touched. |
| Validation | Running image id and revision label as above; `/health/live` and `/health/ready` 200; Entra provider configured; dev-login 404; `/api/me` 401 unauthenticated; 1 active platform administrator; 6 departments; 79 ticket types; IT-1 Open with 2 messages; 1 Data Protection key (readable: the host starts and is ready with the certificate required); 24 audit events; identity history 4; 5 recurring jobs, 12 succeeded and 0 failed since start; 0 errors or permission denials in the log; Development unchanged (398 users, 14 tickets, 6 departments, no `session_version`). |
| Live client-address check (internal network only) | From a throw-away container on the Compose network (peer inside the trusted range) against the running Production app: a forwarded client hit the sign-in limit at request 61 (limit 60 a minute); another forwarded address had its own bucket; a forged left-hand entry in front of the same rightmost address was still limited; a malformed header fell back to the proxy; three IPv6 addresses of one /64 shared one bucket. This proves the deployed configuration, NOT what Cloudflare or cloudflared send. |
| Not testable | Browser sign-in, Platform Administrator global access and department isolation against the live host (Entra only, tunnel stopped): covered by the tests on the same commit, not by a live sign-in. |
| Pre-existing finding | The mailbox subscription is `Failed` (health check FAIL before and after the deployment, unchanged by it): the 12:00Z `mail-subscriptions` run failed with `graph_400` while the tunnel is stopped. Delta polling every 5 minutes works. The previous subscription expires 2026-10-17 19:27Z; start the tunnel before then (or accept polling only) and re-run the subscription job. |
| Reconciliation (S-03 guide) | The guide wrongly said that no Access bypass exists and that mail runs in polling mode. `PRODUCTION_INTEGRATION_VALIDATION.md` section 10 records an approved path-limited bypass for the two Graph paths; mail uses webhooks with delta fallback. Guide and section 24 corrected; no Cloudflare setting was read or changed live. |
| Still outstanding | Controlled live-tunnel check of the forwarded headers (guide section 5, approval needed); D-20 privacy approval (pending, wording unchanged, `TICKET_CONTENT_ACCESS.md` stays a draft); optional logging of Platform Administrator reads. |
| Rollback | Set `HELPDESK_VERSION=1.1.0-s01-fc712d9` in `.env` and run `docker compose --project-directory . -p helpdesk --profile production up -d --no-deps --no-build app`. No schema change was made, so S-01 runs against the unchanged database; the database is never restored automatically and any new data is kept. The pre-deployment encrypted backup `...20261016T170409Z` is for database recovery only, with separate approval. |

## 26. S-03 controlled live-tunnel check (2026-10-16, 17:44Z to 17:57Z)

Scope: tunnel started with `dev.ps1 tunnel-start -ConfirmPublic` and stopped with `tunnel-stop` (Exited 17:57:46Z); nothing changed in Cloudflare, Access, Entra, Graph, code or databases. No cookie, token or ticket content was recorded; only status codes and counts.

| Stage | Result |
|---|---|
| Tunnel | 4 QUIC connections registered; the only ingress is `helpdesk.amb-it.co.uk -> http://app:8080`, everything else `http_status:404`. |
| Access, no session | 21 GET and 3 POST probes (`/`, `/api/me`, `/api/csrf`, `/api/auth/*`, `/api/tickets`, `/api/portal/*`, `/hubs/tickets`, `/hangfire`, `/health/*`, `/openapi`, `/admin`, `/assets/*`, `/api/mail/graph`, `/api/mail/graph/other`, `/api/mail/graphx`): all 302 to the Access login. |
| Webhook bypass | `POST /api/mail/graph/notifications` and `/lifecycle` with a validation token: 200 echoing the token; without a token 400; GET 404. **Finding:** the bypass also reaches the origin for sub-paths (`/api/mail/graph/notifications/x`: GET 404, POST 400), so it matches by prefix, not exactly two paths. The application has no route there, so nothing is exposed; narrowing the Access destination would be a Cloudflare change (not made, needs approval). |
| Signed-in browser, vantage A | Access one-time PIN then Entra sign-in succeeded over HTTPS. 70 requests to a counted `/api/auth` route: first 429 at request 61, 60 x 404 and 10 x 429, exactly the 60 a minute limit. |
| Second vantage (phone hotspot) | The PC changed network at about 17:50:30Z (cloudflared logged lost connections and re-registered by 17:50:43Z; one 502 while the tunnel reconnected). The bucket of the first address was exhausted (429 from 73 s, window until about 118 s) yet the first request after the switch, at 109 s, was 404: a different client address has its own bucket although both arrive through the same tunnel. A shared or cloudflared-keyed bucket would have answered 429. The app container did not restart in between (limiter state intact). |
| Forged headers, authenticated route | The curl test with a copied `CF_Authorization` cookie returned 302 for every request (Access did not accept the copied cookie), so it proved nothing and was not repeated. |
| Forged headers, webhook route (no cookie needed) | 650 POSTs: 600 x 200, 50 x 429 (the 600 a minute webhook limit, per client). Immediately afterwards a request with `X-Forwarded-For: 198.51.100.9`, one with the chain `198.51.100.9, 198.51.100.10` and one with `X-Real-IP: 198.51.100.9` were all still 429 (the application's own rate_limited response): forged values did not become the client identity, consistent with Cloudflare appending the real address on the right and the application using only that entry. `CF-Connecting-IP: 198.51.100.9` was refused by Cloudflare itself (HTTP 403, error 1000) before reaching the origin. After 65 s a plain request returned 200, confirming the limit was the cause. |
| HTTPS scheme | OIDC sign-in completed with the HTTPS redirect through the tunnel (same as 2026-10-14). |
| After the check | Tunnel stopped; `/api/mail/graph/notifications` from outside answers 530 (origin unreachable; `/` still 302 because Access answers at the edge); health check unchanged (one FAIL: the subscription); 0 application errors during the window. |
| Not tested | A live IPv6 client (all addresses were IPv4; IPv6 /64 handling is covered by tests only). Behaviour on a different Cloudflare plan. Whether a copied `CF_Authorization` can be replayed (it was not accepted). |
| Graph subscription (reported separately, not repaired) | State `Failed`, `subscription_id` null since the 12:00Z run on 2026-10-16; the Graph-side subscription (id withheld) lapses at 2026-10-17 19:27Z. Delta polling works. A renewal needs the tunnel up and an approved "Retry subscription" run; none was done in this window. |

## 27. Graph subscription diagnosis and polling-only mode (2026-10-16; code committed locally, not deployed)

| Item | Result |
|---|---|
| Diagnosis (read-only) | Production `mail.mailbox_sync`: state `Failed`, `subscription_id` NULL, client-state hash and delta token present, consecutive failures 0, last sync every 5 minutes (288 `SyncAll` jobs in 24 h, none failed). The Graph-side subscription still existed (read with an app-only token, `GET /subscriptions`: one subscription, correct notification URL, expiring 2026-10-17 19:27Z), so it was not expired or removed. Authorisation was fine (token issued, subscription list readable, delta working). Failures at 12:00Z (renewal PATCH, then create) and 18:00Z (create) returned `graph_400` while the tunnel was stopped, as on 2026-10-14 when the notification URL was unreachable. Graph's own error text was never stored, so the cause is the unreachable notification endpoint by strong circumstance and precedent, not by a captured message. |
| Defects found | (1) A failed renewal discarded the subscription id for ANY permanent error, so a still-valid Graph subscription became unrenewable and a duplicate would follow. (2) The shared `last_sync_error` was overwritten by the next successful delta pass (within 5 minutes), so the subscription error vanished and the two kinds of failure hid each other. (3) Only `graph_400` was recorded, not Graph's error code and message. (4) The mailbox health and the health-check script reported a polling-only installation as Failed. (5) The 6-hourly job retried creation indefinitely and Hangfire showed it as Succeeded. |
| Change | `Mail:InboundMode` (`Webhook` default, `Polling`): no subscription call, retry or report in polling mode; subscription state shown as an intentional mode; stored subscription data and delta token never rewritten; startup no longer needs a public base URL in polling mode. New column `mail.mailbox_sync.subscription_error` (migration `AddSubscriptionError`, additive, nullable, up/down/up verified); delta and subscription errors separated; failed renewals keep the id unless Graph says 404/410; Graph error code and short message captured; web UI, Microsoft 365 page and `health-check-production.ps1` polling-aware; compose default `MAIL_INBOUND_MODE=Polling`. Guide: `docs/guides/INBOUND_MAIL_MODES.md`. D-19 decided. |
| Tests | New `InboundModeTests` (15 cases) and two web tests; Mail namespace 202 passed; architecture 76/76; migration check (59 tables, models match, second run no-op); Release build; compose validation; web build. |
| Deployment needs | One additive migration (`dev.ps1 prod-migrate` as the admin role) and an app-only recreate with the new image; compose default makes Production polling-only. The existing Failed record and delta token are left untouched. Nothing deployed or pushed; Cloudflare stays stopped. |

## 28. Polling-only release deployed to local Production (2026-10-16)

| Item | Result |
|---|---|
| Source / image | `master` @ `10a3ecac684f6456ee5962eeee484b19345a970b` (clean checkout, CI 9/9) built as `helpdesk-host:1.2.1-polling-10a3eca`, `sha256:73e5c867303e18fdb136b1a4e290c865905a0f0a86a0e8c079537b29259aa5ed`, revision label equal to the commit, version label `1.2.1-polling`. `helpdesk-host:1.2.0-s02s03-b505868` (`sha256:4b22b647...`) is unchanged on disk. |
| Preflight | Production healthy (`/health/live` and `/health/ready` 200), delta sync 18:40Z with 0 consecutive failures, Cloudflare stopped. Subscription state `Failed`, expiry 2026-10-17 19:27Z, **subscription id already empty** (the earlier build discarded it on the failed renewal; this release keeps it from now on, so nothing is left to preserve). |
| Disposable tests | Copy of the live database in a no-egress container network (own object store; no Graph reachable). The new image's `migrate` applied only `20261016181257_AddSubscriptionError` (mail history 3 to 4; every other history and every table count identical). Previous image v1.2.0 booted healthy (live 200, ready 200, 0 errors) against the migrated schema; the new image did the same. The `mail-subscriptions` job could not be triggered on the copy, so its "no Graph call" behaviour rests on `InboundModeTests` (CI) and the code. The test containers, network and credential files were removed. |
| Backup (app stopped 18:57:21Z, 0 active application sessions) | `helpdesk-prod-20261016T185727Z.dump.enc`, objects bundle (`...T185729Z`, 92 entries) and configuration bundle (`...T185731Z`, 15 entries): checksums match, decryption and archive verified (71 tables with data); isolated restore into a disposable PostgreSQL identical to the quiescent live database (stable tables and migration history, roles applied, least privilege verified, 9 s); the three files on `I:\HelpdeskBackups\prod` are hash-equal. All earlier backups, volumes, certificates and credentials untouched. |
| Migration | `dev.ps1 prod-migrate` (Production administrator role, `HELPDESK_VERSION=1.2.1-polling-10a3eca`): only `AddSubscriptionError` applied. `mail.mailbox_sync.subscription_error` is `character varying(200)`, nullable. Row counts identical to the pre-migration baseline apart from the one history row; subscription state, expiry, client-state hash unchanged; delta token intact. |
| Deployment | `.env` (git-ignored): `HELPDESK_VERSION=1.2.1-polling-10a3eca`, `MAIL_INBOUND_MODE=Polling` (no conflicting override existed). `docker compose --project-directory . -p helpdesk --profile production up -d --no-deps --no-build app` recreated only the application (dry run first); healthy 18:58:42Z. PostgreSQL, the object store, Development and Cloudflare were not touched. |
| Validation | Running image id and revision as above; container env `Mail__InboundMode=Polling`, `ASPNETCORE_ENVIRONMENT=Production`; `/health/live` and `/health/ready` 200; `/api/auth/providers` entra true; dev-login 404; `/api/me` 401 unauthenticated; counts unchanged (1 user, 6 departments, 79 ticket types, 1 ticket, 2 messages, 1 mailbox). `health-check-production.ps1`: 19 checks, 0 FAIL, 1 WARN (restore test is manual); mailbox reported as "polling mode (intentional, no subscription)". 0 restarts, 0 errors, no permission denials, no subscription log lines. Delta syncs at 19:00, 19:05 and 19:10Z succeeded: `last_sync_at` 19:10:05Z, 0 consecutive failures, `last_sync_error` empty, `subscription_error` empty (the two fields are separate). 42 Hangfire jobs succeeded and 0 failed since the restart, including `mail-sync-all` every 5 minutes and `mail-dispatch`. |
| Not verified live | The manual "Retry subscription" route answering 409 and the 6-hourly `mail-subscriptions` job making no Graph request (next run 00:00Z): both need an Entra sign-in or the schedule, and are covered by `InboundModeTests` (CI). The Graph-side subscription was not queried or changed. No external mail was sent. |
| Rollback | Set `HELPDESK_VERSION=1.2.0-s02s03-b505868` in `.env` and run `docker compose --project-directory . -p helpdesk --profile production up -d --no-deps --no-build app`. The additive column stays (v1.2.0 ignores it); data written since 18:58Z is kept; the database is never restored automatically. v1.2.0 resumes webhook subscription maintenance, so the `Failed` subscription warning returns and the 00:00Z job will try to create a subscription (this fails while the tunnel is stopped). Pre-deployment backup `...20261016T185727Z` is for database recovery only, with separate approval. |
