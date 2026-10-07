# Post-Refinement-10 team review (PARTIAL)

Baseline: commit `081f660`. Mode: read-only, static reading (no suites, no browser automation, no Docker rebuild, no Graph/tenant calls). Nothing in the application was changed.

## 0. Review coverage (read this first)

The session limit interrupted the review. Only four of the ten specialist reviews returned usable results. This report consolidates exactly those handbacks and invents nothing for the others.

| Specialist | Status | Prefix |
|---|---|---|
| Documentation / Content Designer | Completed | DOC |
| Frontend / UX Engineer | Completed | UX |
| SRE / Production Operations Engineer | Completed | SRE |
| QA / Reliability Engineer | Completed | QA |
| Lead Architect / Technical Lead | **Did not complete** (rate-limited) | ARC |
| ITSM Product & Service Desk Lead | **Did not complete** (rate-limited; no findings returned) | ITSM |
| Backend / Domain Engineer | **Did not complete** (rate-limited) | BE |
| Microsoft 365 / Messaging Engineer | **Did not complete** (rate-limited) | MSG |
| Security / Privacy Engineer | **Completed later (section 15)** | SEC |
| Data / Reporting Engineer | **Did not complete** (rate-limited) | DATA |

Completed specialists also returned their findings as text (the findings files in the scratchpad were not written, because subagents were refused file writes). All of their detail below is taken from those handbacks. Confidence is quoted where the specialist stated it; "not stated" means the handback did not give one. Every completed review was static and sampled, not exhaustive; their stated limits are in section 14.

**Areas that remain UNREVIEWED.** The absence of findings in these areas is not evidence that they are sound.

- **Security / Privacy (highest priority gap; now reviewed, see section 15, which supersedes this bullet):** Department isolation on read and write paths, IDOR/BOLA, non-disclosure, Internal Note leakage paths, requester identity and audience enforcement, attachment security, XSS/sanitisation, audit completeness, logging privacy, and the new `article-links` and `queue-types` endpoints. Several UX/QA findings below ask for server-side confirmation (UX-009, UX-012, QA-004) that nobody has yet done.
- **Messaging (Graph/Exchange):** inbound sync, subscriptions, delta, threading, outbound state machine, uncertain-send reconciliation, NDR/loop suppression, mailbox health. D-16 evidence status was not re-assessed (it stays OPEN: simulator evidence only).
- **Backend / Domain:** concurrency, data integrity, constraints, indexes, Hangfire job idempotency, SLA job correctness, migrations.
- **Architecture:** module boundaries, outbox/transaction design, SignalR boundary, ADR gaps.
- **ITSM product:** ticket lifecycle semantics, queue/assignment clarity, SLA semantics as users see them, requester language, admin discoverability.
- **Data / Reporting:** dashboard and SLA metric semantics, history capture for future reports.

Recommendation: re-run these six after the session limit resets (not done here, per instruction).

## 1. Executive summary

Of the four completed reviews, none found a Critical issue. The most important results come from operations: the product has no production deployment artefact, no persistent Data Protection keys, no backup/restore procedure and no metrics, so it is not ready to deploy yet. These are gaps in production-readiness work, not defects in the features built so far.

The frontend review found one real defect cluster in the agent workspace: global keyboard shortcuts stay live behind open dialogs (can tick a row checkbox behind a modal and feed an unintended bulk action), unreliable deep links from mention notifications, and a stale-version trap in the Knowledge editor after a failed publish. The QA review found that the realtime reconnect path does not reconcile after a long outage, and that several important recovery and concurrency paths are not covered by tests. The documentation review found two docs that contradict the locked "no fallback sender" rule and doc claims for features that do not exist.

Because Security, Messaging, Backend, Architecture, ITSM and Data did not complete, this report must not be read as a clean bill of health for those areas.

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 4 |
| Medium | 18 |
| Low | 22 |
| Polish | 8 |
| **Total** | **52** |

By specialist: DOC 11 (M2, L5, P4), UX 19 (M5, L11, P3), SRE 14 (H4, M7, L3), QA 8 (M4, L3, P1). Production blockers: **5** (SRE-001, SRE-002, SRE-003, SRE-005, SRE-006; SRE-006 is unverified, see its entry). Decisions requiring the user: **12** (section 11).

Overlaps merged: UX-005 with the Deactivate part of DOC-008; DOC-002 with SRE-008 (same missing `ROLE` mode); UX-016 tagged to QA-004 (idempotency of portal submit/reply). Counts above are per original finding ID; merged IDs stay listed together.

## 2. Production blockers

Only completed-specialist items are listed. Known gates D-16 (live Graph tenant validation), D-08 (malware scanner) and D-09 (retention policy) remain open and are in section 10; they are not new findings.

1. **SRE-001** No production deployment artefact (decision required).
2. **SRE-002** Data Protection keys not persisted (D-13; decision required).
3. **SRE-003** No backup/restore procedure or evidence (decision required).
4. **SRE-005** No metrics or alertable signals; poison outbox rows retry forever.
5. **SRE-006** Proxy trust configuration ships empty (unverified, needs one confirmation before treating as confirmed).

## 3. Critical findings

None reported by the four completed reviews. (Security, the specialist most likely to find Critical items, did not run.)

## 4. High findings

### SRE-001: No production deployment artefact
- **Specialist / category:** SRE / Deployment, Production Readiness. **Severity:** High. **Confidence:** not stated (static read).
- **Evidence:** `docker-compose.yml` is dev only. `Program.cs` has no `UseStaticFiles`/`MapFallback`, so nothing serves the SPA. No reverse proxy, WebSocket or environment guidance.
- **Affected area:** deployment topology, SPA hosting, SignalR through a proxy.
- **Impact:** there is no defined way to run the product in production; the SPA is not served by the host.
- **Smallest fix:** decide how the SPA is served (host static files vs separate web server) and the proxy; then provide one reference production compose/proxy configuration and a deployment/runbook document.
- **Decision required:** Yes (how the SPA is served; which proxy). **Production blocker:** Yes.

### SRE-002: Data Protection keys not persisted (D-13)
- **Specialist / category:** SRE / Production Readiness. **Severity:** High. **Confidence:** not stated.
- **Evidence:** no `AddDataProtection` anywhere in `src`.
- **Impact:** every container replacement invalidates all sessions, antiforgery tokens and any protected payloads. With more than one instance, cookies would not validate across them.
- **Smallest fix:** persist keys to a durable store (location decision), with an application name set.
- **Decision required:** Yes (key store location). **Production blocker:** Yes.

### SRE-003: No backup or restore procedure, script or evidence
- **Specialist / category:** SRE / Backup/Recovery. **Severity:** High. **Confidence:** not stated.
- **Evidence:** no backup/restore script or document found; PostgreSQL and the object-store bucket must be restored consistently and nothing says how.
- **Impact:** no demonstrated recoverability. A backup without a restore drill must not be treated as recoverability.
- **Smallest fix:** document and script PostgreSQL + attachment backup, and perform and record one restore drill.
- **Decision required:** Yes (RPO/RTO, storage target). **Production blocker:** Yes.

### SRE-005: No metrics; stuck work is not observable
- **Specialist / category:** SRE / Observability. **Severity:** High. **Confidence:** not stated.
- **Evidence:** `Program.cs:68-77` is traces-only; ADR-008's metrics, denylist and correlation id are not implemented. `NotificationOutbox.cs:144-169` retries a poison row every minute indefinitely, with no counter or age signal.
- **Impact:** nothing can alert on stale mail sync, stuck outbox rows, failed Hangfire jobs or SLA job lag; a poison notification row would loop forever silently.
- **Smallest fix:** add a small set of operational counters/gauges (oldest unprocessed outbox row age, failed jobs, last mail-sync age, SLA job lag) and cap or park poison outbox rows with a visible state.
- **Decision required:** No. **Production blocker:** Yes.

## 5. Medium findings

### SRE-006: Proxy trust configuration ships empty
- **Specialist / category:** SRE / Security (deployment). **Severity:** Medium. **Confidence:** Low (the specialist recalled the framework behaviour and did not verify it).
- **Evidence:** `Program.cs:56-66`, `appsettings.json:25` ship `Proxy:KnownProxies=[]`; nothing enforces it outside dev.
- **Impact / abuse scenario (if confirmed):** with no known proxies or networks configured, forwarded headers may be trusted from any caller, so a client could spoof its address (rate limiting, logging, audit source IP).
- **Smallest fix:** verify the framework behaviour with one targeted check; if confirmed, require explicit proxy configuration in Production (fail startup if absent).
- **Decision required:** No. **Production blocker:** Yes, until verified otherwise.

### SRE-004: Readiness check covers PostgreSQL only
- `/health/ready` checks only Postgres; no object store, Hangfire or migration-state signal; no image `HEALTHCHECK`. **Impact:** an instance with a broken object store or stalled jobs reports ready. **Fix:** add object-store and job-processing signals to readiness. **Decision required:** Yes (what should make the instance "not ready"). **Blocker:** No. **Category:** Operations.

### SRE-007: Per-module migrations with no cross-module atomicity or rollback policy
- Migrate runs module by module; the documented order lists 3 of 8 modules. **Impact:** a failure mid-way leaves modules at different schema versions with no stated recovery. **Fix:** document the full order and a rollback/restore policy. **Decision required:** Yes. **Blocker:** No. **Category:** Deployment.

### SRE-008: Single-host assumptions (presence in memory; `ROLE` not implemented)
- Presence is in memory; ADR-006 `ROLE=web|worker|all` is not implemented (merged with DOC-002). **Impact:** cannot run web and worker separately or scale out without presence and realtime gaps. **Fix:** either document single-instance as the supported model or schedule the role split. **Decision required:** Yes. **Blocker:** No. **Category:** Architecture/Operations.

### SRE-009: Mail sync and dispatch share a small worker pool with a fixed lock and no retry
- `SyncAll`/`Dispatch` run serially with a 120 s lock and no retry on 4 shared workers. **Impact:** a slow Graph can stall mail processing and other jobs. **Fix:** review lock timeout, per-mailbox isolation and retry. **Decision required:** No. **Blocker:** No. **Category:** Reliability. (Overlaps the unreviewed Messaging/Backend scope; confirm there.)

### SRE-010: Production accepts unsafe storage/scan settings
- Dev sign-in and the Fake mail provider are guarded and cannot be enabled in Production, but the guards key on the environment name only; `Storage:Provider=InMemory` and `RequireCleanScan=false` are accepted in Production. **Impact:** a misconfigured production could lose attachments on restart or skip scanning. **Fix:** fail startup in Production for those values (D-08 related). **Decision required:** No. **Blocker:** No. **Category:** Deployment/Security.

### SRE-011: No pruning or sizing for growing tables; console-only logs
- No pruning for the outbox, notifications, mail, or attachment orphans; logs go to the console only. **Impact:** unbounded growth; no retained logs. **Fix:** size estimates and a retention approach (ties to D-09), plus a log sink decision. **Decision required:** Yes. **Blocker:** No. **Category:** Operations.

### UX-001: Workspace shortcuts stay live behind open dialogs
- **Specialist / category:** Frontend / Accessibility. **Confidence:** High.
- **Evidence:** `pages/WorkspacePage.tsx` (r, n) and `components/QueuePane.tsx` (j, k, arrows, Enter, x, /) call `useShortcuts` with no enabled flag; only `AppShell.tsx:63` gates on `dialog === null`. `lib/shortcuts.ts` ignores only INPUT, TEXTAREA, SELECT and contentEditable.
- **Impact:** with focus on a button inside the Run macro dialog, `r` moves focus to the composer behind the modal; `x` ticks a row checkbox behind the dialog, which can pull unintended tickets into a later bulk action.
- **Fix:** make the hook inert while a modal dialog is open (or pass an `enabled` flag). **Decision:** No. **Blocker:** No.

### UX-002: Notification deep link to `#msg-` is unreliable
- **Category:** Bug. **Confidence:** Medium. **Evidence:** `components/Conversation.tsx`: the message ref callback calls `scrollIntoView` on every re-render while the hash is present, and the container's effect sets `scrollTop = scrollHeight` after it, so the bottom scroll wins on first load. **Impact:** a mention notification lands on the newest message, and later renders pull the scroll back. **Fix:** scroll to the hash once in an effect after the bottom scroll; skip the bottom scroll when a hash is present. **Decision:** No. **Blocker:** No.

### UX-003: Article Publish leaves a stale version after a partial failure
- **Category:** Bug. **Confidence:** Medium. **Evidence:** `components/KnowledgeSection.tsx` `EditorForm.save` runs `saveDraft` then `publishArticle`; cache and `article` prop update only on success. **Impact:** if publish fails after the save succeeded, every retry gets a version conflict until the article is closed and reopened. **Fix:** update the cache after each step or on error. **Decision:** No. **Blocker:** No.

### UX-004: Row action buttons in admin tables share identical accessible names
- **Category:** Accessibility. **Confidence:** High. **Evidence:** bare "Edit" in `AutomationsSection.tsx:328`, `BusinessHoursSection.tsx:265`, `CannedManager.tsx:97`, `FormsSection.tsx:347`, `HelpTopicsSection.tsx:126`, `KnowledgeSection.tsx:376` (plus "Owner & review"), `MacrosSection.tsx:336`, `SlaPoliciesSection.tsx:206`, `TicketFieldsSection.tsx:203`, `TicketTypesSection.tsx:208`; the same for Deactivate, Retry and Resolve in `MailboxesSection.tsx`. `MembersSection.tsx` and `ViewsMenu` already use the correct pattern. **Impact:** screen-reader and voice users cannot tell controls apart and may change the wrong policy. **Fix:** apply the existing sr-only suffix or `aria-label` pattern. **Decision:** No. **Blocker:** No.

### UX-005 (+ DOC-008, part): Mailbox Deactivate is one click with no consequence warning
- **Category:** Administration. **Confidence:** Medium. **Evidence:** `components/MailboxesSection.tsx` `MailboxRow` calls `patch.mutate({ isActive: !m.isActive })` directly. Under the locked rule, a ticket whose stored mailbox is inactive cannot send public replies. **Impact:** one click blocks replies on every ticket that uses the mailbox; the default mailbox can be deactivated the same way. **Fix:** confirm dialog stating that replies on tickets using this mailbox are not sent until it is reactivated. **Decision required:** No for the confirmation wording; Yes only if you also want the UI to block deactivating the default mailbox. **Blocker:** No.

### DOC-001: API/email docs contradict the locked "no fallback sender" rule
- **Category:** Documentation. **Confidence:** not stated. **Evidence:** `docs/api/PHASE3_API.md:30` says sender identity is "the ticket's mailbox if active, else the department default"; `docs/architecture/EMAIL_ARCHITECTURE.md:161` (14c) says "Only tickets without a mailbox use the default." Both contradict `CLAUDE.md` and commit `ec64c07`. **Impact:** a reader (or future implementer) could reintroduce the fallback. **Fix:** state that the reply is recorded NotDelivered (`no_ticket_mailbox` / `mailbox_unavailable`) and that the default applies only at ticket creation. **Decision:** No. **Blocker:** No.

### DOC-002 (+ SRE-008): Docs claim features that do not exist
- **Category:** Documentation. **Evidence:** `ARCHITECTURE.md:4` claims `ROLE=web|worker|all` and `:49` claims OpenAPI-generated types; nothing reads `ROLE` (only a comment in `HangfireSetup.cs:11`), there is no generator script, and the BACKLOG lists both as open. **Impact:** misleads operators and developers. **Fix:** mark both as planned. **Decision:** No. **Blocker:** No.

### QA-001: Realtime client does not reconcile after a long outage
- **Category:** Reliability. **Confidence:** code-proven (per the specialist). **Evidence:** `src/web/src/lib/realtime.ts:180-186` `onreconnected` invalidates only `['tickets']`, `['ticket']`, `['notifications']`, never `['portal']`; after auto-reconnect gives up (about 47 s), `onclose` retries `start()` after 15 s (`:190`) and that success path (`:195-199`) invalidates nothing; `setConnectionFactory` (`:152`) is used by no test. **Impact:** after a long disconnection the UI shows "live" while missed events are never refetched. **Fix:** one shared reconcile function called from both paths and including `['portal']`; add one Vitest with a fake connection. **Decision:** No. **Blocker:** No.

### QA-002: Stuck-`Sending` outbound recovery is untested
- **Category:** Testing. **Evidence:** `MailSync.cs:329-345` requeues stale `Sending` rows or reconciles them if `submit_started` is set; no test mentions `Sending`; `MailOutboundTests.cs:76` forces `state='Queued'`, which a real crash never leaves; `submit_started` is matched by a raw `LIKE '%submit_started%'`, so a rename would silently turn reconcile into a blind resend. **Impact:** a regression could cause duplicate email. **Fix:** two tests (one per branch) with the flag produced by the real path. **Decision:** No. **Blocker:** No. (Not a demonstrated bug.)

### QA-003: Portal test helper auto-links requesters by raw SQL
- **Category:** Testing. **Evidence:** `Portal/PortalKit.cs:19-60` `TicketForAsync` inserts into `ticketing.requester_links` when an agent creates a ticket for a registered employee's address. **Impact:** production never does this ("email is not proof"), so many portal/merge/history tests start from an unreachable ownership state. **Fix:** add a negative control (the ticket is not visible in `/api/portal/requests` until linked) and use the real admin-resolution path in a few tests. **Decision:** No. **Blocker:** No.

### QA-004: Concurrency and duplicate coverage stops at ticket and organisation writes
- **Category:** Testing / Concurrency. **Evidence:** not raced: mailbox sync (advisory lock, `MailSync.cs:28`), the outbound claim (`OutboundMail.cs:186`; the test only dispatches sequentially), portal reply double-submit, and bulk versus a concurrent single edit (bulk passes `expectedVersion: null`, so it overwrites). **Impact:** duplicate or lost work could go unnoticed. **Fix:** targeted concurrency tests. **Decision required:** Yes for two points: should a duplicate portal reply be de-duplicated, and is bulk last-write-wins by design? **Blocker:** No.

## 6. Low findings

### SRE-012: Log privacy is convention only
- Nothing enforces redaction (ADR-008 denylist not implemented). **Fix:** implement the denylist or a test. **Decision:** No. **Blocker:** No. **Category:** Observability/Privacy.

### SRE-013: No runbook for dependency-down cases
- No documented behaviour/steps for PostgreSQL, object store or Graph unavailable. **Fix:** short runbook. **Decision:** No. **Blocker:** No. **Category:** Operations.

### SRE-014: Floating image tags, `:latest` SeaweedFS, `AllowedHosts=*`
- Unpinned images and unrestricted host header. **Fix:** pin tags; set `AllowedHosts` for production. **Decision:** No. **Blocker:** No. **Category:** Deployment.

### UX-006: Reorder arrows lose focus; one reorder dialog can race
- `HelpTopicsSection.tsx` disables arrows while pending (focus drops); `QueuePrefsMenu.tsx` disables arrows at list ends; `KnowledgeSection.tsx` `CategoryDialog` has no pending guard and builds the order from stale props. **Impact:** keyboard users lose their place; fast double click sends mismatched orders. **Fix:** `aria-disabled` plus early return; add the pending guard. **Confidence:** Medium. **Category:** Accessibility.

### UX-007: Focus not restored after choosing from the ticket More menu
- `components/TicketLinks.tsx` `MoreMenu` closes the menu before opening the dialog, so `Dialog.tsx` records `body` as the opener. **Impact:** focus lands at the top of the page afterwards. **Fix:** focus the trigger first or add a `returnFocusTo` prop. **Confidence:** High. **Category:** Accessibility.

### UX-008: Esc or Close silently discards typed work
- `Dialog.tsx` always closes on Esc; `NewTicketDialog.tsx` and the Knowledge article editor (12-row body) have no dirty check, unlike the composer. **Fix:** optional `dirty` prop with confirmation. **Confidence:** Medium. **Category:** UX.

### UX-009: BulkBar sends a hidden Team/Assignee choice after the selection spans departments
- `components/BulkBar.tsx` does not reset `team`/`assignee` when `needDept` becomes true; the selects are disabled but `change` still includes them. **Impact:** Apply may send a team picked for department A with a ticket from department B; the server should reject per ticket but the agent cannot see what is sent. **Fix:** clear or drop those values. **Tag:** Security must confirm per-ticket server rejection (UNREVIEWED). **Confidence:** Medium. **Category:** Authorization (UX).

### UX-010: Portal "My requests" tabs use ARIA tab roles without the keyboard model
- `pages/portal/PortalRequests.tsx`: `role=tab` with no arrow keys, roving `tabIndex` or `aria-controls`; the shared `Tabs` in `components/common.tsx` is correct. **Fix:** reuse `Tabs` or use `aria-pressed` buttons. **Confidence:** High. **Category:** Accessibility.

### UX-011: Saved-view Delete is immediate; Archive saves the draft without saying so
- `ViewsMenu.tsx:102` deletes without confirmation (members removal confirms); the Archive mutation in `KnowledgeSection.tsx` calls `saveDraft` first. **Fix:** confirm the delete; state or skip the draft save. **Confidence:** High. **Category:** UX.

### UX-012: Retry sending checks permission against any department
- `components/Conversation.tsx` `Delivery` uses `me.memberships.some(canTicket(me,'tickets.reply',…))` instead of the ticket's department. **Impact:** UX only (the server authorises); a user may see a Retry button that errors. **Fix:** pass the ticket's department. **Confidence:** Medium. **Category:** Authorization (UX).

### UX-013: Mention picker ARIA does not match behaviour
- `Composer.tsx`: `role=listbox` with `role=option` buttons and constant `aria-selected="false"`; no arrow keys, `aria-controls`, `aria-expanded` or `aria-activedescendant`; results not announced. **Fix:** implement combobox behaviour, or drop roles and add a polite result count. **Confidence:** Medium. **Category:** Accessibility.

### UX-014: Portal file picker silently truncates to 5; no feedback after reply or submit
- `PortalSubmit.tsx`, `PortalRequest.tsx` use `.slice(0,5)` with no message; reply success shows no status; submit-success does not move focus. **Fix:** messages and focus (wording to Content). **Confidence:** High. **Category:** Requester Experience.

### UX-015: Mailbox edit shows a stale name; 8-column table has no overflow handling
- `MailboxRow` initialises `useState(m.displayName)` once, so a concurrent rename is overwritten on Save; `table.grid` has no overflow wrapper (about 900 px usable at 1366 wide, a static estimate, not measured). **Fix:** reset the name when the form opens; wrap the table; one laptop-width check. **Confidence:** Low. **Category:** Administration.

### UX-016 (tag QA-004): Portal state persists across route params
- `PortalRequest.tsx` keeps `text`/`files` when `:reference` changes; `PortalSubmit.tsx` `RequestForm` keeps `subject`, `values`, `submissionId` when `formId` changes; neither is keyed. **Impact:** a draft or idempotency id could carry to another request/form. **Fix:** key by `reference`/`formId`. **Tag:** Backend for idempotency scope (UNREVIEWED). **Confidence:** Low. **Category:** Requester Experience.

### DOC-003: Stale phase and module statements
- `DEVELOPMENT.md`: schemas listed as audit/identity/organisation only, "Graph added in Phase 3"; `ARCHITECTURE.md` module table still "Planned" with no Knowledge row; `PRODUCT.md`/`DEVELOPMENT_PLAN.md` still list "tags" (a locked exclusion), "later a portal", and omit Refinements 1-11. **Fix:** refresh. **Category:** Documentation.

### DOC-004: Docs say "full suite at milestones", contradicting `CLAUDE.md`
- `DEVELOPMENT.md`, `DEVELOPMENT_PLAN.md`. **Fix:** align with "full suite only on explicit request". **Category:** Documentation.

### DOC-005: Demo tooling and operations runbook missing from docs
- `dev-demo-seed`/`dev-demo-reset` undocumented in `DEVELOPMENT.md`; `docs/guides` has only the Graph runbook; README links only five documents. **Category:** Documentation.

### DOC-006: Automation history shows raw codes
- `AutomationsSection.tsx:145,198,211` show `loop_detected`, `max_depth`, `addInternalNote`. **Fix:** reuse `ACTION_LABEL` / `RESULT_TEXT`. **Category:** Copy/Terminology.

### DOC-008 (rest): Weak confirmation copy
- The merge dialog never says it cannot be undone; the member-removal `window.confirm` is vague. (The mailbox part is under UX-005.) **Category:** Copy/Terminology.

### QA-005: Negative realtime leak tests rely on fixed sleeps
- `RealtimeTests.cs:97-105` (1200 ms) and `:361, 416, 454, 488` (300 ms); a late leaked event gives a false pass; the presence test mixes wall clock with an injected sweep time. **Fix:** send a positive sentinel after the silence window; use `AdjustableClock`. **Category:** Testing.

### QA-006: Recurring-job registration is never verified
- `Jobs:Enabled=false` in all tests, so the registration of the five recurring jobs (`HangfireSetup.cs:62-76`) is untested. **Category:** Testing.

### QA-007: Structural guard coverage is Ticketing only
- Architecture tests cover Ticketing, not Knowledge, Mail or Attachments queries. **Decision required:** structural guard there, or accept example-based coverage? **Category:** Testing.

## 7. Polish / copy / visual findings

- **UX-017:** Dashboard subtitle exposes roadmap wording ("Not a history: reports come later") in `pages/DashboardPage.tsx`. Remove the sentence.
- **UX-018:** Dead realtime foreign-change code (`lib/realtime.ts` `useRemoteChange`/`clearRemoteChange` have no non-test callers; the module-level `remote` map grows for the session). **Decision required:** remove, or wire an "updated by someone else" notice?
- **UX-019:** Small items: breadcrumb in `DepartmentPage.tsx` where "Administration" and "Departments" both link to `/admin`; unnamed trailing `<th />` in `KnowledgeSection.tsx` and `MailboxesSection.tsx`; Knowledge title search is not debounced; every failed message renders `role=alert` in `Conversation.tsx` with no live region for new messages; `.notify-panel` hard-coded at `left:190px; top:5.25rem`; no loading state in `NotificationBell`; spacing typo `defaultStatuses =mode` in `QueueFilters.tsx`.
- **DOC-007:** `MembersSection.tsx` shows roles as "DepartmentAdmin" / "TeamLead" (should read as names).
- **DOC-009:** Server 400 messages read "q: q must be at least 2 characters." (not verified at runtime).
- **DOC-010:** The `CLAUDE.md` collaboration "planned order" is already built, and `MVP.md` has no historical marker; the mailbox-assignment workflow question exists only in `CLAUDE.md`. **Decision required:** register it as a row in `OPEN_DECISIONS.md`?
- **DOC-011:** Documentation mixes "authorization" and "authorised" (pick one spelling convention).
- **QA-008:** Bulk edit on a merged source is untested; e2e has `retries: 0` and a fixed `waitForTimeout` at `portal.spec.ts:273`.

## 8. Cross-cutting root causes

1. **Production hosting was never designed as a deliverable.** SRE-001/002/003/005/006/007/010/011/014, DOC-002 and DOC-005 all come from the same gap: the build targets development, with ADR-008/006/010 intentions (metrics, role split, deployment) not yet implemented or written down.
2. **Docs are not updated when behaviour is decided.** DOC-001/002/003/004/010 show decisions (no fallback sender, single-agent testing policy, finished roadmap items) not propagated into older documents.
3. **Dialog and keyboard behaviour is handled per component, not centrally.** UX-001, 006, 007, 008, 013 share one cause: `useShortcuts` and `Dialog` have no common contract (modal awareness, focus return, dirty-guard).
4. **Mutation sequences without step-wise cache updates.** UX-003, UX-015 and UX-016 follow one pattern: client state is updated only on overall success or only on mount.
5. **Realtime is advisory but its recovery path is weakly specified and untested.** QA-001, UX-018 and QA-005.
6. **Recovery and concurrency paths are tested only on happy or sequential paths.** QA-002, 003, 004, 006, 007.

## 9. Test gaps (from QA, and gaps flagged by UX/SRE)

Highest-value targeted tests to add (no broad suites):
1. Realtime reconcile after reconnect and after the manual restart path, including `['portal']` (QA-001).
2. Stuck-`Sending` recovery: both branches, with the real `submit_started` flag (QA-002).
3. Negative control that an agent-created ticket is not portal-visible until a real link exists (QA-003).
4. Concurrency: mailbox sync lock, outbound claim, portal reply double-submit, bulk vs concurrent edit (QA-004; two need decisions).
5. Sentinel-based negative realtime assertions instead of fixed sleeps (QA-005).
6. Recurring job registration (QA-006).
7. Structural guards for Knowledge/Mail/Attachments queries, if wanted (QA-007).
8. Frontend: shortcut suppression behind dialogs (UX-001), BulkBar clearing hidden choices (UX-009), Knowledge editor retry after partial failure (UX-003).
9. Server-side confirmation (not performed) that bulk change rejects a foreign-department team/assignee per ticket (UX-009).

Strengths noted by QA: tests use the real stack (dev-login, real migrations, database per fixture, real CSRF); isolation, revocation and Internal Note realtime paths are well covered; architecture tests pin the anonymous/authenticated-only endpoint lists, the CSRF-exempt webhooks and every `IgnoreDepartmentScope()` caller; mail covers uncertain and duplicate cases.

## 10. Production-readiness gates

Open gates known from the project (not re-assessed by any completed specialist unless stated):
- **D-16** live Graph tenant validation: OPEN (simulator evidence only). Messaging review did not complete.
- **D-08** malware scanning gate: open (SRE-010 adds that `RequireCleanScan=false` is accepted in Production).
- **D-13** persistent Data Protection keys: open (SRE-002).
- **D-09** retention policy: open (SRE-011 pruning depends on it).
- **Backup/restore evidence:** open (SRE-003).
- **New from this review:** production deployment artefact (SRE-001), metrics/alerting (SRE-005), proxy trust confirmation (SRE-006).
- **Security review of the delivered product:** not done; this is a gate in its own right.

## 11. Decisions required from you (12)

1. **SRE-001** How is the SPA served (host static files vs separate server) and which reverse proxy?
2. **SRE-002** Where do Data Protection keys live?
3. **SRE-003** Backup targets, RPO/RTO, and who performs the restore drill?
4. **SRE-004** What should make an instance "not ready" (object store, jobs, migrations)?
5. **SRE-007** Migration order/rollback policy across the eight modules (restore-from-backup vs down migrations)?
6. **SRE-008** Is single-instance the supported model, or is the web/worker role split to be built?
7. **SRE-011** Retention/pruning and log sink approach (depends on D-09).
8. **UX-005** Beyond a consequence confirmation, should the UI block deactivating the default mailbox?
9. **UX-018** Remove the dead foreign-change realtime code, or wire an "updated by someone else" notice?
10. **DOC-010** Register the mailbox-assignment workflow question in `OPEN_DECISIONS.md`?
11. **QA-004** Should a duplicate portal reply be de-duplicated? Is bulk last-write-wins by design?
12. **QA-007** Structural guards for Knowledge/Mail/Attachments, or accept example-based coverage?

(The two parts of QA-004 count as one decision here.)

## 12. Recommended remediation order

1. Re-run the six unreviewed specialist reviews, **Security first**, then Messaging and Backend. Fix nothing security-related until that exists.
2. Resolve decisions 1-3 and 7, then build production readiness: deployment artefact and proxy trust check (SRE-001, SRE-006), persistent Data Protection keys (SRE-002), backup/restore plus one restore drill (SRE-003), minimal metrics and poison-row handling (SRE-005, SRE-004).
3. Fix confirmed workspace defects: UX-001 (shortcuts behind dialogs), UX-003 (stale publish version), UX-002 (notification deep link), QA-001 (realtime reconcile) with its test.
4. Documentation fixes that remove contradictions with locked rules (DOC-001, DOC-002, DOC-004, DOC-003).
5. Accessibility sweep: UX-004, 006, 007, 010, 013.
6. Safety/clarity: UX-005 (mailbox deactivate), UX-008, UX-009, UX-011, DOC-008.
7. Recovery and concurrency tests: QA-002, QA-003, QA-004 (after decision), QA-005, QA-006.
8. Remaining Low and Polish items together in one pass.

## 13. Strengths / areas not to disturb

- **Dialogs and focus:** `Dialog.tsx` Esc handling, focus trap, autofocus hook, opener restore (UX).
- **Composer:** mode clarity (words, icon, banner, never colour alone), per-ticket drafts, collision hint (UX).
- **Status semantics in the UI:** status, priority, SLA and health marks are glyph plus text; light/dark tokens have decent contrast; a global focus-visible ring exists (UX).
- **Caching and concurrency in the UI:** `useApplyTicket` cache handling, version tokens on edits, mutation buttons disabled while pending (UX).
- **Queue:** table semantics, URL-held filters, bulk selection cleared on filter change (UX).
- **Realtime stance:** presence and realtime are advisory only; REST is the source of truth (UX).
- **Portal:** idempotent `submissionId`, attachment partial-failure message, skip link; two-step requester-link confirmation; uncertain-send duplicate warning (UX).
- **Copy:** portal copy consistently says "request", never "ticket"; "Internal note" naming is consistent; British spelling holds in user-visible strings; non-disclosing 404 wording is accurate; mailbox-unavailable delivery copy matches the locked rules; empty states are actionable; `OPEN_DECISIONS` matches the known gates (DOC).
- **Operations basics:** environment guards on dev sign-in and the Fake provider; Secure/HttpOnly/SameSite cookies; HSTS; read-only Hangfire dashboard limited to platform admins; migrations off at startup by default with an explicit `migrate` command; non-root image; loopback-bound dev ports; secrets kept out of appsettings; separate live and ready probes (SRE).
- **Test foundation:** see section 9 (QA).

## 14. Limits of the completed reviews

- DOC: grep-based sample. Did not read the API docs, threat models, reports, ADR bodies, `UX.md` or `AGENTS.md` line by line; no runtime checks.
- UX: static reading only, no browser, Playwright or Vitest; layout points are estimates; Automations, RuleEditors, Forms, TicketFields, TicketTypes, SLA, BusinessHours, Macros, Canned, TicketProperties/Followers, RequesterDrawer, TeamPage, ProfilePage, markdown/RichText, PortalKnowledge, StatusPage and DepartmentsPage were only skimmed or grep-checked; contrast judged from token values; server-side behaviours (version conflicts, bulk authorisation, idempotency scope) were not verified.
- SRE: no Docker build or run, no restore drill, no live DB/container; ForwardedHeaders and EF advisory-lock behaviour unexercised.
- QA: about 60 test files assessed by name inventory and grep, not read in depth; nothing was run; mail protocol and Fake-provider versus Graph fidelity not audited. Only QA-001 is a code-proven product defect; QA-002 to QA-004 are evidence gaps, not demonstrated bugs.
- Not reviewed by anyone: the Refinement 9 and 10 backend code (queue preferences, queue filters, knowledge owner/review date, reply-link endpoint) beyond what the above touched.


---

# 15. Security / Privacy review (added after the partial report)

Specialist: Security / Privacy Engineer (AGENTS.md section 12). Baseline: commit `081f660`, code unchanged since. Method: single agent, read-only, static reading of the code paths below plus a handful of read-only GET probes against the local dev API as the bootstrap Platform Admin (`dev-admin`, not a member of the DEMO department) to confirm one finding. No tests, builds or mutations were run; DEMO data was not changed. Existing Frontend/QA/SRE/Content findings were reused, not repeated; cross-references are given where they overlap.

**Result in one line:** the ticket content boundary is well built (department scope filter on tickets/messages/requesters, 404 non-disclosure, per-ticket re-authorisation in bulk, Internal Note gates in REST, search, snippets, notifications, mentions, presence and SignalR). The one confirmed exposure is a Platform Admin side door through an administrative mail endpoint. The rest are abuse-control, attachment-policy and trust-assumption gaps.

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 1 |
| Medium | 3 |
| Low | 8 |
| Polish | 1 |
| **Total** | **13** |

Production blockers: **3** (SEC-001, SEC-002, SEC-003). Decisions required: **5** (SEC-002, SEC-004, SEC-006, SEC-007, SEC-012).

## 15.1 High

### SEC-001: Platform Admin reads sender addresses and subjects of any department's problem mail
- **Specialist / category:** Security / Authorization, Privacy. **Severity:** High. **Confidence:** High (code read and confirmed at runtime).
- **Evidence:** `GET /api/departments/{id}/mail/attention` is guarded by `RequirePermission(DepartmentMailboxesManage)` (`MailEndpoints.cs:55-57`). That is an administrative permission, which the authorisation handler grants to any Platform Admin for any existing department without a membership (`PermissionAuthorizationHandler.cs:44-48`). `MailboxService.AttentionAsync` (`MailboxService.cs:244-277`) filters only by `DepartmentId` and returns `Address` (the sender address) and `Subject` (first 200 characters) for Failed, Uncertain and Ignored mail; `MailDbContext` has no department scope filter. Runtime, as `dev-admin` (member only of IT Support): `GET .../IT Support (Demo)/mail/attention` returned 200 with 2 items containing `address` and `subject`, while `GET .../dashboard` for the same department returned 404. Values were not printed or recorded. The existing test only asserts the platform admin can list mailboxes (`MailAdminApiTests.cs:32`); none covers `attention`.
- **Affected area:** Mail administration API; Platform Admin separation.
- **Impact / abuse scenario:** a Platform Admin with no membership in HR calls the attention endpoint for HR and reads who emailed the HR mailbox and the subject lines of ignored, failed and uncertain mail (for example a subject naming an employee or a grievance). This is ticket-content metadata and breaks the locked rule that a Platform Admin gets no implicit Department content access. The ticket dashboard, queues and tickets correctly answer 404 for the same caller.
- **Smallest recommended fix:** in `AttentionAsync`, return subject and sender address only when the caller also holds `tickets.read` in that department (by membership); otherwise return the operational fields (state, category, attempts, ids) with those two fields omitted. Add one test for a non-member Platform Admin.
- **Decision required:** No. **Production blocker:** Yes (violates a hard rule).
- **Related:** SEC-005, SEC-012 (same root cause: administrative permission plus data not covered by the scope filter).

## 15.2 Medium

### SEC-002: Attachment policy is a short extension blocklist with no scanner behind it (D-08)
- **Specialist / category:** Security / Production Readiness. **Severity:** Medium. **Confidence:** High (code), the exposure depends on D-08 status.
- **Evidence:** `AttachmentValidator.cs` blocks 26 executable and script extensions and checks magic bytes for common formats; everything else is stored and downloadable as `application/octet-stream`. Not blocked: macro-enabled Office (`docm`, `xlsm`, `pptm`), disk and container images (`iso`, `img`, `vhd`), `html`/`htm`/`svg`/`xhtml`, `url`/`scf`, `one`, `xll`, `msix`/`appx`. A `zip` passes on its signature alone. `RequireCleanScan=false` is accepted in Production (SRE-010).
- **Impact / abuse scenario:** an external sender mails a macro-enabled workbook or an `.iso` containing malware to a department mailbox. It is ingested as an attachment of a ticket, and an agent downloads and opens it. With no scanner the blocklist is the only control, and download is not blocked while scan state is `NotScanned` unless `RequireCleanScan` is on. Download is correctly forced as an attachment with `nosniff` and an API CSP, so the risk is endpoint malware, not in-app XSS.
- **Smallest recommended fix:** extend the blocked-type list for the extensions above, and make Production startup fail when `RequireCleanScan` is false (SRE-010) until a scanner exists.
- **Decision required:** Yes (which additional types the organisation wants blocked, for example whether legitimate macro workbooks must be allowed after scanning). **Production blocker:** Yes (existing gate D-08).
- **Related:** SRE-010.

### SEC-003: No rate limiting or abuse controls on authenticated endpoints; large uploads are buffered in memory
- **Specialist / category:** Security / Production Readiness. **Severity:** Medium. **Confidence:** High.
- **Evidence:** no `AddRateLimiter` or request-size policy exists in `src` (the only throttle is the inbound mail sender window, `InboundProcessor.cs:145-152`). Portal create (`PortalRequestService.CreateAsync`) and reply have no per-account limit. A reply accepts up to 5 files of 25 MB each (`PortalRequestService.cs:86`, `AttachmentOptions.cs:9`), read into `byte[]` in memory. `ReadFormAsync` runs with framework default limits. Search uses `ILIKE '%term%'` over message bodies.
- **Impact / abuse scenario:** any signed-in employee scripts the portal to create thousands of requests (each queueing an acknowledgement email) or posts concurrent 125 MB replies, exhausting memory, storage, the mail queue or database. A compromised low-privilege account can do this without any department membership.
- **Smallest recommended fix:** add the built-in ASP.NET Core rate limiter (per-user policies for portal create, reply and search) and explicit request-body limits for the multipart endpoint. No new dependency.
- **Decision required:** No (limits themselves are values to agree). **Production blocker:** Yes (abuse controls are a listed project gate).
- **Related:** SRE-005, QA-004.

### SEC-004: Merging tickets of different requesters shares one conversation with both people
- **Specialist / category:** Security / Privacy. **Severity:** Medium. **Confidence:** Medium (code read; not run).
- **Evidence:** `TicketMerge.cs` checks department and permission only; it does not compare requesters. `MailBridge.AddMessageAsync` (`MailBridge.cs:215-282`) sends later email replies for a merged source's thread to the canonical ticket as public Requester messages with the sender as author. The portal shows all public messages of the canonical ticket to its requester, labelling other authors "Another participant" (`PortalRequestService.cs:224-230`). The merge dialog shows the candidate's requester name but gives no warning.
- **Impact / abuse scenario:** an agent merges Ana's ticket (source) into Ben's (canonical) as a "duplicate". Ana replies by email; her message and its text now appear in Ben's portal request, and Ben's replies and agent replies on the canonical thread go to Ben only. Neither requester expects to share a thread with a stranger. This is within one department and only after an agent action, but it discloses a requester's words to another requester.
- **Smallest recommended fix:** the options are product decisions (see below).
- **Decision required:** Yes: (a) allow it but warn the agent in the merge dialog when requesters differ, (b) block merge across different requesters, or (c) keep as is. **Production blocker:** No.
- **Related:** ITSM review (not yet run); SEC-006.

## 15.3 Low

### SEC-005: Automation execution history is not covered by the department scope filter
- **Category:** Authorization. **Confidence:** Medium (code read; DEMO has no rules, so not confirmed at runtime).
- **Evidence:** `AutomationExecution` has no `HasQueryFilter` (the scoped entities are Ticket, Message, Requester, TicketSla, SlaEvent, TicketFieldValue, TicketFollower, TicketLink, TicketParticipant, CannedResponse). `GET /api/departments/{id}/automations/history` requires `department.automation.manage` (administrative, so a Platform Admin passes) and returns `TicketReference`, rule names, outcomes and condition traces (`AutomationAdmin.cs:171-196`). Traces never hold the subject (it is null by design, `Conditions.cs:206`) but do hold `RequesterDomain`.
- **Impact / abuse scenario:** a Platform Admin without membership lists which tickets (by reference) a department's rules fired on and the requester domains. Metadata only, same root cause as SEC-001.
- **Smallest recommended fix:** return history only to callers who also hold `tickets.read` in that department, or add a department scope filter to the execution entity.
- **Decision required:** No. **Production blocker:** No. **Related:** SEC-001, SEC-012.

### SEC-006: A correlated inbound email is accepted from any sender into an existing ticket
- **Category:** Messaging / Security. **Confidence:** Low-Medium (needs the Messaging review to confirm what the correlation evidence can be).
- **Evidence:** headers and our correlation token decide the ticket (`InboundProcessor.cs:260-289`); `MailBridge.AddMessageAsync` then adds a public Requester message authored by whatever address sent it, appears to the ticket's requester as "Another participant", and reopens the ticket (D-15). There is no check that the sender is the requester or a participant.
- **Impact / abuse scenario:** a person who obtains a ticket's `Message-ID` or token (for example from a forwarded email) sends mail with the matching `In-Reply-To` to the department mailbox. Their text appears on the victim's ticket and reopens it.
- **Smallest recommended fix:** none until the product decision is made.
- **Decision required:** Yes (should replies from addresses that are not the requester or a participant be attached, held for review, or become a new ticket?). **Production blocker:** No.
- **Related:** SEC-004; Messaging review pending.

### SEC-007: Entra `email`/`preferred_username` is treated as a verified address
- **Category:** Authentication. **Confidence:** Medium.
- **Evidence:** `EntraClaimsReader.cs:26-29` takes `email`, else `preferred_username`; the requester identity code describes it as "verified" (`RequesterIdentityService.cs` class summary) and uses it to link an existing requester row that has no ticket history (`:43-61`) and to overwrite the linked requester's contact address and name on each portal use (`RefreshContactAsync`). Entra does not guarantee that these claims are verified or immutable by the tenant.
- **Impact / abuse scenario:** someone who can set their own mail attribute or UPN in the tenant (a guest, or an account with directory write access) sets it to a colleague's address. If the colleague has an unused requester row in a department, the attacker's portal use links it, and the colleague later gets "already linked to a different account" for that department. The reply address on that requester follows the attacker's claim. The precondition is tenant-level attribute control, so this is a trust assumption more than an open hole.
- **Smallest recommended fix:** state the assumption (tenant-controlled attributes) in the security notes, or additionally require the immutable account identifier (already stored) to match before any address-based link.
- **Decision required:** Yes (accept the tenant-trust assumption or tighten linking). **Production blocker:** No.

### SEC-008: Any Department Admin can search the whole organisation's directory
- **Category:** Privacy. **Confidence:** High.
- **Evidence:** `GET /api/users/lookup` allows any caller with `department.members.manage` in any department (`UserEndpoints.cs:39-55`) to search all active users by name or email (minimum length and a result cap apply, `PlatformUserService.cs:65-80`).
- **Impact:** a Department Admin can enumerate names and email addresses of staff who have no relationship to their department. Needed for adding members, but broader than required.
- **Smallest recommended fix:** none required now; consider returning email only on an exact or prefix match, or restricting to users with no member-only filter. **Decision required:** No. **Production blocker:** No.

### SEC-009: SignalR hub has no WebSocket origin allow-list
- **Category:** Security (defence in depth). **Confidence:** Low (relies on the framework default, not checked at runtime).
- **Evidence:** `services.AddSignalR()` only (`TicketingModule.cs:52`); no `UseWebSockets`/`AllowedOrigins`. The negotiate POST is CSRF-protected (the client sends the antiforgery header) and the session cookie is `SameSite=Lax`.
- **Impact / abuse scenario:** a page on a sibling origin of the same site could open a skip-negotiation WebSocket carrying the victim's cookie and receive content-free ticket events (ids, references, department ids) or set presence as the victim. Cross-site origins are blocked by `SameSite=Lax`.
- **Smallest recommended fix:** configure an explicit allowed-origin list. **Decision required:** No. **Production blocker:** No.

### SEC-010: Sanitiser keeps `data:` links in HTML mail and signatures
- **Category:** Security. **Confidence:** High (code), exploitation unlikely.
- **Evidence:** `MailText.cs:36-52` allows the `data` scheme and restricts only `img`; `a href="data:text/html,…"` is kept. Inbound HTML is shown in a sandboxed frame without scripts (safe in-app), but signature HTML is sent to external recipients.
- **Impact:** a crafted link survives in outbound mail or inbound display. In-app impact is negligible because of the sandbox and CSP. **Smallest fix:** allow `data:` only for `img` (`data:image/`). **Decision required:** No. **Production blocker:** No.

### SEC-011: Document responses carry no clickjacking or content-type headers
- **Category:** Deployment / Security. **Confidence:** High.
- **Evidence:** `Program.cs:98-114` sets `nosniff`, `Referrer-Policy` and a restrictive CSP only on `/api` responses; the host does not serve the SPA (SRE-001), so no `frame-ancestors`, CSP or `X-Frame-Options` exists for the application page.
- **Impact:** once deployed, the page can be framed by another site (clickjacking on destructive buttons) unless the serving layer adds headers. **Fix:** set CSP with `frame-ancestors 'none'`, `nosniff` and `Referrer-Policy` on the SPA when the production serving design (SRE-001) is made. **Decision required:** No. **Production blocker:** No (fold into SRE-001).

### SEC-012: Platform Admin's reach over non-content department data is implicit and undocumented
- **Category:** Authorization. **Confidence:** High.
- **Evidence:** by `PermissionKind.Administrative`, a Platform Admin passes every `department.*.manage` permission without membership. Beyond SEC-001/SEC-005 this includes retrying and re-sending failed mail with duplicate risk (`MailEndpoints.cs:59-64`), reading and editing Knowledge drafts, request-form audience user lists, member lists, and the department audit view (organisation events only).
- **Impact:** configuration data and operational actions, not ticket content, but the boundary is not written down and these actions are not gated by the "no implicit content access" rule.
- **Smallest recommended fix:** list explicitly (in `PERMISSIONS.md`) which administrative capabilities a Platform Admin keeps. **Decision required:** Yes (confirm the intended boundary, in particular mail retry and Knowledge draft access). **Production blocker:** No.

## 15.4 Polish

### SEC-013: Knowledge markdown accepts `/\host` as an in-app link
- **Category:** Visual Polish / Security hygiene. **Confidence:** Medium.
- **Evidence:** `markdown.tsx:9` accepts a link beginning with `/` unless the next character is `/`; `/\evil.example` passes and browsers treat it like `//evil.example`. The server's `ReturnUrl.Sanitize` rejects backslashes, the client does not.
- **Impact:** none beyond what an author can already do with an `https://` link, but it can make an external link look in-app (`target` is not set for it). **Fix:** reject `\` in `safeHref`. **Decision required:** No. **Production blocker:** No.

## 15.5 Cross-references to earlier findings

- **UX-009 (BulkBar hidden team/assignee):** server enforcement confirmed. `BulkOneAsync` re-checks `tickets.update`, assignment rights and `ValidateTeamAsync`/`ValidateAssigneeAsync` against each ticket's own department; a foreign team or ineligible assignee returns `invalid` for that ticket (same message for missing and foreign IDs, so no existence oracle). The UI issue stands as a clarity defect only.
- **UX-012:** UX only, the server authorises `tickets.reply` against the ticket's department.
- **SRE-010 / SEC-002:** `RequireCleanScan=false` accepted in Production strengthens SEC-002.
- **SRE-001 / SEC-011:** serving design must add document security headers.
- **QA-004 / SEC-003:** concurrency and abuse of the portal reply path.

## 15.6 Areas reviewed with no issue found

- **Department scope filter and 404 non-disclosure:** Tickets, messages, requesters, SLA, followers, links, participants and field values carry the global scope filter; resource endpoints resolve the ticket through it, then check permission against the stored department (`ResourceAuthorization.cs`). `IgnoreDepartmentScope()` is used only in portal, mail-bridge, automation and system paths, and the calls I read (portal, mail bridge, notifications, presence, realtime) filter by explicit department or owner id; the remaining automation and SLA calls were listed, not read in full.
- **Client-supplied IDs:** ticket create (`requesterId`, team, assignee, mailbox, type), bulk, macros (macro, canned, type, team, assignee all re-validated against the ticket's department), merge and links (cross-department answers 409 only after the ticket is visible), follower targets (must hold `tickets.read`), request forms and Help Topics (form must belong to the department), saved-view and queue-preference default views (owner only). Foreign and missing team/user IDs give the same error text.
- **Internal Notes:** excluded in the query for callers without `tickets.notes.read` (messages list, activity, search body match and snippets), never selected in the portal, notification and mention paths re-check note access at creation and at read, realtime note events go to a separate group that requires note access, presence downgrades "writing a note" to "viewing" without it. Every role that can read tickets also has note access today.
- **Portal:** requester ownership is by explicit link, never address; merged requests fail closed; attachments only for public messages; idempotency keys include the requester id; reply author labelling does not expose other requesters' names.
- **Request Form audiences and Help Topics:** one shared `PortalFormAccess` decision for catalogue, metadata and submission; topics exist only if their form is usable by the caller.
- **Knowledge:** Published slot only, audience and active category rules in one shared predicate; reply-link endpoint requires `tickets.reply`, a linked active account and the ticket's own department; maintenance endpoints are department-scoped and owner eligibility is validated; no draft or archived article reaches a requester.
- **New endpoints:** `queue-types` (route department plus `tickets.read`), `queue-preferences` (own row only, view ownership checked), `article-links` (above): no issues beyond SEC-001's general pattern, which they do not share.
- **SignalR:** group names are server-decided, the only client-callable method (`SetPresence`) is membership-checked, payloads carry ids and kinds only, requester events use a separate group and coarse kinds.
- **XSS:** React text rendering by default; markdown renderer builds React nodes (no raw HTML) with a scheme allow-list; email HTML is sanitised server-side and shown in a sandboxed, script-less frame with a restrictive CSP; signature and acknowledgement placeholders are HTML-encoded; stored signature HTML is sanitised before saving.
- **Authentication and session:** single-tenant issuer and `tid` pinning, PKCE, state and nonce, `oid` as the stable subject, per-request active-user validation, `HttpOnly` / `SameSite` cookies, antiforgery on every unsafe endpoint except the Graph webhooks, `returnUrl` sanitised, dev sign-in guarded by environment (SRE verified).
- **Webhooks:** per-subscription client-state secret stored hashed and compared in constant time, body size limited, handshake echo served as `text/plain` with `nosniff`.
- **Audit and logging:** platform audit is limited to organisation and identity categories; audit rows resist update/delete (asserted in the existing audit tests); ActionResult never stores note text; log templates use codes and ids, no addresses, subjects or bodies; no committed secrets found (`git grep`).
- **Attachments (delivery side):** attachment-only `Content-Disposition`, our own content-type table, filename sanitising, path stripping, composite department+ticket+id lookup, private object keys.
- **Hangfire dashboard:** read-only and limited to Platform Admins; jobs carry ids only.

## 15.7 Could not be verified

- Runtime behaviour of `article-links`, `queue-types` and `queue-preferences` (the dev API may predate commit `081f660`); these were judged from code.
- SEC-005 at runtime (the DEMO department has no automation rules).
- SEC-009: the default WebSocket origin behaviour of the framework was recalled, not exercised.
- Live Entra claim behaviour (SEC-007) and anything involving a real Graph tenant (D-16 stays open; Messaging review pending).
- Object-store access policy in a real deployment (only the dev compose file exists, SRE-001).
- Agent-side upload endpoints and the full set of attachment size/scan flows beyond the validator and download path.
- No fuzzing of guessed IDs across every endpoint was run; coverage came from reading the authorisation pattern and its use. The existing isolation test matrix was not run (and not reviewed beyond noting its coverage).
- Components for markdown/RichText beyond the files named above; exhaustive review of every admin screen's rendering.
