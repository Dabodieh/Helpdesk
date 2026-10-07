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
| Security / Privacy Engineer | **Did not complete** (rate-limited) | SEC |
| Data / Reporting Engineer | **Did not complete** (rate-limited) | DATA |

Completed specialists also returned their findings as text (the findings files in the scratchpad were not written, because subagents were refused file writes). All of their detail below is taken from those handbacks. Confidence is quoted where the specialist stated it; "not stated" means the handback did not give one. Every completed review was static and sampled, not exhaustive; their stated limits are in section 14.

**Areas that remain UNREVIEWED.** The absence of findings in these areas is not evidence that they are sound.

- **Security / Privacy (highest priority gap):** Department isolation on read and write paths, IDOR/BOLA, non-disclosure, Internal Note leakage paths, requester identity and audience enforcement, attachment security, XSS/sanitisation, audit completeness, logging privacy, and the new `article-links` and `queue-types` endpoints. Several UX/QA findings below ask for server-side confirmation (UX-009, UX-012, QA-004) that nobody has yet done.
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
