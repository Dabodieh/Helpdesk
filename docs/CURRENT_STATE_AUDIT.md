# Current State Audit — 2026-10-13

> Updated after triage: see section 10 for findings, fixes and remaining issues. Sections 4–5 record the state at audit start.

Read-only audit. No application code, containers or Git history were changed by the audit. Evidence is labelled **[V]** verified this session, **[D]** taken from project documents/commit history, **[U]** untested/unverified.

## 1. Repository state [V]

- Branch `claude/optimistic-keller-a6385x`, HEAD `2333928`, tracks `origin` (same SHA). Working tree clean before this file was added. Single branch, no stashes, no extra worktrees.
- 167 commits, all authored as the same Git user. Codex work is identifiable only by the absence of the `Co-Authored-By: Claude` trailer and by commit content.
- Last Claude-trailer commit: `8223363` (2026-10-09 12:02, security and compliance audit).

## 2. Changes since the last Claude work [V]

| Commit(s) | Date | Content |
|---|---|---|
| `870a305`, `6824d34`, `d0caae4`, `f98e276`, `9ccd191` | 10-08 | "Graphite" UI redesign: shell/theme, administration, mailbox drawers, ticket queues/workspace, remaining pages, attachment controls gated (~900 lines, web only). |
| `24a6746` | 10-13 | Hardening: invitation link tokens moved to URL fragment and redacted from request paths; atomic invitation acceptance; Entra setup cancellation recovery; Permissions-Policy/COOP/CORP headers. 26 files, backend + web + tests + docs. |
| `21cdb39` | 10-13 | 60 files of design prototypes (`docs/design/redesign/*`), `.codex/agents/qa-security-engineer.toml`. |
| `2333928` | 10-13 | README refresh. |

Earlier mixed commits without trailer (Phase 3–6 work, 10-05/06) appear to be Claude work committed without the trailer; not separately attributed.

## 3. Implemented functionality by module [D, partly V]

Size (non-migration C#): Ticketing 16.8k lines, Mail 5.1k, Identity 1.8k, Organisation 1.7k, Knowledge 1.1k, Host 0.8k, Attachments 0.8k, Audit 0.4k, SharedKernel 0.6k; web 20.2k lines TS/TSX. 37 ADRs. Migration history in the local database matches the latest migration in source for all seven modules [V] (none pending).

- **Identity/Entra**: OIDC sign-in, in-app guided Entra setup (device code), account invitations and activation, platform admin vs department permissions (ADR-033/034/035/036/037). Dev sign-in route exists only when `Authentication:DevSignIn:Enabled`.
- **Organisation**: Departments (security boundary), members, mailboxes, requesters. Teams removed (`RemoveTeamConcept`).
- **Ticketing**: lifecycle, queues, assignment, notes, merge, followers, macros/canned responses, saved views, bulk actions, search, SLA, automation, reporting (Phase 7), request forms/portal, notifications, realtime hub.
- **Mail**: Graph provider + fake provider, inbound pipeline, delivery states, NDR handling, mailbox sync, backups scripts. Real tenant never exercised.
- **Knowledge**: articles, audiences, feedback, reply links.
- **Attachments**: S3/SeaweedFS store, pasted images; disabled in production pending scanner.
- **Frontend**: Graphite shell, agent workspace, portal, administration hub, analytics.

Not independently exercised in a browser this session [U]: all UI flows, mail send/receive, SLA timers, automation execution, realtime.

## 4. Validation results [V]

Run once, whole solution, local .NET SDK 10.0.401 against Compose PostgreSQL (host port 5433).

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| `Helpdesk.GraphSpike.Tests` | 43 | 0 | 0 |
| `Helpdesk.ArchitectureTests` | 68 | **8** | 0 |
| `Helpdesk.Host.Tests` (integration, 5m48s) | 970 | **1** | 0 |
| Web `vitest` (34 files) | 198 | 0 | 0 |
| Web `oxlint` | exit 0 (warnings only: refs-in-render, set-state-in-effect, fast-refresh) | 0 errors | — |
| Web `tsc -b && vite build` | success; main chunk 870 kB (>500 kB warning) | — | — |

Not run: Playwright e2e (needs the full running stack and writes to the dev database), Docker image builds, scale/load tests, migration round-trips.

## 5. Confirmed defects and incomplete functionality [V]

1. **Architecture tests: reviewed lists/boundaries are stale (8 failures, all test-side or hygiene, none security-leaking):**
   - Anonymous allow-list lacks `/api/auth/providers` (new anonymous endpoint, not reviewed).
   - Authenticated-only list lacks `GET /api/ticket-capabilities`, `POST /api/invitations/accept`, `POST /api/invitations/preview`.
   - `Mail` read-only-types list lacks `MailSyncAgeSignal`.
   - `Project_references_follow_the_module_dag`: `Organisation` now references `Identity` (not in the permitted DAG).
   - `Only_contracts_and_the_module_registration_class_are_public` fails for Identity, Mail, Organisation, Ticketing: EF migration classes are public. Looks like a rule that did not exclude migrations; fails on every module that gained migrations.
2. **Integration test regression:** `TicketTypesAndFieldsTests.Requesters_see_and_supply_only_requester_visible_fields_and_edit_only_editable_ones` fails deterministically (portal form GET returns 404, `PortalFormsService.GetAsync` path). Cause not diagnosed; may be the "new forms default restricted" audience rule or a seed/test setup not updated. Treat as unresolved until investigated.
3. **Frontend:** large single JS chunk (870 kB); lint warnings in `Composer.tsx`, `AnalyticsReportPage.tsx`, `workspace.test.tsx`.

## 6. Security, data integrity and deployment risks

- **CRITICAL (found and caused this session) [V]:** the running `hd-host` container is `ASPNETCORE_ENVIRONMENT=Development` with `DevSignIn` enabled and was reachable at `https://helpdesk.amb-it.co.uk` through the Cloudflare tunnel with no Cloudflare Access gate (unauthenticated `/api/auth/dev-login` returned 400, i.e. route live). `docs/reports/SECURITY-REMEDIATION-STATUS-2026-10-13.md` (F-03) explicitly says not to reconnect the tunnel to this instance. The tunnel was running; restarting the origin to fix a 502 re-exposed it. Tunnel was then stopped (`docker stop cloudflared`; public hostname now returns 530). One dev-login session was logged at 19:41:25 UTC, shortly after the origin restart; origin not attributed from logs. Treat any session created on that instance as untrusted and review audit events.
- **Running image is outdated [V]:** `helpdesk-host:local` built 2026-10-09 08:50; source has since changed (`24a6746`). `/api/auth/providers` returns 404 on it. The image's backend and the current Vite frontend differ.
- **Dev mode on public path [V]:** public hosting of a Development-mode instance is not acceptable; production profile (`ProductionHosting.cs`) exists but is not what is running.
- **Test database leakage [V]:** none left after the run (0 `helpdesk_test_*` databases).
- Open gates [D]: no real Graph tenant validation (D-16); no malware scanner (D-08, attachments off in production); Data Protection keys not encrypted at rest (D-13/F-04); retention policy unapproved (D-09/F-01); no recorded restore drill, no backup owner (F-02); rate limiting for admin/webhooks open (F-06); session lifetime policy undecided (F-08); log retention/alerting undecided (F-09).

## 7. Documentation / code inconsistencies [V]

- `docs/reports/SECURITY-REMEDIATION-STATUS-2026-10-13.md` describes the backend as running from source on localhost:5080 with the tunnel stopped; the actual state at audit start was the old `hd-host` container on 5080, Vite on 127.0.0.1, and the `cloudflared` container running (until stopped here).
- Architecture test allow-lists and project instruction file state the endpoint list is "reviewed"; three new endpoint groups were never added.
- Project `CLAUDE.md` still says attachments exist behind scanner gate; consistent. `AGENTS.md`/`.codex/agents` are present alongside `.claude/agents`; both state subagents are off by default.
- `docs/plans/DEVELOPMENT_PLAN.md` says Phases 0–7 "built"; project memory/notes said Phase 5 not started. The repo is the more recent source: Phases 5–7 are present.

## 8. Production-readiness blockers

Same as section 6 open gates, plus: running instance must be rebuilt from HEAD in Production configuration; Cloudflare Access (or equivalent) must be verified before any public exposure; architecture tests must be green before treating authorization allow-lists as trustworthy.

## 9. Prioritised next tasks (not started)

1. Keep the tunnel stopped until the Development instance is replaced or Cloudflare Access is verified on the hostname with an unauthenticated request redirecting to login. Review audit/session records from the exposure window.
2. Decide hosting posture for the public URL (production-mode image from HEAD behind Access) — needs user decision, deploy not authorised.
3. Investigate the failing portal form test (possible real access regression).
4. Refresh architecture-test allow-lists and the DAG/public-type rules with explicit review of each new endpoint, and decide whether Organisation→Identity is acceptable (ADR).
5. Close open gates in order: restore drill + backup owner, rate limiting, Graph tenant validation, scanner decision, retention policy.

## 10. Triage and stabilisation update [V]

**Dev sign-in session 19:41:25 UTC.** One session only (user `Dev Admin`, the standard development subject). Activity 19:41–19:42: SPA load, SignalR negotiate, read-only GETs, and one `POST /api/admin/microsoft-365/check-connection` (audit event `m365.connection_checked`, 19:42:29). No other writes, no user/role/department/ticket changes; `last_sign_in_at` changed only for `Dev Admin`. Pattern is an interactive browser session, timing matches the user's own check of the site after the origin was restored, but request logs hold no client IP so identity **cannot be proven**. Verdict: probably legitimate, no evidence of misuse, no data changed. The connection check is a read-only probe of Microsoft 365. Tunnel is stopped, so the session cannot be used externally. Revocation was not performed: the cookie scheme is protected by the shared Data Protection key ring, and rotating it signs out everyone and is a security-control change needing approval. If you did not make that request, treat as an incident: rotate the key ring and review Microsoft 365 app sign-in logs.

**Fixes (test/hygiene only; no behaviour or policy change; not committed):**

| Category | Root cause | Change |
|---|---|---|
| Host test `Requesters_see_and_supply_only_requester_visible_fields…` | Outdated test setup. Portal forms are served only for a Department with a usable active default mailbox (PR10-D22, `DepartmentsWithSenderAsync`, since `6bf9d0d`); the test created none. Policy is intentional and unchanged. | Test creates a mailbox (`fixture.CreateMailboxAsync`). |
| Anonymous allow-list | `GET /api/auth/providers` is anonymous by design; returns one boolean (Entra configured) for the sign-in page (ADR-037). | Added with justification. |
| Authenticated-only list | `GET /api/ticket-capabilities` (attachments flag, no Department data); `POST /api/invitations/preview` and `/accept` (token is the credential, service also requires the signed-in identity to match the invited address). Correct category; each reviewed. | Added with justification. |
| Mail read-only readers | `MailSyncAgeSignal`, `OutboundQueueAgeSignal`, `OutboundNeedingAttentionSignal` (aggregate metrics, no address/id) and `Microsoft365Service` (platform-admin page, `platform.departments.manage`, cross-Department mailbox health without ticket content, ADR-035). | Added with justification. |
| Module DAG | Real dependency is **Knowledge → Identity** (not Organisation → Identity, which was already expected): `KnowledgeAdminService` uses `IUserDirectory`/`ICurrentUser` from `Identity.Contracts`, since `081f660` (10-07). Same contract-only pattern as Organisation and Ticketing; the "no internals" boundary test passes. | Test expectation updated. No ADR existed for it; recommend recording one. |
| Public types outside Contracts (4 modules) | 10 migrations generated with EF's default `public` accessibility; 31 older ones are `internal` (the convention). Rule left intact. | The 10 classes changed to `internal` (one keyword each, no schema/behaviour change). |

**Targeted results:** Architecture suite 76/76 pass. Host: `TicketTypesAndFieldsTests`, `InvitationTests`, `EntraSetupTests`, `ProductionHostingTests` 39/39 pass (includes migration application through the test fixture). The full Host suite (971 tests) was not rerun.

**Remaining:** tunnel still stopped and the running `hd-host` image is stale and Development-mode; Knowledge→Identity ADR written (ADR-038); `.NET` migration files carry LF/CRLF warnings only; all section 6 open gates unchanged.

### Final result [V]

Complete backend suite, run once after the fixes (`dotnet test Helpdesk.slnx`): GraphSpike 43/43, Architecture 76/76, Host integration 971/971; 0 failed, 0 skipped. Frontend results in section 4 are unchanged (vitest 198/198, lint 0 errors, build passes). ADR-038 now records the Knowledge -> Identity.Contracts dependency. Changes are committed as one audit-fix commit (test setup, reviewed lists, `internal` migrations, ADR, this document).

Remaining blockers are unchanged: stale Development-mode `hd-host` image with the public tunnel stopped, no Cloudflare Access verification, and the production gates in section 6 (Graph validation, scanner, restore drill, key encryption, retention). Unattributable 19:41 dev-login session: see section 10.
