# Post-Refinement-10 team review (consolidated)

Baseline: commit `081f660` (Refinement 10). Mode: read-only specialist review, static reading of the repository and documents. Nothing in the application was changed.

How to read this report: sections 1 to 14 are the consolidated view (counts, blockers, decisions, remediation plan). Sections 15 to 21 are the specialist evidence behind them, with every finding, its evidence and its recommended fix. Finding IDs are stable and used everywhere (prefixes: SRE, UX, DOC, QA from the first four reviews; SEC, MSG, BE, ARC, ITSM, DATA from the later six). Nothing was removed from the evidence sections.

## 1. Executive summary

**Verdict.** The product is not ready for production, and the reasons are specific and fixable. No Critical finding was raised. Seven High findings were raised, and eleven distinct issues are production blockers (section 2). Most of the application is built carefully: the module structure, the concurrency and idempotency tools, the Department isolation on tickets, the Internal Note handling, the SLA arithmetic and the delivery-state honesty are strengths and should not be disturbed (section 13). The problems cluster in a few places.

**What stands in the way (plain terms).**
1. **Production hosting does not exist yet** (SRE-001, SRE-002, SRE-003, SRE-005, SRE-006): no deployment artefact, no persistent Data Protection keys, no backup or restore procedure, no metrics, and an unverified proxy-trust default.
2. **A Platform Admin can read mail subjects and sender addresses of any department** through the mail attention queue (SEC-001), which breaks a locked rule. Automation history has the same pattern (SEC-005).
3. **Anyone who holds a thread identifier can add themselves, or others, to a ticket's reply Cc list** (MSG-002, with SEC-006, BE-001 and MSG-003): a stranger's email can reopen a ticket, appear to the requester, and receive every later agent reply.
4. **Retrying a bounced reply sends nothing but marks it Sent** (MSG-001).
5. **Any Department Admin can register any mailbox**, so only unverified Exchange-side scoping stands between them and another person's mailbox (MSG-004), and the live Graph tenant evidence (D-16) is still missing.
6. **No abuse controls and a thin attachment policy with no scanner** (SEC-003, SEC-002).

**Where the effort should go.** The Architect's analysis shows most findings share a small number of causes (section 8). Four small, targeted fixes would close about fifteen findings: a ticket-party rule, one more use of the existing outbox pattern, a classification rule for administrative endpoints with one probe test, and a draft-id fix on retry with a guard on the delivery report.

**What this report is not.** It is a static review. No full test suite, no broad browser run, no load or query testing, no restore drill, no live Graph tenant call and no mutation of the DEMO data were performed (section 14). The only runtime evidence is a short set of read-only requests made by the Security review to confirm SEC-001. D-16 (live Graph validation) remains open and no finding here is tenant evidence.

### 1.1 Count reconciliation

All ten specialists are represented. The table gives the raw count of findings as raised by each specialist, then reconciles it to distinct issues so that nothing is counted twice.

| Specialist | Section | Critical | High | Medium | Low | Polish | Total |
|---|---|---|---|---|---|---|---|
| Documentation / Content (DOC) | 21 | 0 | 0 | 2 | 5 | 4 | 11 |
| Frontend / UX (UX) | 21 | 0 | 0 | 5 | 11 | 3 | 19 |
| SRE / Operations (SRE) | 21 | 0 | 4 | 7 | 3 | 0 | 14 |
| QA / Reliability (QA) | 21 | 0 | 0 | 4 | 3 | 1 | 8 |
| Security / Privacy (SEC) | 15 | 0 | 1 | 3 | 8 | 1 | 13 |
| Messaging / Graph (MSG) | 16 | 0 | 2 | 4 | 5 | 1 | 12 |
| Backend / Domain (BE) | 17 | 0 | 0 | 8 | 7 | 1 | 16 |
| Lead Architect (ARC) | 18 | 0 | 0 | 4 | 5 | 0 | 9 |
| ITSM / Service Desk (ITSM) | 19 | 0 | 0 | 3 | 4 | 0 | 7 |
| Data / Reporting (DATA) | 20 | 0 | 0 | 3 | 6 | 0 | 9 |
| **Raw findings** | | **0** | **7** | **43** | **57** | **11** | **118** |

| Step | Effect | Critical | High | Medium | Low | Polish | Total |
|---|---|---|---|---|---|---|---|
| Raw specialist findings | as above | 0 | 7 | 43 | 57 | 11 | 118 |
| Remove architectural root-cause findings that consolidate earlier defects (ARC-001 to ARC-004) | they explain existing findings and add no defect | 0 | 0 | -4 | 0 | 0 | -4 |
| Remove duplicate halves of the same issue (SEC-006 is MSG-002; SEC-004 is BE-002) | same underlying issue reported twice; the higher-severity or fuller entry is kept | 0 | 0 | -1 | -1 | 0 | -2 |
| **Distinct issues** | | **0** | **7** | **38** | **56** | **11** | **112** |

Not counted in either table: four enhancements (ITSM section: E-1 to E-4, not defects) and the standing gates (section 10).

*Related clusters that are kept as separate entries, because each needs its own fix even though they share a cause:* SEC-001 / SEC-005 / SEC-012 (same pattern on three endpoints or decisions); MSG-002 / MSG-003 / BE-001 (the party rule, three symptoms); MSG-001 / BE-009 / MSG-007 (delivery-state correctness); BE-004 / BE-005 / BE-015 (after-commit durability); SEC-002 / SRE-010 (attachment safety); SRE-008 / DOC-002 (the missing web/worker role split and the document that claims it); UX-005 / DOC-008 (mailbox deactivation warning and merge-dialog copy; DOC-008 keeps the rest); QA-004 / BE-003 / BE-010 / UX-016 (replay and staleness); SRE-011 / MSG-011 / DATA-003 (retention and audit); BE-007 / DATA-001 (first-response clock and sidebar counts); BE-006 / DATA-002 (SLA selection and the first-response figure). Where a duplicate is genuinely ambiguous the IDs are kept and the overlap is stated in the entries.

| Item | Count |
|---|---|
| Raw findings | 118 |
| Distinct issues after removing root-cause consolidations and duplicates | 112 |
| Unique production blockers | 11 (7 High, 4 Medium) |
| Standing gates kept separate (D-16, D-09, and evidence not yet produced) | see section 10 |
| Unique decisions requiring the owner | 30 (from 37 raw "decision required" flags) |
| Remediation workstreams | 16 in 5 priority bands |
| Enhancements noted but not counted | 4 |

## 2. Production blockers

One deduplicated register of finding-level blockers. D-16 is a separate standing gate (section 10) and is not in this list.

| # | Finding ID(s) | Severity | Problem | Related / root cause | Decision | Stage |
|---|---|---|---|---|---|---|
| B-1 | SEC-001 | High | A Platform Admin without a membership reads the sender addresses and subjects of failed, uncertain and ignored mail of any department (confirmed at runtime) | SEC-005, SEC-012, ARC-002 (RC-4) | No (fix); D14 for the wider boundary | P0, WS-1 |
| B-2 | MSG-002 (same issue as SEC-006) | High | Correlated mail is attached from any sender; the sender and its Cc join the ticket and receive every later reply; the ticket reopens | MSG-003, BE-001, MSG-012, ARC-003 (RC-2) | Yes, D8 | P0, WS-2 |
| B-3 | MSG-001 | High | Retrying a reply after a bounce sends nothing and marks it Sent | BE-009, MSG-007, ARC-001 (RC-3) | No | P0, WS-3 |
| B-4 | MSG-004 | Medium | Any Department Admin can register an arbitrary mailbox; only Exchange-side scoping (unverified, D-16 E-2) prevents reading or sending as another person | D-16 | Yes, D10 | P0, WS-4 |
| B-5 | SRE-001 | High | No production deployment artefact; nothing serves the SPA; no proxy or WebSocket guidance | SEC-011 | Yes, D1 | P1, WS-5 |
| B-6 | SRE-006 | Medium | Proxy trust ships empty (unverified: needs one confirmation) | SEC-009 context | No (verify first) | P1, WS-5 |
| B-7 | SRE-002 | High | Data Protection keys not persisted (D-13): every container replacement drops sessions and antiforgery tokens | D-13 | Yes, D2 | P1, WS-6 |
| B-8 | SRE-003 | High | No backup or restore procedure, script or evidence | ARC-007 | Yes, D3 | P1, WS-6 |
| B-9 | SRE-005 | High | No metrics; stuck mail sync, outbox rows, failed jobs and SLA lag are unobservable; poison outbox rows retry forever | BE-005, SRE-004 | No | P1, WS-7 |
| B-10 | SEC-003 | Medium | No rate limiting or abuse controls; uploads buffered in memory (a listed project gate) | MSG-005, BE-001 | No (values to agree) | P1, WS-8 |
| B-11 | SEC-002 | Medium | Attachment policy is a short blocklist with no scanner (D-08); production accepts the scan requirement switched off | SRE-010 | Yes, D13 | P1, WS-8 |

Eleven unique blockers: seven High, four Medium. B-1 to B-4 are application correctness and security (P0); B-5 to B-11 are production readiness (P1).


## 3. Critical findings

None raised by any of the ten specialists.

## 4. High findings

Seven findings, all on the production-blocker register in section 2. Detail and evidence: sections 15 to 21.

| ID | Finding | Specialist | Workstream | Blocker | Decision |
|---|---|---|---|---|---|
| SRE-001 | No production deployment artefact | SRE | WS-5 | B-5 | D1 |
| SRE-002 | Data Protection keys not persisted (D-13) | SRE | WS-6 | B-7 | D2 |
| SRE-003 | No backup or restore procedure, script or evidence | SRE | WS-6 | B-8 | D3 |
| SRE-005 | No metrics; stuck work is not observable | SRE | WS-7 | B-9 |  |
| SEC-001 | Platform Admin reads sender addresses and subjects of any department's problem mail | Security | WS-1 | B-1 |  |
| MSG-001 | Retry after a confirmed bounce does not send anything and reports the message as Sent | Messaging | WS-3 | B-3 |  |
| MSG-002 | Correlated mail is accepted from any sender and can add recipients to the ticket (SEC-006) | Messaging | WS-2 | B-2 | D8 |

(7 findings.)

## 5. Medium findings

Forty-three raw findings. ARC-001 to ARC-004 are architectural root-cause findings that consolidate earlier defects, and SEC-004 duplicates BE-002; see section 1.1. Detail and evidence: sections 15 to 21.

| ID | Finding | Specialist | Workstream | Blocker | Decision |
|---|---|---|---|---|---|
| SRE-004 | Readiness check covers PostgreSQL only | SRE | WS-7 |  | D4 |
| SRE-006 | Proxy trust configuration ships empty | SRE | WS-5 | B-6 |  |
| SRE-007 | Per-module migrations with no cross-module atomicity or rollback policy | SRE | WS-6 |  | D5 |
| SRE-008 | Single-host assumptions (presence in memory; ROLE not implemented) | SRE | WS-6 |  | D6 |
| SRE-009 | Mail sync and dispatch share a small worker pool with a fixed lock and no retry | SRE | WS-7 |  |  |
| SRE-010 | Production accepts unsafe storage/scan settings | SRE | WS-5 |  |  |
| SRE-011 | No pruning or sizing for growing tables; console-only logs | SRE | WS-7 |  | D7 |
| SEC-002 | Attachment policy is a short extension blocklist with no scanner behind it (D-08) | Security | WS-8 | B-11 | D13 |
| SEC-003 | No rate limiting or abuse controls on authenticated endpoints; large uploads are buffered in memory | Security | WS-8 | B-10 |  |
| SEC-004 | Merging tickets of different requesters shares one conversation with both people | Security | WS-2 |  | D9 |
| MSG-003 | conversationId is accepted as sole evidence although it behaves like a subject match | Messaging | WS-2 |  | D8 |
| MSG-004 | Any Department Admin can register an arbitrary mailbox; only Exchange-side scoping stops them | Messaging | WS-4 | B-4 | D10 |
| MSG-005 | Acknowledgements go to an unauthenticated sender and carry attacker-chosen text | Messaging | WS-8 |  | D11 |
| MSG-006 | Acknowledgements are not marked as automatic | Messaging | WS-3 |  |  |
| BE-001 | Ticket participants have no limit and no way to be removed | Backend | WS-2 |  | D8 |
| BE-002 | Merging tickets of different requesters orphans the source requester (backend side of SEC-004) | Backend | WS-2 |  | D9 |
| BE-003 | Posting a public message has no idempotency key; the portal reply is not atomic with its attachments | Backend | WS-10 |  |  |
| BE-004 | Automation and SLA-triggered work after commit is not durable | Backend | WS-9 |  | D16 |
| BE-005 | One failing notification outbox item can block the whole queue; no attempt count or error is kept | Backend | WS-9 |  |  |
| BE-006 | The SLA policy is chosen once at creation, before automation routes or classifies the ticket | Backend | WS-12 |  | D17 |
| BE-007 | The first-response clock keeps running after a ticket is resolved or closed without a public reply | Backend | WS-12 |  | D18 |
| BE-008 | Sorting the queue by SLA hides every ticket that has no deadline | Backend | WS-12 |  |  |
| ARC-001 | The mail send outcome is encoded as strings across three layers | Architect | WS-3 |  |  |
| ARC-002 | Isolation outside the Ticketing scope filter is by convention, and content can ride on administrative endpoints | Architect | WS-1 |  |  |
| ARC-003 | "Who is a party to a ticket" has no single owner | Architect | WS-2 |  |  |
| ARC-004 | Durable intent is applied to some after-commit work and not to the rest | Architect | WS-9 |  |  |
| ITSM-001 | After an agent answers, the ticket looks untouched | ITSM | WS-13 |  | D20 |
| ITSM-002 | Agents cannot hand a ticket to another team or colleague, and macros that do are invisible to them | ITSM | WS-13 |  | D21 |
| ITSM-003 | A ticket without a stored mailbox can never be answered by email, and nothing repairs it | ITSM | WS-13 |  | D22 |
| DATA-001 | The sidebar queue counts do not use the same definitions as the queue list or the dashboard | Data | WS-14 |  | D25 |
| DATA-002 | "Awaiting first response" is silently an SLA figure | Data | WS-14 |  | D26 |
| DATA-003 | The audit stream is both the source of truth for history and the place retention would have to change | Data | WS-14 |  | D7 |
| UX-001 | Workspace shortcuts stay live behind open dialogs | Frontend/UX | WS-15 |  |  |
| UX-002 | Notification deep link to #msg- is unreliable | Frontend/UX | WS-15 |  |  |
| UX-003 | Article Publish leaves a stale version after a partial failure | Frontend/UX | WS-10 |  |  |
| UX-004 | Row action buttons in admin tables share identical accessible names | Frontend/UX | WS-15 |  |  |
| UX-005 | Mailbox Deactivate is one click with no consequence warning | Frontend/UX | WS-15 |  | D28 |
| QA-001 | Realtime client does not reconcile after a long outage | QA | WS-10 |  |  |
| QA-002 | Stuck-Sending outbound recovery is untested | QA | WS-11 |  |  |
| QA-003 | Portal test helper auto-links requesters by raw SQL | QA | WS-11 |  |  |
| QA-004 | Concurrency and duplicate coverage stops at ticket and organisation writes | QA | WS-10 |  | D19 |
| DOC-001 | API/email docs contradict the locked "no fallback sender" rule | Documentation | WS-16 |  |  |
| DOC-002 | Docs claim features that do not exist | Documentation | WS-16 |  |  |

(43 findings.)

## 6. Low findings

Fifty-seven raw findings (SEC-006 duplicates MSG-002). Detail and evidence: sections 15 to 21.

| ID | Finding | Specialist | Workstream | Blocker | Decision |
|---|---|---|---|---|---|
| SRE-012 | Log privacy is convention only | SRE | WS-7 |  |  |
| SRE-013 | No runbook for dependency-down cases | SRE | WS-7 |  |  |
| SRE-014 | Floating image tags, :latest SeaweedFS, AllowedHosts=* | SRE | WS-5 |  |  |
| SEC-005 | Automation execution history is not covered by the department scope filter | Security | WS-1 |  |  |
| SEC-006 | A correlated inbound email is accepted from any sender into an existing ticket | Security | WS-2 |  | D8 |
| SEC-007 | Entra email/preferred_username is treated as a verified address | Security | WS-13 |  | D15 |
| SEC-008 | Any Department Admin can search the whole organisation's directory | Security | WS-13 |  |  |
| SEC-009 | SignalR hub has no WebSocket origin allow-list | Security | WS-5 |  |  |
| SEC-010 | Sanitiser keeps data: links in HTML mail and signatures | Security | WS-5 |  |  |
| SEC-011 | Document responses carry no clickjacking or content-type headers | Security | WS-5 |  |  |
| SEC-012 | Platform Admin's reach over non-content department data is implicit and undocumented | Security | WS-1 |  | D14 |
| MSG-007 | Any sender can pose as a bounce and mark a sent reply as failed | Messaging | WS-3 |  |  |
| MSG-008 | Uncertain sends are never reconciled automatically | Messaging | WS-3 |  |  |
| MSG-009 | Ignored inbound mail cannot be released to a ticket | Messaging | WS-13 |  | D12 |
| MSG-010 | A re-delivered message with the same Message-ID after 2 seconds becomes a second ticket | Messaging | WS-3 |  |  |
| MSG-011 | Full bodies of all inbound mail, including ignored and bounce mail, are kept with no purge | Messaging | WS-14 |  |  |
| BE-009 | The delivery-state report has no transition rules | Backend | WS-3 |  |  |
| BE-010 | Bulk edits apply to whatever state a ticket is in now | Backend | WS-10 |  | D19 |
| BE-011 | Invalid references answer with 400 in some places and 409 in others | Backend | WS-16 |  |  |
| BE-012 | Requester identity resolution is capped at 100 cases with no paging or search | Backend | WS-13 |  |  |
| BE-013 | Saving queue preferences for the first time twice at once returns a server error | Backend | WS-10 |  |  |
| BE-014 | Lifecycle timestamps are overwritten, so the first resolution time is not kept | Backend | WS-12 |  |  |
| BE-015 | A queued notification is classified from the ticket's state at drain time, not at event time | Backend | WS-9 |  |  |
| ARC-005 | "Open work" is restated in four other files besides the shared predicate | Architect | WS-14 |  |  |
| ARC-006 | Significant decisions since ADR-015 are recorded in long bullets, not ADRs | Architect | WS-16 |  |  |
| ARC-007 | Cross-schema database coupling and migration order are accepted in practice but not written down or tested | Architect | WS-6 |  |  |
| ARC-008 | A cross-module contract carries a security precondition only in prose | Architect | WS-16 |  |  |
| ARC-009 | Maintainability: one very large service and an unchecked client contract | Architect | WS-16 |  |  |
| ITSM-004 | The portal status "Waiting" does not say whose turn it is | ITSM | WS-13 |  | D23 |
| ITSM-005 | Resolving or closing without a public message tells the requester nothing, and nothing ever closes a resolved ticket | ITSM | WS-13 |  | D24 |
| ITSM-006 | The department setup overview can say "1 active form" when no requester can use it | ITSM | WS-13 |  |  |
| ITSM-007 | New email tickets are unrouted and only an administrator-built rule routes them | ITSM | WS-13 |  |  |
| DATA-004 | The three ticket-creation audit events have different shapes | Data | WS-14 |  |  |
| DATA-005 | A merged ticket keeps its status and is easy to count by mistake | Data | WS-14 |  | D27 |
| DATA-006 | Lifecycle columns hold the current value only | Data | WS-14 |  |  |
| DATA-007 | Two ideas of "about to breach", and an age that mixes waiting on us with waiting on the requester | Data | WS-12 |  |  |
| DATA-008 | Some facts are not captured at all and cannot be rebuilt later | Data | WS-14 |  |  |
| DATA-009 | One person can be several requesters | Data | WS-14 |  |  |
| UX-006 | Reorder arrows lose focus; one reorder dialog can race | Frontend/UX | WS-15 |  |  |
| UX-007 | Focus not restored after choosing from the ticket More menu | Frontend/UX | WS-15 |  |  |
| UX-008 | Esc or Close silently discards typed work | Frontend/UX | WS-15 |  |  |
| UX-009 | BulkBar sends a hidden Team/Assignee choice after the selection spans departments | Frontend/UX | WS-15 |  |  |
| UX-010 | Portal "My requests" tabs use ARIA tab roles without the keyboard model | Frontend/UX | WS-15 |  |  |
| UX-011 | Saved-view Delete is immediate; Archive saves the draft without saying so | Frontend/UX | WS-15 |  |  |
| UX-012 | Retry sending checks permission against any department | Frontend/UX | WS-15 |  |  |
| UX-013 | Mention picker ARIA does not match behaviour | Frontend/UX | WS-15 |  |  |
| UX-014 | Portal file picker silently truncates to 5; no feedback after reply or submit | Frontend/UX | WS-15 |  |  |
| UX-015 | Mailbox edit shows a stale name; 8-column table has no overflow handling | Frontend/UX | WS-15 |  |  |
| UX-016 | Portal state persists across route params | Frontend/UX | WS-10 |  |  |
| QA-005 | Negative realtime leak tests rely on fixed sleeps | QA | WS-11 |  |  |
| QA-006 | Recurring-job registration is never verified | QA | WS-11 |  |  |
| QA-007 | Structural guard coverage is Ticketing only | QA | WS-1 |  | D30 |
| DOC-003 | Stale phase and module statements | Documentation | WS-16 |  |  |
| DOC-004 | Docs say "full suite at milestones", contradicting CLAUDE.md | Documentation | WS-16 |  |  |
| DOC-005 | Demo tooling and operations runbook missing from docs | Documentation | WS-16 |  |  |
| DOC-006 | Automation history shows raw codes | Documentation | WS-16 |  |  |
| DOC-008 | Weak confirmation copy | Documentation | WS-16 |  |  |

(57 findings.)

## 7. Polish / copy / visual findings

Eleven findings, kept deliberately rather than discarded. Detail and evidence: sections 15 to 21.

| ID | Finding | Specialist | Workstream | Blocker | Decision |
|---|---|---|---|---|---|
| SEC-013 | Knowledge markdown accepts /\host as an in-app link | Security | WS-16 |  |  |
| MSG-012 | Every reply shows an opaque token to the requester | Messaging | WS-2 |  |  |
| BE-016 | A validation message lists the wrong allowed sources | Backend | WS-16 |  |  |
| UX-017 | Dashboard subtitle exposes roadmap wording | Frontend/UX | WS-15 |  |  |
| UX-018 | Dead realtime foreign-change code | Frontend/UX | WS-15 |  | D29 |
| UX-019 | Small visual items (breadcrumb, unnamed headers, debounce, live regions, panel position, loading state, spacing typo) | Frontend/UX | WS-15 |  |  |
| QA-008 | Merged-source bulk edit untested; fixed wait in an end-to-end test | QA | WS-11 |  |  |
| DOC-007 | Roles shown as DepartmentAdmin / TeamLead in Members | Documentation | WS-16 |  |  |
| DOC-009 | Server 400 messages repeat the field name | Documentation | WS-16 |  |  |
| DOC-010 | Mailbox-assignment question missing from the open-decisions list; roadmap wording stale | Documentation | WS-16 |  | D22 |
| DOC-011 | Mixed "authorization" / "authorised" spelling in documents | Documentation | WS-16 |  |  |

(11 findings.)

## 8. Cross-cutting root causes

From the Lead Architect's analysis (section 18.2), checked against the other specialists. A fix at the root closes several findings at once.

| Root cause | Findings it explains | Systemic or local | One fix that closes several |
|---|---|---|---|
| **RC-1. After-commit work is durable in some places and best-effort in others.** Durable: notification outbox, mail outbox, delivery re-report. Not durable: automation dispatch, SLA breach follow-up. | BE-004, BE-005, BE-015, ARC-004, SRE-005 | Systemic for automation and SLA follow-up; the rest is local | Reuse the existing outbox pattern for automation and SLA follow-up (a small intent row in the same transaction, drained by the existing job, deterministic keys kept); add attempt count and last error to the outbox row. Not a general event framework. |
| **RC-2. Nothing defines who is a party to a ticket.** Requester, participants (a bag of addresses without provenance), message authors and merged requesters are decided separately by mail, portal, merge and outbound Cc. | MSG-002, SEC-006, MSG-003, BE-001, SEC-004, BE-002, ARC-003, MSG-012 | Systemic | One small rule in one place: a correlated message attaches only from the requester or an existing participant; participants record who added them, are capped and removable; a merge across requesters has an explicit rule. |
| **RC-3. The send outcome crosses the Mail/Ticketing boundary as a status plus a magic string.** | MSG-001, BE-009, MSG-007, ARC-001, QA-002 | Partly systemic; the MSG-001 defect itself is local | Local first: a retry of an already-submitted message clears the draft id and is a recorded re-send. Then explicit uncertain and bounced states so string checks disappear. |
| **RC-4. Isolation outside the scope-filtered ticket set is by convention, and administrative permissions pass for a Platform Admin without membership.** | SEC-001, SEC-005, SEC-012, ARC-002, QA-007 | Systemic but small in extent (two endpoints today) | One stated rule (content-derived fields need ticket read by membership; administrative permission returns configuration only), the two endpoint fixes, and one probe test over every administrative GET as a non-member Platform Admin. |
| **RC-5. Lifecycle rules decided once and never re-evaluated.** SLA policy chosen at creation before routing; first-response clock independent of status; lifecycle timestamps overwritten. | BE-006, BE-007, BE-014, DATA-006, DATA-007 | Local to the SLA and lifecycle code; needs three product decisions | One decision session (D17, D18), then a small change in one module. |
| **RC-6. Client-initiated writes lack replay and staleness protection while the core is strict.** | BE-003, BE-010, QA-004, UX-016, UX-003, BE-013 | Local, repeated | Optional client message id used as the unique source reference (the mechanism exists for portal creation); optional per-row versions for bulk. |
| **RC-7. Production hosting is not yet a deliverable.** | SRE-001 to SRE-014, SEC-009, SEC-010, SEC-011, ARC-007 | Systemic, outside the application code | The planned deployment work (section 12). |
| **RC-8. Decisions are recorded in dense bullets, not ADRs; documents lag decisions.** | DOC-001 to DOC-011, ARC-006, ARC-008, ARC-009 | Systemic in process, cheap to fix | The missing ADRs and a documentation refresh. |
| **RC-9. The headline figures were each defined separately.** Open work is restated in several places; the sidebar, list and dashboard disagree; "awaiting first response" is an SLA figure. | DATA-001, DATA-002, DATA-005, ARC-005 | Systemic, easy to fix once decided | One shared definition of "counted ticket", "open work" and "awaiting first response" used by the sidebar, list, dashboard and future reports. |

Blockers outside hosting: B-2 traces to RC-2, B-3 to RC-3, B-1 to RC-4. B-4 (MSG-004), B-10 (SEC-003) and B-11 (SEC-002) have no architectural cause: a missing verification step and decision, the abuse-controls gate and the attachment-scanning gate.

## 9. Test gaps

Targeted tests only; none of these asks for a broad suite. Sources in brackets.

1. Realtime reconcile after reconnect and after the manual restart path, including the portal query keys (QA-001).
2. Stuck-`Sending` recovery for both branches, with the real `submit_started` flag; and send, bounce, retry, assert a second provider send (QA-002, MSG-001).
3. Negative control that an agent-created ticket is not portal-visible until a real link exists (QA-003).
4. Concurrency and replay: mailbox sync lock, outbound claim, portal and agent reply double-submit, bulk against a concurrent edit (QA-004, BE-003, BE-010; two need decisions D19 and none for BE-003).
5. Sentinel-based negative realtime assertions instead of fixed sleeps (QA-005).
6. Recurring job registration (QA-006) and bulk edit of a merged source (QA-008).
7. Structural guards for Knowledge, Mail and Attachments queries, if wanted (QA-007, D30).
8. A probe test: sign in as a non-member Platform Admin and call every GET under an administrative permission, asserting that no content-classified field appears (ARC-002, SEC-001, SEC-005).
9. The sender rule for correlated mail, participant cap and removal (MSG-002, BE-001); the outcome of a cross-requester merge for both requesters (BE-002).
10. Sidebar counts against the lists they open (DATA-001).
11. Frontend: shortcut suppression behind dialogs (UX-001), BulkBar clearing hidden choices (UX-009), Knowledge editor retry after a partial failure (UX-003).
12. Durable automation after a restart between commit and dispatch, and an outbox item that fails persistently (BE-004, BE-005), once the decisions are made.
13. Test-quality items: the portal helper that links requesters by raw SQL hides an unreachable state (QA-003); negative realtime tests rely on fixed sleeps (QA-005).

What the existing tests do well (do not disturb): they use the real stack (dev-login, real migrations, a database per fixture, real CSRF); isolation, revocation and Internal Note realtime paths are well covered; the architecture tests pin the anonymous and authenticated-only endpoint lists, the CSRF exemptions, every scope-bypass call and the module dependency graph; mail covers uncertain and duplicate cases.

## 10. Production-readiness gates

Standing gates, separate from the finding-level blockers in section 2.

| Gate | Status | Notes |
|---|---|---|
| **D-16 live Graph tenant validation** | **OPEN.** Runbook unrun; the E-1 to E-38 result table is empty; the Refinement 3 attempt was recorded as blocked (no approved tenant). | All Graph and Exchange behaviour is code and simulator evidence only. Nothing in this review is tenant evidence. Experiments that the findings depend on are tabled in section 16.7. |
| **D-09 retention and data lifecycle policy** | OPEN (policy). | Retention, export, anonymisation and legal hold are undecided. Pruning (SRE-011), raw mail retention (MSG-011) and audit-stream separation (DATA-003) wait on it. |
| **D-08 malware scanning engine** | OPEN (engine choice). | Scanning is required before production releases that accept attachments. SEC-002 and SRE-010 depend on it. |
| **D-13 persistent Data Protection keys** | OPEN. | Same item as blocker B-7 (SRE-002). |
| **Backup and restore evidence** | OPEN. | Same item as blocker B-8 (SRE-003). A backup without a restore drill must not be treated as recoverability. |
| **Abuse controls** | OPEN. | Same item as blocker B-10 (SEC-003). |
| **Evidence not yet produced** | NOT RUN | A full regression run, a broad browser pass, load or query testing at scale, and a restore drill have not been performed (by instruction). They are the evidence that would be needed to claim readiness; each needs explicit permission before it is run. |

Not a gate but a precondition: ITSM-003 (tickets without a stored mailbox) should be settled before the portal is opened to requesters.

## 11. Decisions required from you

Thirty distinct questions. Raw "decision required" flags across the specialist entries were 37; several specialists asked the same question. "Blocks" says whether the related remediation cannot be finished until you decide; most fixes that do not depend on a decision can proceed independently. "Default" is given only where the review clearly supports one.

| # | Related IDs | Question | Why it matters | Options the review supports | Default (if clearly supported) | Blocks |
|---|---|---|---|---|---|---|
| D1 | SRE-001, SEC-011 | How is the web app served in production, and which reverse proxy terminates TLS and WebSockets? | There is no production artefact; security headers for the app page belong to this layer. | Host serves the built app; or a separate static web server behind the same proxy. | None | WS-5 |
| D2 | SRE-002 (D-13) | Where do the Data Protection keys live? | Without durable keys every container replacement signs everyone out. | PostgreSQL; or a persistent volume (the open decision lists both). | None | WS-6 |
| D3 | SRE-003 | What are the backup targets, recovery point and time objectives, and who performs the restore drill? | Database and object store must be restored consistently; none of it is documented. | Set RPO/RTO and a drill owner. | None | WS-6 |
| D4 | SRE-004 | What makes an instance "not ready"? | Readiness currently checks only the database. | Add object store, job processing and/or migration state to readiness. | None | No |
| D5 | SRE-007, ARC-007 | What is the migration order and rollback policy across the eight modules? | Modules share one database with hard cross-schema keys; per-module rollback is impossible while later modules exist. | Document one database, one role, the order, and rollback by restore. | Architect: this is acceptable for a modular monolith if written down. | WS-6 (documentation) |
| D6 | SRE-008 | Is a single instance the supported model, or is the web/worker role split to be built? | Presence and the connection registry are in memory; the role mode in the docs is not implemented. | State single-instance as supported; or schedule the split. | None | No |
| D7 | DATA-003, MSG-011, SRE-011 (D-09) | Retention periods; which facts are kept for reporting separately from personal data; pruning for outbox, notifications, mail, attachments and logs; the log sink. | The append-only audit stream is the source of truth for history and also holds personal data; raw mail bodies are kept indefinitely. | A content-free lifecycle record kept separately from personal data (a view or a small table written by the same audit write); pruning rules once D-09 is settled. | None | WS-14, WS-7 (pruning) |
| D8 | MSG-002, SEC-006, MSG-003, BE-001 | What happens to mail that carries a valid thread identifier from a sender who is neither the requester nor a participant? Keep, restrict or remove the conversation-id evidence? Cap on participants, and an agent action to remove one? | A single email can currently join a ticket, reopen it and capture later replies. | Mail from other senders: (a) becomes a new ticket with a note naming the suggested ticket; (b) is held for review without showing or reopening; (c) attaches without participants and without reopening. Conversation id: keep with a participant sender, or remove. | Review minimum: never create participants from a non-participant's message. | WS-2 (B-2) |
| D9 | SEC-004, BE-002 | What happens when tickets of different requesters are merged? | The source requester's request vanishes from their portal and they stop receiving replies; the other requester sees their later mail. | Block; warn; or allow and add the source requester as a participant (and show them the canonical request). | ITSM: allow, and add the source requester as a participant, because merging different people's reports is common and blocking would hurt. | WS-2 |
| D10 | MSG-004 | Who may register a mailbox, and should creation probe the mailbox through Graph? | Without Exchange-side scoping a Department Admin could read or send as any mailbox. | Platform Admin only; approved-address list; creation-time probe refusing 403/404. | None | WS-4 (B-4) |
| D11 | MSG-005 | Which control stops acknowledgements being sent to unauthenticated or forged senders? | Rotating sender addresses bypass the per-sender limit; attacker text can appear in the subject. | A per-mailbox acknowledgement cap; acknowledge only senders that pass SPF/DKIM/DMARC; drop the subject placeholder from the default template. | None | WS-8 |
| D12 | MSG-009 | Should an operator be able to create a ticket from ignored inbound mail, and for which reasons? | Legitimate mail ignored by a rule or the rate limit can never become a ticket. | An audited "create ticket from this message" action for chosen ignore reasons. | None | No |
| D13 | SEC-002 | Which further attachment types are blocked, and may legitimate macro-enabled files be allowed after scanning? | The blocklist is the only control until a scanner exists. | Extend the list (macro-enabled Office, disk images, HTML/SVG, shortcuts, and similar); fail startup in production if the scan requirement is off. | None | WS-8 (B-11) |
| D14 | SEC-012 | Which administrative capabilities does a Platform Admin keep without membership (mail retry and resend, Knowledge drafts, form audience lists, member lists, department audit)? | The boundary is implicit; two of its side doors leaked content. | Write the list into the permissions document. | None | WS-1 (documentation part only) |
| D15 | SEC-007 | Is the tenant-controlled mail attribute trusted as the requester's verified address? | A user who can set that attribute can pre-claim someone's unused requester row. | Accept and document the trust assumption; or require an immutable-identity match before an address-based link. | None | No |
| D16 | BE-004, ARC-004 | Is best-effort automation after commit acceptable, or must it be durable? | A restart between commit and dispatch silently skips routing and escalation rules. | Accept (current behaviour); or store an intent row in the same transaction and drain it with the existing job. | Architect: reuse the existing outbox pattern. | WS-9 |
| D17 | BE-006 | When is the SLA policy chosen? | Team, type and priority conditions rarely match email tickets that are routed afterwards. | Document and hide unusable conditions; re-evaluate on team, type or priority change (original start kept); or run automation before the SLA is applied. | ITSM direction: re-evaluate on change with the original start time kept. | WS-12 |
| D18 | BE-007 | Does resolving or closing a ticket without a public reply stop the first-response clock, and how does it count? | Every first-contact resolution and every spam or duplicate otherwise records a first-response breach afterwards. | Stop and count as not applicable; stop and count as met; require a reply (today). | ITSM: stop and not applicable, the usual convention. | WS-12, WS-14 |
| D19 | BE-010, QA-004 | Is bulk edit last-write-wins by design, or should it check each row's version? | A stale selection can overwrite a decision made in between. | Keep (atomic and audited per ticket); or send per-row versions and report "changed since you selected it". | None | No |
| D20 | ITSM-001 | How should an agent and a lead tell "needs us" from "answered" after the first reply? | After an agent replies the ticket stays Open and possibly Unassigned. | A per-department default status after a public reply; an agent-reply automation trigger; a last-speaker marker in the queue. No new status. | None | WS-13 |
| D21 | ITSM-002 | May Agents change a ticket's team (routing), or should unavailable macros show as disabled with a reason? | Escalation to a specialist team needs a team lead every time. | Allow team change for Agents (assignee changes stay with leads); or show disabled macros with the reason. | None | WS-13 |
| D22 | ITSM-003, DOC-010 | How are tickets without a stored mailbox handled? Also: register the question in the open-decisions list. | They can never be answered by email and nothing repairs them; connecting a mailbox later does not help. | Refuse portal submissions until the department has a usable default mailbox; or design the explicit "assign a mailbox to this ticket" action. | None | WS-13; settle before the portal opens |
| D23 | ITSM-004 | Does the requester-facing "Waiting" always mean "waiting for you"? | Pending also covers waiting for a third party; the requester cannot tell whose turn it is. | Rename for the requester, or add one line of explanation. | None | No |
| D24 | ITSM-005 | Should resolving without a public reply prompt the agent, and is silent resolution acceptable? | The requester is told nothing, and nothing auto-closes (timers are in the backlog). | Prompt with the option to send a reply now. | None | No |
| D25 | DATA-001 | Do Pending tickets belong in the sidebar counts? | The sidebar counts Open only; the list and dashboard use Open and Pending. (The SLA badge counting resolved and closed tickets is a plain bug, no decision.) | Open only (then say so everywhere); or Open and Pending. | None | WS-14 |
| D26 | DATA-002 | What does "awaiting first response" mean? | It is currently an SLA figure and reads zero when no policy applies. | Define from the message stream (no public agent message yet), independent of SLA; or rename it "SLA first response pending". | None | WS-14 |
| D27 | DATA-005 | Are merged duplicates counted in "created during the period"? | A merged source keeps its status and can be counted twice. | Count (they were raised); or exclude (not separate demand). Either, stated once. | None | WS-14 |
| D28 | UX-005 | Should the UI block deactivating the default mailbox? | Deactivation blocks replies for every ticket using that mailbox. | A consequence confirmation (no decision needed); and optionally block the default. | Confirmation only. | No |
| D29 | UX-018 | Remove the dead foreign-change realtime code, or use it for an "updated by someone else" notice? | Dead code that grows state for the session. | Remove; or wire a notice. | None | No |
| D30 | QA-007 | Extend structural guards to Knowledge, Mail and Attachments queries, or accept example-based coverage? | Isolation outside the ticket set is by convention. | Add guards; or accept. The administrative-endpoint probe test (ARC-002) is separate and supported. | The probe test is supported; broader guards optional. | No |

Decisions D8, D9, D10 and D22 gate the work that closes four of the eleven blockers; D1 to D3 and D13 gate the production-readiness blockers; the rest can be decided later without delaying the P0 work.

## 12. Recommended remediation plan

Remediation is not started. The plan groups findings into 16 workstreams in five priority bands so that shared fixes close several findings at once. Each workstream lists the findings it closes. Sequencing assumes one implementation agent working sequentially with targeted tests only.

### 12.1 Workstreams

**P0. Security, data and mail correctness blockers**

| WS | Workstream | Findings closed | Needs decision | Notes |
|---|---|---|---|---|
| WS-1 | Administrative endpoint isolation | SEC-001, SEC-005, SEC-012, ARC-002, QA-007 | D14 (documentation), D30 (optional) | Fix the two endpoints; state the rule; add the probe test over every administrative GET. The endpoint fix needs no decision. |
| WS-2 | Ticket-party rule (inbound sender, participants, merge) | MSG-002, SEC-006, MSG-003, BE-001, SEC-004, BE-002, ARC-003, MSG-012 | D8, D9 | One small rule and service used by the mail bridge, merge and the Cc builder; participant provenance, cap and removal; composer shows Cc by name. |
| WS-3 | Mail delivery-state and retry correctness | MSG-001, BE-009, MSG-007, MSG-008, MSG-010, MSG-006, ARC-001 | None | Start here: a retry of an already-submitted message clears the draft id and is a deliberate re-send, with a guard on the delivery report; then explicit uncertain and bounced states; mark acknowledgements as automatic; automatic read-only reconcile of uncertain sends. |
| WS-4 | Mailbox registration and tenant scoping | MSG-004 | D10 | Creation-time probe and the decision on who may register; depends on the D-16 tenant evidence (E-2, E-3). |

**P1. Production-readiness blockers**

| WS | Workstream | Findings closed | Needs decision | Notes |
|---|---|---|---|---|
| WS-5 | Hosting and security hardening | SRE-001, SRE-006, SRE-010, SRE-014, SEC-011, SEC-009, SEC-010 | D1 | Production artefact and proxy; verify SRE-006 first; production start-up guards (storage, scan requirement); document security headers for the app page; origin allow-list for the hub; restrict `data:` links to images. |
| WS-6 | Recovery: backup and restore, Data Protection keys, migration policy | SRE-002, SRE-003, SRE-007, SRE-008, ARC-007 | D2, D3, D5, D6 | Persistent keys; backup plus one recorded restore drill; the migration-order paragraph and test; state the supported topology. |
| WS-7 | Observability and operations | SRE-004, SRE-005, SRE-009, SRE-011, SRE-012, SRE-013 | D4, D7 (pruning) | Small set of counters (oldest pending outbox age, failed jobs, last mail sync, SLA lag); readiness; log redaction; dependency-down runbook. |
| WS-8 | Abuse controls and attachment safety | SEC-002, SEC-003, MSG-005 | D11, D13 (and D-08 engine) | Built-in rate limiter and body limits; extend blocked types; acknowledgement cap; scanner when chosen. |

**P2. Workflow, data-integrity and reliability**

| WS | Workstream | Findings closed | Needs decision | Notes |
|---|---|---|---|---|
| WS-9 | Durable after-commit processing | BE-004, BE-005, BE-015, ARC-004 | D16 | Reuse the outbox pattern for automation and SLA follow-up; attempts and last error on the outbox row; store the message id on the item. |
| WS-10 | Replay, idempotency and staleness protection | BE-003, BE-010, QA-004, UX-016, UX-003, QA-001, BE-013 | D19 | Optional client message id as the unique source reference; portal reply with attachments as one outcome; realtime reconcile; first-save race; client state keyed by route. |
| WS-11 | Test debt for recovery and concurrency | QA-002, QA-003, QA-005, QA-006, QA-008 | None | The targeted tests in section 9. |

**P3. ITSM, reporting and operational correctness**

| WS | Workstream | Findings closed | Needs decision | Notes |
|---|---|---|---|---|
| WS-12 | SLA lifecycle and policy re-evaluation | BE-006, BE-007, BE-014, BE-008, DATA-007 | D17, D18 | Decide, then a small change in the SLA code; order the SLA sort so tickets without a deadline come last; label the two "about to breach" ideas. |
| WS-13 | Service-desk workflow clarity | ITSM-001 to ITSM-007, MSG-009, BE-012, SEC-007, SEC-008 | D20, D21, D22, D23, D24, D12, D15 | Needs-response signal; agent handoff; mailbox-less tickets (settle before the portal opens); portal wording; resolve prompt; setup overview; identity-case paging. |
| WS-14 | Reporting foundation and audit-retention interaction | DATA-001 to DATA-006, DATA-008, DATA-009, ARC-005, MSG-011 | D7, D25, D26, D27 (and D18) | One shared definition of counted ticket, open work and awaiting first response; fix the sidebar counts; consistent creation events; retention separation before Historical Reports. |

**P4. UX, accessibility, low and polish**

| WS | Workstream | Findings closed | Needs decision | Notes |
|---|---|---|---|---|
| WS-15 | UX and accessibility | UX-001, UX-002, UX-004 to UX-015, UX-017, UX-018, UX-019 | D28, D29 | Dialog and shortcut contract (UX-001, 006, 007, 008, 013 share one cause), notification deep link, accessible names, confirmations, mailbox deactivation warning. |
| WS-16 | ADR, documentation and copy debt | DOC-001 to DOC-011, ARC-006, ARC-008, ARC-009, SEC-013, BE-011, BE-016 | None | The missing ADRs (outbox interceptor, identity links and audiences, mentions and presence, user preferences, reply-link contract, Platform Admin permission meaning); documents that contradict locked rules; copy and terminology; small hygiene items. |

Sixteen workstreams account for every one of the 118 findings exactly once (checked when this plan was built).

### 12.2 Shared fixes that close several findings

| Fix | Closes |
|---|---|
| One ticket-party rule (sender must be requester or participant; participants have provenance, a cap and removal) | MSG-002, SEC-006, MSG-003, BE-001, and shapes SEC-004/BE-002 |
| Reuse the outbox pattern for automation and SLA follow-up, add attempts and last error | BE-004, BE-005, BE-015, ARC-004 |
| Classify content on administrative endpoints, fix two endpoints, add one probe test | SEC-001, SEC-005, SEC-012 (structure), ARC-002 |
| Clear the draft id on retry, guard the delivery report, then explicit uncertain and bounced states | MSG-001, BE-009, MSG-007, ARC-001 |
| One shared definition of counted ticket, open work and awaiting first response | DATA-001, DATA-002, DATA-005, ARC-005 |
| Optional client message id as the unique source reference | BE-003 (agent and portal), part of QA-004 |
| Decide D17 and D18 once | BE-006, BE-007, BE-014 (and the reporting meaning of stored SLA events) |

### 12.3 Order of work

1. **Decisions first (no code).** D8, D9, D10 and D22; then D1 to D3 and D13 (production readiness). The workstreams that need no decision can start immediately.
2. **P0, in this order.** WS-3 (local, no decision, closes B-3) and WS-1 (closes B-1, no decision for the fix) first; WS-2 once D8 and D9 are made; WS-4 once D10 is made, with the tenant evidence from D-16 where possible.
3. **Run the D-16 tenant validation after WS-2 and WS-3 land**, so the real-tenant run exercises the final threading and retry behaviour rather than code that is about to change.
4. **P1.** Verify SRE-006 first (one check), then WS-5, WS-6, WS-7 and WS-8. WS-6 needs a recorded restore drill before it counts as closed.
5. **P2, P3, P4** in band order. Inside P3, settle D17 and D18 before WS-14, because the reporting definitions depend on them. Do not start Historical Reports before WS-14's definitions and the retention separation (D7) are settled.
6. After the P0 and P1 fixes, ask for permission to run broader verification (a full regression run, a broad browser pass, load and query testing, the restore drill): these have not been run and are the evidence that readiness needs.

Nothing in the plan introduces a new subsystem, framework or dependency; each workstream reuses an existing mechanism.

## 13. Strengths / areas not to disturb

These are consolidated from all ten reviews. They should not be reworked while fixing the findings above.

- **Architecture.** A one-way module graph enforced by architecture tests; contracts inverted correctly (Mail does not reference Ticketing); one consistent concurrency toolkit (version tokens, row locks, advisory locks, atomic claims, skip-locked, deterministic keys); audit, SLA clocks, notification intent and outbound mail commit in the same transaction as the change; a documented, working outbox interceptor; thin Hangfire entry points; an explicit per-module migration order; injected time everywhere.
- **Security.** Department scope filter on tickets, messages and requesters with non-disclosing 404s; three declared authorisation patterns pinned by tests; every scope-bypass call site reviewed and pinned; per-ticket re-authorisation in bulk; Internal Notes excluded in queries, search, snippets, notifications, mentions, presence and realtime; separate note and requester realtime groups with content-free payloads; sanitised mail HTML shown in a script-less sandboxed frame; stored signature HTML sanitised; webhook client state hashed and compared in constant time; platform audit limited to organisation and identity events; no committed secrets; attachment download forced as attachment with a fixed content-type table.
- **Mail.** Sender identity from the ticket's own mailbox, never an agent address; no silent fallback; atomic claim, draft id and submit flag persisted before sending; no blind retry of an ambiguous send; honest "accepted for delivery, not confirmed as delivered" wording; evidence into another department discarded; subject references never attach by themselves; loop and noise protection; mailbox health signals; idempotent inbound ingestion.
- **Domain and data.** Strong CHECK constraints, composite foreign keys keeping relations inside one department, unique keys for idempotency and numbering, merge in one transaction, business-time SLA arithmetic that handles clock changes, keyset pagination, batched list projections, trigram search.
- **Workflow.** Four statuses with defined meaning and reopen on requester reply; one conversation across email and portal; truthful delivery states; followers, mentions, presence and collision hints; queues, saved views, default view, columns and quick filters; a dashboard whose figures equal the queue filters they link to and carry no per-agent ranking; request forms with explicit audiences, help topics, requester-safe Knowledge links; a setup overview with warnings; macros that only prepare drafts and never exceed the caller's permissions.
- **Reporting.** Messages are immutable, every lifecycle change is audited with previous and next values, every SLA transition is an event with its cycle: a truthful historical report can be built without invented snapshots.
- **Frontend.** The dialog focus handling, composer mode clarity (words, icon, banner), per-ticket drafts, glyph-plus-text status marks, URL-held filters, advisory-only realtime, idempotent portal creation.
- **Copy and documents.** Portal copy says "request", "Internal note" naming is consistent, British spelling holds, empty states are actionable, the open-decisions list matches the known gates.
- **Operations basics.** Environment guards on dev sign-in and the Fake mail provider, secure cookies, HSTS, a read-only Hangfire dashboard limited to Platform Admins, migrations off at start-up by default with an explicit command, a non-root image, loopback-bound development ports, secrets out of configuration files, separate live and ready probes.
- **Tests.** The test foundation described in section 9.

## 14. Review coverage

All ten specialist reviews are complete and represented. The review was performed in two passes: four specialists first (DOC, UX, SRE, QA), then the other six one at a time after a session limit interrupted the first run.

| Specialist | Prefix | Section | Findings | Method |
|---|---|---|---|---|
| Documentation / Content Designer | DOC | 21 | 11 | Grep-based sample of product copy and documents; no runtime checks |
| Frontend / UX Engineer | UX | 21 | 19 | Static reading of the web app; no browser, Playwright or Vitest |
| SRE / Production Operations | SRE | 21 | 14 | Static reading of deployment, configuration and jobs; no Docker run, no restore drill |
| QA / Reliability | QA | 21 | 8 | Static review of test suites by inventory and grep; nothing run |
| Security / Privacy | SEC | 15 | 13 | Static reading of the authorisation, identity, portal, realtime, attachment and logging paths, plus a few read-only requests against the dev API as the bootstrap Platform Admin to confirm SEC-001 |
| Microsoft 365 / Messaging | MSG | 16 | 12 | Static reading of the Mail module and its Ticketing seams; no Graph or tenant call, no mail sent |
| Backend / Domain | BE | 17 | 16 | Static reading of services, mappings, constraints, jobs and the migration list |
| Lead Architect | ARC | 18 | 9 | Module graph, contracts, cross-schema coupling, after-commit mechanisms, ADR set, repeated rules |
| ITSM / Service Desk | ITSM | 19 | 7 | Design and code read as a service desk; no screen walked in a browser |
| Data / Reporting | DATA | 20 | 9 | Metric definitions and captured history read from the code; no query run |

**Limitations that remain (do not read this report as more than it is).**
- **D-16 is open.** Every statement about what Graph or Exchange actually does is code, simulator or documentation evidence. The runbook result table is empty.
- **No full regression suite** (backend, frontend or end to end) was run, no broad Playwright pass, no architecture or isolation matrix run; test suites were reviewed statically.
- **No production-scale load or query testing**, no `EXPLAIN` runs, no scale data. Performance statements are about query shape only.
- **Runtime checks were explicitly not performed**, apart from the Security review's few read-only requests against the local dev API. The dev API may predate the newest endpoints (queue preferences, queue types, article links), which were judged from code.
- **No restore drill, no Docker build or run, no deployment**, so hosting findings are about missing artefacts and configuration, not observed failures.
- **No data was changed**: the DEMO department was not reset or mutated, no real email was sent, no Graph call was made.
- **Sampling.** The Documentation, Frontend and QA reviews were sampled (see their limits in section 21.7); the later six read the areas named above, not every file.
- **Refinement 9 and 10 code** (queue preferences, queue filters, Knowledge maintenance, reply links) was reviewed by the Security, Backend and Architect passes for the points they raised; it received no separate dedicated review.
- **Unresolved product behaviour** is recorded as decisions (section 11); no specialist decided it on the owner's behalf.

---

# Specialist evidence

The sections below are the detailed evidence for the consolidated view above. They keep every finding, its evidence and its recommended fix, and the limits each specialist stated. Cross-references such as "section 16.7" refer to the numbering used here.

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
- **Update:** the Backend review (section 17, BE-002) found the other half of the effect: the source requester stops receiving replies and the request disappears from their portal.
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
- **Update:** the Messaging review (section 16, MSG-002) confirmed this and escalated it to High: the sender and the message's To/Cc become ticket participants who receive later agent replies.
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


---

# 16. Microsoft 365 / Messaging review (added after the Security section)

Specialist: Microsoft 365 / Messaging Engineer (AGENTS.md section 11). Baseline: commit `081f660`. Method: single agent, read-only, static reading of `Helpdesk.Modules.Mail` (provider, inbound pipeline, correlation, outbound sender, sync, subscriptions, mailbox service), the Ticketing seams (`MailBridge`, `TicketService` reply path, acknowledgements) and the email docs and runbook. No Graph or tenant call was made, no mail was sent, nothing was run or changed. Security, Frontend, QA, SRE and Content findings were reused, not repeated.

**Evidence classes used below.** *Code* = what the implementation does. *Simulator* = behaviour of `FakeMailProvider` and recorded-shape tests. *Real tenant* = observed against Microsoft 365: **none exists**. The runbook result table for E-1..E-38 is empty, so every statement about what Graph or Exchange actually does is unverified. Simulator results are not tenant evidence.

**Result in one line:** the sender-identity and no-fallback rules, the outbound state model and the "never retry an uncertain send blindly" rule are well built. The two serious problems are a correctness defect in retry after a bounce and the fact that correlation evidence is trusted without checking who sent the mail.

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 2 |
| Medium | 4 |
| Low | 5 |
| Polish | 1 |
| **Total** | **12** |

Production blockers from this review: **3** (MSG-001, MSG-002, MSG-004), plus the standing gate **D-16**. Decisions required: **5** (MSG-002, MSG-003, MSG-004, MSG-005, MSG-009).

## 16.1 Conclusion on SEC-006 (can an unrelated sender attach mail to an existing ticket?)

**Yes, and the impact is larger than the Security review stated.** Findings from the code:

1. **Attachment needs only a bearer-style identifier, never a sender check.** `CorrelationEngine.CorrelateAsync` (`MailRules.cs:110-189`) attaches a message to a ticket if any one of these matches, in order: an `In-Reply-To` or `References` id equal to a stored Message-ID of any inbound or outbound mail of that department; the `[hd:<token>]` footer text or an `x-helpdesk-token` header equal to a stored outbound token; or the Exchange `conversationId` of any message in the same mailbox. Nothing compares the sender with the ticket's requester or participants (`InboundProcessor.TicketAsync`, `MailBridge.AddMessageAsync`).
2. **What the three kinds of evidence are worth.**
   - Message-IDs and tokens are unguessable, but they are *capabilities*, not identities: everyone who ever received a message in the thread (To, Cc, a forwardee, an archive, a mailing list) can see them. The token is printed in the visible footer of every reply and is quoted in every later reply. The `x-helpdesk-token` header is attacker-supplied input, so it is accepted at face value too.
   - `conversationId` is weak. The project's own spike notes expect it to derive from the conversation topic, that is, from the subject (E-15, unverified). It is accepted as sole evidence (step 4), which makes it a subject match in effect, against the rule that a subject is only ever a hint.
3. **Consequence of attaching.** The message is added as a *public* message of the ticket, the ticket is reopened (D-15), the sender is created or reused as a requester row, and **the sender plus every To/Cc address in the message become ticket participants** (`MailBridge.cs:302-311`). `TicketService.PostMessage` (`TicketService.cs:462-470`) copies all participants as Cc on every later public agent reply. So a single email carrying a valid identifier lets an unrelated party add themselves, or anyone, to the Cc line of every future reply on that ticket. The composer banner shows only a count (for example "Cc 2"), not who.
4. **Existing protections that still hold:** a match pointing into another department or another mailbox is discarded and flagged; the department always comes from the receiving mailbox; a subject reference alone never attaches (it only produces a note for the agent).

**What evidence should be trusted.** Headers and tokens are good proof that the message belongs to the *thread*, not that the sender may *act on* it. The safe rule is: strong correlation evidence AND a sender who is the ticket's requester or an existing participant, compared on normalised addresses, may attach as today. Any other sender should not get a public message, a reopen or new participants. What happens to such mail instead is a product choice (see MSG-002). `conversationId` should not be sufficient on its own for any sender.

## 16.2 High

### MSG-001: Retry after a confirmed bounce does not send anything and reports the message as Sent
- **Specialist / category:** Messaging / Bug. **Severity:** High. **Confidence:** High (code, and the simulator keeps the sent copy permanently; real-tenant behaviour depends on E-25).
- **Evidence:** a bounce correlated by `NdrCorrelation` moves the original outbound row to `Failed` with `ndr_delivery_failed` (`InboundProcessor.cs:214-234`) and leaves its `ProviderDraftId`. The agent UI shows "Retry sending" for that failure (`Conversation.tsx:110`; the button is hidden only for `outcome_uncertain`). Both retry routes (`MailOutbox.RetryAsync`, `OutboundMail.cs:140-155`, and `MailboxService.RetryAsync`, `MailboxService.cs:318-324`) set the row back to `Queued` without clearing `provider_draft_id`. `OutboundSender.SendAsync` then calls `FindSentCopyAsync(existingDraft)` first (`OutboundMail.cs:205`); the original message is still in Sent Items, so a sent copy is found, the send is skipped, and the row is marked `Sent` with `LastError` cleared. No test covers retry after a bounce.
- **Affected area:** outbound retry; delivery state shown to agents and requesters' replies.
- **Impact:** an agent corrects a typo in the requester's address or simply retries a bounced reply. The system says "Sent by email", nothing leaves the mailbox, and the requester never receives the answer. The bounce evidence is cleared from the row (audit keeps the original event). Same effect for any `Failed` row whose draft was already sent, for example after a database error that followed a successful send.
- **Smallest recommended fix:** a retry of a message whose earlier send was *submitted* (bounce, or failure after submission) must create a new draft (clear `provider_draft_id` and the submit flag) and be recorded as an explicit re-send, as the existing "accept duplicate risk" path already does for uncertain mail; or block Retry for `delivery_failed` and tell the agent to fix the address and send a new reply. Add one test: send, bounce, retry, assert a second provider send.
- **Decision required:** No (either fix gives a correct outcome; the second is the smaller change). **Production blocker:** Yes.
- **Related:** MSG-007 (a forged bounce can trigger this state).

### MSG-002: Correlated mail is accepted from any sender and can add recipients to the ticket (SEC-006)
- **Specialist / category:** Messaging / Security, Privacy. **Severity:** High. **Confidence:** High for the code path, Medium for ease of exploitation (needs a Message-ID or token from the thread).
- **Evidence:** section 16.1 above; `MailRules.cs:110-189`, `InboundProcessor.cs:260-289`, `MailBridge.cs:215-311`, `TicketService.cs:462-470`.
- **Affected area:** inbound threading; ticket participants; outbound Cc.
- **Impact / abuse scenario:** a requester forwards a support email to a colleague, or the thread is archived in a shared mailbox. Anyone who can read that email knows the thread's Message-ID and the `[hd:…]` token. They send a message to the department mailbox with `In-Reply-To` set to that id, or quoting the footer, with their own address in Cc. The ticket reopens, their text appears to the requester as "Another participant", and from then on every public agent reply (including internal-facing detail an agent writes to the requester) is also emailed to the sender and to anyone they Cc'd. Nobody is warned.
- **Smallest recommended fix:** do not attach, reopen, or add participants for a message whose sender is not the ticket's requester or an existing participant. As a minimum independent of the product decision, never create participants from the To/Cc of a message sent by a non-participant. List the Cc recipients by name in the composer banner so the agent sees them.
- **Decision required:** Yes. Options for mail from other senders that carries valid evidence: (a) create a new ticket with a note naming the suggested ticket (as for a subject hint), (b) hold it for review on the suggested ticket without showing it to the requester or reopening, (c) attach but without participants and reopen (smallest change, still shows their text to the requester). Also: may a participant's reply add further participants? **Production blocker:** Yes.
- **Related:** SEC-006, SEC-004, MSG-003, MSG-012.

## 16.3 Medium

### MSG-003: `conversationId` is accepted as sole evidence although it behaves like a subject match
- **Category:** Messaging. **Severity:** Medium. **Confidence:** Low-Medium (depends on Exchange's grouping rules, which are exactly E-15/E-16/E-17 and unobserved).
- **Evidence:** `MailRules.cs:182-193` attaches on a `conversationId` match with no sender check and any earlier message of the mailbox, inbound or outbound. `EMAIL_ARCHITECTURE.md` section 5 notes it is derived from the conversation topic and promises nothing across subject changes. Policy line 35 says a subject is never sufficient.
- **Impact / abuse scenario:** if Exchange puts a new, unrelated message with the same normalised subject into the same conversation, a stranger who knows a ticket's subject (it appears in the requester's own copy of our replies) can land on it without any header or token.
- **Smallest recommended fix:** do not let `conversationId` attach on its own: require it together with a participant sender (MSG-002 rule), or drop step 4 until E-15..E-17 show it is safe.
- **Decision required:** Yes (keep, restrict to known participants, or remove). **Production blocker:** No (resolved by the MSG-002 sender rule), but E-15..E-17 must be observed.

### MSG-004: Any Department Admin can register an arbitrary mailbox; only Exchange-side scoping stops them
- **Category:** Messaging / Authorization. **Severity:** Medium. **Confidence:** Medium (code certain; outcome depends on how the tenant grants the app, which is the unobserved D-16 prerequisite).
- **Evidence:** `MailboxService.CreateAsync` (`MailboxService.cs:100-160`) validates only the address format and a 128-character `providerRef`, then activates the mailbox and starts syncing; it never asks Graph whether the mailbox exists or is in the app's scope. Creating needs `department.mailboxes.manage`, which Department Admins and Platform Admins hold. The runbook's least-privilege setup (scoped Exchange application role, no unscoped Entra `Mail.*` consent) is the only barrier, and E-2 has not been run.
- **Impact / abuse scenario:** if the app registration has tenant-wide mail permissions (a common shortcut), a Department Admin registers `finance-director@…` as their department's mailbox. The helpdesk ingests that mailbox's new mail as tickets of the department and replies from that identity. With the runbook's scoping the same action only produces permission-denied sync errors.
- **Smallest recommended fix:** on create (and on enabling), probe the mailbox through Graph and refuse on 403/404; and decide whether registering a mailbox is a Platform Admin action or needs an approved-address list.
- **Decision required:** Yes (who may register mailboxes). **Production blocker:** Yes, until the tenant scoping is proven (D-16 E-2) and the decision made.
- **Related:** SRE-010, D-16.

### MSG-005: Acknowledgements go to an unauthenticated sender and carry attacker-chosen text
- **Category:** Messaging / Operations. **Severity:** Medium. **Confidence:** Medium.
- **Evidence:** a new inbound ticket queues an acknowledgement to the `From` address (`EmailIdentity.cs:302-331`) using the department template, whose placeholders include `{{ticket.subject}}` (sender-supplied). The only inbound throttle is 30 messages per sender address per mailbox per 10 minutes (`InboundProcessor.cs:145-152`). The `Authentication-Results` header is available in the retrieved headers but not consulted. `BACKLOG.md` already records "per-requester acknowledgement rate limit" as deferred on the argument that the sender limit covers it.
- **Impact / abuse scenario:** someone sends mail with a forged or rotating `From` (any address) and a subject of their choosing. Each message creates a ticket and makes the department mailbox send the acknowledgement, containing the attacker's subject text, to the forged address: a spam and phishing reflection using the organisation's reputation, and a flood of tickets. The per-sender limit is bypassed by changing the address.
- **Smallest recommended fix:** a per-mailbox cap on acknowledgements per window, and optionally only acknowledge when the message passed SPF/DKIM/DMARC (`Authentication-Results`), or leave `{{ticket.subject}}` out of the default template.
- **Decision required:** Yes (which control, and whether unauthenticated senders get an acknowledgement). **Production blocker:** covered by the abuse-controls gate (SEC-003); not counted separately.

### MSG-006: Acknowledgements are not marked as automatic
- **Category:** Messaging. **Severity:** Medium. **Confidence:** Medium (the Graph limit on settable headers is from documentation, unverified).
- **Evidence:** no `Auto-Submitted`, `X-Auto-Response-Suppress` or `Precedence` header is set anywhere in the provider or sender (searched). The Graph draft path can set only custom `x-` headers, and only `x-helpdesk-token` is set (`MicrosoftGraphMailProvider.cs`, `CreateDraftAsync`). The inbound guard drops mail that carries those headers, so the system expects other systems to mark theirs.
- **Impact:** our acknowledgement cannot be recognised as automatic by a peer autoresponder or another helpdesk, which can answer it with a ticket-creating message; the pair then ping-pong, capped only by the per-sender limit (up to 30 tickets per 10 minutes). E-37 (inter-mailbox loop) is unobserved.
- **Smallest recommended fix:** add `x-auto-response-suppress: All` (an `x-` header) to acknowledgements through `internetMessageHeaders`, and confirm in E-37/E-38 what Exchange adds on submission.
- **Decision required:** No. **Production blocker:** No.

## 16.4 Low

### MSG-007: Any sender can pose as a bounce and mark a sent reply as failed
- **Category:** Messaging. **Confidence:** Medium. **Evidence:** `MailGuard.LooksLikeNdr` accepts a `mailer-daemon` or `postmaster` local part on any domain, or a `report-type=delivery-status` content type from anyone (`MailRules.cs:76-88`); `NdrCorrelation` then trusts a quoted Message-ID or token of a `Sent` or `Uncertain` outbound message of the same mailbox and marks it `Failed` (`InboundProcessor.cs:214-234`). **Impact / abuse scenario:** a requester, or anyone who received our reply, sends a forged bounce quoting the id; the agent sees "Delivery failed" on a reply that was delivered, and with MSG-001 a retry silently fixes nothing. **Fix:** require the bounce to come from the tenant's own transport (for example `Authentication-Results`, the `X-MS-Exchange-…` markers, or a report originating from our domain), after E-38 shows real headers. **Decision:** No. **Blocker:** No.

### MSG-008: Uncertain sends are never reconciled automatically
- **Category:** Reliability. **Confidence:** Medium. **Evidence:** `RunDispatchAsync` reconciles only rows stuck in `Sending`; an `Uncertain` row is looked at again only when an operator clicks retry (`MailboxService.RetryAsync`). After a timeout the sent copy lookup may run before Exchange has moved the draft (E-25 unmeasured), so a message that was in fact sent can stay Uncertain until someone acts. **Impact:** avoidable manual work and alarming states. **Fix:** re-run the existing read-only reconcile for Uncertain rows after a delay (never sends). **Decision:** No. **Blocker:** No.

### MSG-009: Ignored inbound mail cannot be released to a ticket
- **Category:** ITSM Workflow / Administration. **Confidence:** High. **Evidence:** the Attention list includes `Ignored` rows (`MailboxService.cs:252`) but `RetryAsync` accepts only `Failed` and `Uncertain` (`:286`). Rules that ignore mail: `Auto-Submitted` other than `no`, `Precedence` bulk/list/junk, any `List-Id`/`List-Unsubscribe`, `X-Auto-Response-Suppress`, and the sender rate limit. **Impact:** legitimate mail from monitoring tools, vendors with list headers, or a sender over the limit is recorded but can never become a ticket; the operator can only read it. **Fix:** an explicit, audited "create ticket from this message" action for ignored inbound mail. **Decision required:** Yes (is releasing ignored mail wanted, and which reasons may be released). **Blocker:** No.

### MSG-010: A re-delivered message with the same Message-ID after 2 seconds becomes a second ticket
- **Category:** Data Integrity. **Confidence:** Low (needs E-13/E-14 evidence on how often Exchange re-delivers). **Evidence:** `InboundProcessor.cs:156-175` treats the same Message-ID as a duplicate only when subject, sender and received time are within 2 seconds; otherwise it ingests the message and flags `internet_id_collision`. **Impact:** a late duplicate delivery (for example the same mail reaching the mailbox via two routes) creates a duplicate ticket or duplicate reply. **Fix:** treat same Message-ID, subject and sender as a duplicate regardless of the time gap, and flag only when the content differs. **Decision:** No. **Blocker:** No.

### MSG-011: Full bodies of all inbound mail, including ignored and bounce mail, are kept with no purge
- **Category:** Privacy. **Confidence:** High. **Evidence:** `RecordAsync` stores `BodyText` and the original HTML (`BodyHtmlOriginal`) for every inbound message, ignored ones included (`InboundProcessor.cs:112-135`); no retention job exists (SRE-011, D-09). **Impact:** unbounded retention of spam, bounces and unrelated mail containing personal data. **Fix:** after D-09, add a retention rule; until then document it. **Decision:** tied to D-09. **Blocker:** No (part of the retention gate).

## 16.5 Polish

### MSG-012: Every reply shows an opaque token to the requester
- **Category:** Copy/Terminology. **Confidence:** High. **Evidence:** `OutboundMail.cs:BuildHtml` appends "Ticket IT-1042 · [hd:<32 hex characters>]" in small grey text to every reply. **Impact:** customers see a machine token with no explanation, and it is the correlation capability discussed in MSG-002. **Fix:** short explanatory wording ("Reference for replies"), once the correlation rule is settled. **Decision:** No. **Blocker:** No.

## 16.6 Notes on the Security findings in the messaging area

- **SEC-001 / SEC-005 (mail attention):** the attention list is operational data for the mailbox administrator. The proposed redaction must keep the sender address and subject visible to Department Admins who are members; it only withholds them from a non-member Platform Admin. The same endpoint family also lets a Platform Admin re-queue mail and, with the duplicate-risk flag, resend uncertain mail in a department they are not a member of (already covered by SEC-012).
- **SEC-006:** answered above and escalated as MSG-002.
- **SEC-009 / SEC-010:** no messaging aspect.

## 16.7 D-16 status

**OPEN. No real-tenant evidence exists.** The runbook (`docs/guides/GRAPH_TENANT_VALIDATION.md`) is unrun, its result table for E-1..E-38 is empty, and the Refinement 3 attempt was recorded as BLOCKED (no approved tenant configuration). Everything in the Graph provider is written to documented contracts and exercised only through recorded-shape tests and the in-process simulator. This review did not change that, and nothing here may be read as tenant evidence.

Experiments that this review's conclusions depend on, in priority order:

| Experiment | Why it matters here |
|---|---|
| E-21, E-22 | sender is the shared mailbox under app-only auth; Reply-To; no "on behalf of" |
| E-25, E-12, E-10 | draft id stays valid after send; sent copy lookup; Message-ID rewritten on submission (MSG-001 on the real path, MSG-008) |
| E-23, E-24, E-26 | reply headers carried by `createReply`; recorded id equals recipient's Message-ID (threading, NDR matching) |
| E-15, E-16, E-17 | `conversationId` behaviour (MSG-003) |
| E-2, E-3 | out-of-scope mailbox returns 403; send under scoped grants only (MSG-004) |
| E-30..E-35 | subscription creation, expiry limits, renewal, missed notifications, 8-day continuity |
| E-37, E-38 | inter-mailbox loop; real header samples for the guard rules and bounce detection (MSG-006, MSG-007) |
| E-13, E-14 | Message-ID absence and rewriting by gateways (MSG-010) |

## 16.8 Areas reviewed with no issue found

- **Sender identity and Reply-To:** replies are created and sent through the ticket's own mailbox path (`users/{mailbox}/…`), `from` and `sender` are never set, agent personal addresses never appear, Reply-To is the helpdesk mailbox (code and simulator only; E-21/E-22 pending).
- **Ticket mailbox immutability and no fallback:** a public reply without a stored mailbox is blocked as `no_ticket_mailbox`; an inactive or unavailable one is recorded as `mailbox_unavailable`; the outbox insert re-checks that the mailbox is an active mailbox of the same department and fails otherwise. Team, assignee, status and type changes do not touch the mailbox. (The department default is used only at creation, and for acknowledgements of tickets that have none, which equals the creation rule.)
- **Outbound state model:** `Queued → Sending → Sent / Failed / Uncertain`, one atomic claim, the draft id persisted before sending, the submit flag persisted before the send call, timeouts/5xx/network treated as ambiguous and 429/4xx as not, no blind retry of an ambiguous send, explicit and audited "accept duplicate risk" as a new message, crash recovery that reconciles instead of resending once the send may have happened. Ticketing is told only "submitted" or "not confirmed", and the UI says "accepted for delivery; not confirmed as delivered" (Graph acceptance is not presented as delivery).
- **Transactional outbox:** the outbound row and the ticket message commit together.
- **Inbound idempotency:** one row per mailbox and immutable provider id, unique constraint race handled, replays find the existing result.
- **Department and mailbox isolation:** department comes only from the receiving mailbox; evidence into another department or mailbox is discarded and flagged; a subject reference never attaches.
- **Loop and noise protection:** own helpdesk addresses (every department), `Auto-Submitted`, bulk, list and `X-Auto-Response-Suppress` mail are ignored; bounces are never tickets, requester replies or acknowledgement triggers; acknowledgements are never sent to the sending mailbox and are created inside the ticket-creation transaction (one per ticket, none for replays).
- **Webhooks and subscriptions:** per-subscription client state stored hashed and compared in constant time, mailbox taken from our own record, bounded body, handshake echo; lifecycle events (`reauthorizationRequired`, `subscriptionRemoved`, `missed`) handled; subscription failures are visible on the mailbox and delta keeps working without webhooks.
- **Delta:** token advanced only after the whole batch, a 410 restarts from the configuration time with idempotent re-ingest, per-mailbox advisory lock, delta links accepted only on the configured Graph host.
- **Mailbox health:** reports subscription missing/failed/expired/expiring, sync failing or stale, and outbound failed/uncertain and inbound failed counts, with the machine reasons the UI uses.
- **Logging and diagnostics:** log templates carry ids and codes only; Graph error text is dropped and only the error code kept; tokens are not logged; audit stores no bodies.
- **Signatures:** per agent and department, sanitised once and fixed into the queued message, appended once, never on internal notes or acknowledgements, no department footer.

## 16.9 Could not be verified

- Every actual Graph/Exchange behaviour (D-16): sender display, draft and sent-copy identifiers, header handling on `createReply`, `conversationId` grouping, subscription lifetime limits, delta filter support, notification timing.
- The Graph restriction that only `x-` headers can be set when creating a draft (assumed from documentation in MSG-006).
- Whether Exchange Online attaches usable `Authentication-Results` to messages retrieved through Graph (assumed in MSG-005 and MSG-007).
- Retry after a bounce at runtime: the conclusion rests on the code and on the simulator keeping the sent copy; no mail was sent and the DEMO data was not changed.
- How the tenant grants the app registration (MSG-004).
- Exact documented subscription maximum for Outlook messages: the project's documents say just under 7 days; the configured default of 3 days is within that, but the limit was not re-checked against Microsoft documentation here and is covered by E-30.


---

# 17. Backend / Domain review (added after the Messaging section)

Specialist: Backend / Domain Engineer (AGENTS.md section 9). Baseline: commit `081f660`. Method: single agent, read-only, static reading of the services, EF mappings and constraints, jobs, migrations list and the Ticketing/Knowledge/Mail seams named below; no tests, builds or runtime writes. Security, Messaging, QA, SRE and Content findings were reused, not repeated; where they have a backend consequence it is stated and cross-referenced.

**Result in one line:** the core is carefully built (row locks plus version tokens on every ticket mutation, atomic claim queries, unique keys for idempotency, strong CHECK constraints, composite foreign keys for department ownership, no N+1 in list projections). The findings are about durability of after-commit work, a few lifecycle semantics that will mislead operators or reports, and the backend side of the earlier High findings.

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 0 |
| Medium | 8 |
| Low | 7 |
| Polish | 1 |
| **Total** | **16** |

Production blockers from this review: **0** new (MSG-001 and MSG-002 stay blockers; their backend parts are BE-001, BE-002 and the notes in 17.7). Decisions required: **6** (BE-001, BE-002, BE-004, BE-006, BE-007, BE-010).

## 17.1 Medium

### BE-001: Ticket participants have no limit and no way to be removed
- **Specialist / category:** Backend / Data Integrity. **Severity:** Medium. **Confidence:** High (code).
- **Evidence:** `PeopleUpsert.ParticipantAsync` (`MailBridge.cs:47-64`) inserts every To/Cc address with `ON CONFLICT DO NOTHING`; `AddMessageAsync` adds the sender and all To/Cc of every correlated message (`MailBridge.cs:302-311`). There is no cap anywhere (searched for a limit), and no endpoint or UI action removes a participant (the endpoint list has none; the web app only reads them). The reply path copies all participants to Cc (`TicketService.cs:462-470`).
- **Impact / scenario:** one inbound message with a very large To/Cc list (a reply-all to a distribution list, or the SEC-006 abuse) adds all of them permanently. Every later public reply on that ticket then carries that Cc list: replies go to people nobody chose, and a list over the provider's recipient limit makes every reply fail permanently with a 4xx (shown as "Delivery failed"), with no way to repair the ticket except editing the database.
- **Smallest recommended fix:** cap participants per ticket (a small constant) and per message, and add a "remove from Cc" action for agents with `tickets.update`; list the Cc recipients by name in the composer (MSG-002).
- **Decision required:** Yes (the removal action is new behaviour; the cap value and what happens above it). **Production blocker:** No (the abuse path is blocked by MSG-002).
- **Related:** MSG-002, SEC-006.

### BE-002: Merging tickets of different requesters orphans the source requester (backend side of SEC-004)
- **Category:** Backend / ITSM Workflow. **Severity:** Medium. **Confidence:** High for the mechanics, Medium for the user impact.
- **Evidence:** `TicketMerge.cs` never compares requesters. After the merge: the portal list excludes merged tickets (`PortalRequestService.cs:126`), and opening the merged request returns "Not found" unless the canonical ticket is also the caller's (`:178-190`); so the source requester's request silently disappears from their portal. Agent replies on the canonical ticket go to the canonical requester and its participants only (`TicketService.cs:462-470`); the source requester is not added as a participant. Their later email replies are routed to the canonical ticket by the merge hop in `AddMessageAsync` and show to the canonical requester as "Another participant" (`PortalRequestService.cs:224-230`).
- **Impact:** requester B stops receiving answers and can no longer find their request, while requester A starts seeing B's replies. Both are consequences of one agent action and none of it is surfaced to the agent.
- **Smallest recommended fix:** product decision, then either block or warn when requesters differ, or add the source requester as a participant of the canonical ticket and show them the canonical request.
- **Decision required:** Yes: block merge across different requesters, warn, or merge and add the source requester as a participant (and show the canonical request in their portal). **Production blocker:** No.
- **Related:** SEC-004.

### BE-003: Posting a public message has no idempotency key; the portal reply is not atomic with its attachments
- **Category:** Backend / Reliability. **Severity:** Medium. **Confidence:** High.
- **Evidence:** `PostMessageRequest` (`TicketDtos.cs:127`) and the portal reply carry no idempotency key (portal ticket creation does, via `submissionId`). `PortalRequestService.ReplyAsync` (`:237-262`) commits the message in its own transaction (`TicketIngressService`) and only then stores the files one by one; an object-store or validation failure after the commit returns an error although the message exists.
- **Impact / scenario:** a request that succeeded but whose response was lost (timeout, proxy retry, impatient second click after the button re-enables) is posted again. For an agent that is a second email to the requester; for a requester a second reply, a second reopen and a second "requester replied" notification. A failed attachment upload produces an error, the requester retries, and the message is duplicated while the files are lost.
- **Smallest recommended fix:** an optional client-generated message id used as a unique `SourceRef` (the mechanism already exists for portal creation and email), returning the earlier result on replay; and store the files in the same unit of work or report the partial outcome with the saved message.
- **Decision required:** No. **Production blocker:** No.
- **Related:** QA-004, UX-016.

### BE-004: Automation and SLA-triggered work after commit is not durable
- **Category:** Backend / Reliability. **Severity:** Medium. **Confidence:** High (mechanism), Medium (frequency).
- **Evidence:** user and ingestion changes run automation in `AfterCommitAsync` (`TicketLifecycle.cs:39-52`) with no stored intent; the SLA monitor commits the breach/warning rows and then, outside the transaction, publishes the event and runs the `SlaBreached`/`SlaApproaching` automation (`TicketLifecycle.cs:115-127`, deterministic key). The lease in the automation engine only resumes an execution when the same event is delivered again; nothing re-delivers an SLA event whose row is already marked breached, and nothing re-dispatches after a crash. By contrast, notifications do use a durable outbox.
- **Impact:** a restart or crash between commit and dispatch silently skips automation: a ticket is not auto-assigned or routed on creation, or an SLA breach rule (for example escalate to a team) never fires, and nothing records that it was skipped.
- **Smallest recommended fix:** stage a small intent row in the same transaction (the notification outbox pattern already in the codebase) and drain it with the existing recurring job, keeping the deterministic keys that make replays safe. Do not widen it into a general event framework.
- **Decision required:** Yes (accept best-effort automation, which is the current stated behaviour, or make it durable). **Production blocker:** No.
- **Related:** SRE-005.

### BE-005: One failing notification outbox item can block the whole queue; no attempt count or error is kept
- **Category:** Backend / Reliability. **Severity:** Medium. **Confidence:** High (mechanism), Low (likelihood of a persistent failure).
- **Evidence:** `DrainAsync` always takes the oldest 100 unprocessed rows (`NotificationOutbox.cs:144`); an item that throws is rolled back and stays at the head (`:146-165`). The table has no attempts, last-error or dead-letter state (`NotificationOutboxItem`). The fast path runs the same drain inside the request that published the event.
- **Impact:** if 100 items failed persistently (a data condition the recorder cannot process), no newer notification would ever be created, each agent action would spend its time retrying them, and nothing would show why. Transient failures recover on the next run, so today this is a latent risk, not an observed fault.
- **Smallest recommended fix:** record attempts and the last error code on the item, skip items beyond a retry limit (leaving them visible), and order by attempts or skip failed ids within the batch. Also expose the oldest pending age (SRE-005).
- **Decision required:** No. **Production blocker:** No.
- **Related:** SRE-005.

### BE-006: The SLA policy is chosen once at creation, before automation routes or classifies the ticket
- **Category:** ITSM Workflow / Backend. **Severity:** Medium. **Confidence:** High.
- **Evidence:** policy matching runs in `BeforeCommitAsync(Created)` inside the creating transaction (`TicketLifecycle.cs:27-37`) using the conditions `priority`, `team`, `mailbox`, `ticketType`, `subjectContains`, `requesterEmail`, `requesterDomain` (`Conditions.cs`, `PolicyFields`). Automation for `TicketCreated` runs after commit (BE-004) and may set team, priority or assignee. `OnChangesAsync` reacts only to public replies and status changes (`SlaEngine.cs:136-160`), so later team, type or priority changes never re-select the policy. ADR-014 states that the snapshot is frozen at creation, but offers Team, Type and Priority as policy conditions.
- **Impact / scenario:** an administrator creates "Network team: first response 1 hour" and a rule "route VPN mails to the Network team". Email tickets arrive without a team, get the default (or no) policy, are routed afterwards, and never match the team policy. The same happens when an agent raises the priority to Urgent. The configuration looks right and is silently ineffective.
- **Smallest recommended fix:** none without a decision.
- **Decision required:** Yes: (a) document that only creation-time attributes can select a policy and hide the unusable conditions for email/portal tickets, (b) re-evaluate the policy when team, type or priority changes (and say how the running clocks restart), or (c) run automation before the SLA is applied. **Production blocker:** No.
- **Related:** ITSM review (not yet run).

### BE-007: The first-response clock keeps running after a ticket is resolved or closed without a public reply
- **Category:** ITSM Workflow / Reporting. **Severity:** Medium. **Confidence:** High (code); the intended behaviour is not stated anywhere.
- **Evidence:** the first-response clock is stopped only by an agent public reply (`MeetFirstResponse`, `SlaEngine.cs:190-205`); `ApplyStatus` handles the resolution clock only (`:212-285`), and `RecordDue` checks `FirstResponseDueAt` with no reference to the ticket status (`:321`). A ticket resolved or closed with no public agent reply (a duplicate, spam, solved by phone, handled by internal note) still receives a recorded warning, a breach and a `SlaBreached` automation run after it is closed.
- **Impact:** breaches and automation fire on finished tickets, and any future first-response attainment report would count them as misses. The dashboard's SLA figures are unaffected because they read only the open backlog.
- **Smallest recommended fix:** none without a decision.
- **Decision required:** Yes (should resolving or closing stop the first-response clock, and should that count as met, as not applicable, or as the miss it is today?). **Production blocker:** No.
- **Related:** Data/Reporting review (not yet run).

### BE-008: Sorting the queue by SLA hides every ticket that has no deadline
- **Category:** Bug / UX-visible. **Severity:** Medium. **Confidence:** High.
- **Evidence:** `sla_due_asc` keeps only tickets that have a `next_due_at` (`TicketQueryService.cs:403-405`, comment "a ticket without one has no place in this order"), although the cursor code reserves a maximum-date sentinel for tickets without a deadline (`EncodeCursor`, `slaDue ?? DateTimeOffset.MaxValue`), which suggests they were meant to sort last. The queue's SLA column header toggles this sort (`QueuePane.tsx`).
- **Impact:** an agent clicking the SLA header sees only part of the queue; in a department with no SLA policy the list becomes empty ("No tickets here", with a "Reset filters" button). Tickets disappear from view without any explanation.
- **Smallest recommended fix:** order tickets without a deadline last instead of removing them, and make the cursor handle the sentinel.
- **Decision required:** No. **Production blocker:** No.
- **Related:** Frontend (UX) review.

## 17.2 Low

### BE-009: The delivery-state report has no transition rules
- **Category:** Data Integrity. **Confidence:** Medium. **Evidence:** `DeliveryStateSink.SetAsync` (`MailBridge.cs:385-413`) sets any of Pending/Sent/Failed over the current value, using an `ExecuteUpdate` without a version or ordering guard; a late or replayed report can overwrite a newer one (for example Failed over Sent, or Sent over Failed). The Mail side's own state is the truth and the dispatcher re-reports, so it converges, but only on the next dispatch pass. Together with MSG-001 it lets a failed bounce be reported as Sent. **Fix:** ignore a report older than the stored one (use the mail row's `updated_at` or a sequence) and make Sent over a bounce-Failed impossible without a real new send. **Decision:** No. **Blocker:** No.

### BE-010: Bulk edits apply to whatever state a ticket is in now
- **Category:** Concurrency. **Confidence:** High. **Evidence:** `BulkOneAsync` locks the row but passes `expectedVersion: null` (`TicketService.cs:801-811`) and the request carries no versions. Each ticket is processed in its own transaction, audited with previous and next values, and permission and team/assignee validity are re-checked per ticket. **Impact:** an agent selects tickets from a list that is minutes old and sets "Resolved"; a ticket the requester has just replied to (which reopened it) is resolved again, and an assignment another agent just made is overwritten. No inconsistent state results; the loss is of a decision made in between. **Fix:** optionally send each row's version and report "changed since you selected it" per ticket. **Decision required:** Yes (last-write-wins by design, or per-ticket version checks in bulk). **Blocker:** No. **Related:** QA-004.

### BE-011: Invalid references answer with 400 in some places and 409 in others
- **Category:** API Consistency. **Confidence:** High. **Evidence:** a foreign or inactive team or assignee on a ticket is `409 invalid_state` (`TicketService.ValidateTeamAsync`/`ValidateAssigneeAsync`); the same mistake on a request form default team, ticket type, category, help-topic form, default view or owner is `400` with a field error (`FormsAdmin.cs:308`, `HelpTopics.cs:151`, `KnowledgeAdminService.CheckCategoryAsync`, `QueuePreferences.cs`). **Impact:** clients cannot treat "bad reference" uniformly, and the web client has to know which form each endpoint uses. **Fix:** pick one status for "reference not valid for this department" and apply it everywhere (the field-error 400 is the more common). **Decision:** No. **Blocker:** No.

### BE-012: Requester identity resolution is capped at 100 cases with no paging or search
- **Category:** Administration. **Confidence:** High. **Evidence:** `RequesterIdentityAdminService.ListAsync` returns the 100 most recently active unlinked requesters of the department (`:35-42`). **Impact:** in a department that receives much email, every external sender is an "unlinked requester"; an employee whose old tickets are not among the 100 most recent cannot be found in the screen, so their history cannot be resolved by an administrator. **Fix:** paging and a name/email search on the list. **Decision:** No. **Blocker:** No.

### BE-013: Saving queue preferences for the first time twice at once returns a server error
- **Category:** Concurrency (Refinement 9 code). **Confidence:** Medium. **Evidence:** `QueuePreferencesService.SetAsync` reads the row, adds one if absent and saves (`QueuePreferences.cs:77-88`) without handling the primary-key violation. **Impact:** a double click or two tabs saving before any row exists makes the second request fail with a 500 instead of succeeding. Recoverable by saving again. **Fix:** upsert in one statement or catch the unique violation and retry as an update. **Decision:** No. **Blocker:** No.

### BE-014: Lifecycle timestamps are overwritten, so the first resolution time is not kept
- **Category:** Data Integrity / Reporting. **Confidence:** High. **Evidence:** `Ticket.ApplyStatus` clears `ResolvedAt` and `ClosedAt` when a ticket is reopened and sets `ResolvedAt` to the new time when it moves from Closed to Resolved (`Entities.cs:ApplyStatus`). The SLA row keeps only the current resolution cycle (`ResolutionCycle`, earlier cycles live in `sla_events`); requester names and addresses are also rewritten by later mail and portal use. **Impact:** "when was it first resolved" and reopen counts must be rebuilt from audit and SLA events; there is no stored first-resolution time. **Fix:** none now; keep the audit and SLA event streams complete and decide the reporting definitions before Historical Reports. **Decision:** No. **Blocker:** No. **Related:** Data/Reporting review (not yet run).

### BE-015: A queued notification is classified from the ticket's state at drain time, not at event time
- **Category:** Reliability. **Confidence:** Medium. **Evidence:** the outbox item for a message carries no message id (only mentions do), and `NotificationRecorder` decides "requester reply" or "agent reply" from the latest public message when it runs (`Notifications.cs:94-99`) and "assigned" from the current assignee. **Impact:** when the drain is late (backlog, restart), a requester reply followed by an agent reply is recorded as a followed update, so the assignee misses "the requester replied"; intermediate assignees are not notified. In normal operation the fast path runs immediately, so this is rare. **Fix:** store the message id on the item and classify by that message. **Decision:** No. **Blocker:** No.

## 17.3 Polish

### BE-016: A validation message lists the wrong allowed sources
- **Category:** Copy/Terminology. **Evidence:** `TicketIngressService.cs:22` says "source must be one of Web, Manual" while `MessageSources.IsValid` also accepts `portal`. **Fix:** build the text from the allowed set. **Decision:** No. **Blocker:** No.

## 17.4 The specifically requested investigations

- **MSG-001 (bounce retry becoming Sent):** confirmed from the backend side. `MailOutbox.RetryAsync` and the admin retry requeue a `Failed` row without clearing the provider draft id; the sender looks up the sent copy first. The Ticketing report path then accepts Failed to Sent (BE-009). A correct fix needs both the send decision (clear the draft id and record a deliberate re-send, as the accept-duplicate path already does) and a guard on the report.
- **MSG-002 (correlated stranger joining a ticket):** the writes that follow are one requester row for the stranger, a public requester message, reopen, notifications to the assignee and followers ("requester replied"), automation `RequesterMessageAdded`, and unbounded permanent participants (BE-001). Nothing in the schema ties a message author or participant to the ticket's requester, so the database would not stop it; the fix belongs in the application (MSG-002).
- **SEC-004 (cross-requester merge):** BE-002.
- **Portal reply de-duplication:** BE-003 (no key, and not atomic with attachments).
- **Bulk last-write-wins:** BE-010 (atomic and audited per ticket; the loss is of an intermediate decision).
- **Notification outbox poison and retry:** BE-005 (head-of-line risk, no attempt tracking) and BE-015 (late classification). The claim-and-record unit is correct: one transaction, an atomic claim, so a replay or a second worker cannot duplicate; a failed item rolls back and is retried.

## 17.5 Areas reviewed with no issue found

- **Ticket mutation concurrency:** `LockAndLoadAsync` takes a row lock, rejects merged tickets and checks the version token; update, assignment, properties, merge (locks in sorted id order), links and follower changes all use it, and conflicts return a stable 409 version-conflict.
- **Constraints and mappings:** CHECK constraints for status, priority, source, message kind/author/delivery combinations, internal notes never delivered or authored by requesters, SLA policy targets, ticket fields, links not self; composite foreign keys keep message, link, requester-link and merge relations inside one department; unique keys for references, numbers, email per department, source references, participants, followers, link pairs, mention keys, unread notification per user and ticket, requester link per requester and per account and department.
- **Numbering and idempotency:** department counters, unique `(department, source, source_ref)`, portal creation key includes the requester id.
- **Requester linking:** insert-if-absent with conflict handling for both automatic and administrator links, one account per department, audited, access refresh to the hub.
- **Followers, mentions:** eligibility re-checked, rows cascade with membership, idempotent writes; mentions are keyed by note and user and notify only users who can read notes at creation and at read.
- **Merge transaction:** locks, repointing of earlier merges, link cleanup, follower transfer, SLA stop and audit in one transaction; events published after commit.
- **SLA arithmetic:** business-time calculations over `TimeZoneInfo` handle daylight-saving gaps and repeats deliberately, exceptions and holidays, pausing and resuming with remaining time, new cycle on reopen; the monitor claims rows with `FOR UPDATE SKIP LOCKED` and the recorded events are deterministic.
- **Queue and dashboard definitions:** one shared predicate set, merged sources excluded, keyset pagination with a unique tie-breaker on every sort (the SLA sort aside, BE-008); lists are projected in batches with no per-row queries; searches use trigram indexes and bounded pages; lookups are capped.
- **Hangfire:** recurring jobs are idempotent and guarded with `DisableConcurrentExecution`; one-off send and sync jobs rely on atomic claims, so retries cannot double-send.
- **Request Forms and Help Topics:** every referenced form, type, team, category and mailbox is validated against the department; optimistic versions on updates; audited.
- **Knowledge maintenance (Refinement 10):** department-scoped, owner eligibility validated server-side, review dates compared in London time, version-checked, audited; the stored owner is never trusted for display.
- **Time zones:** UK calendar logic is in the SLA calendar and in the Knowledge overdue check; the container image is a Debian-based ASP.NET image (time zone data not verified at runtime, see 17.6).

## 17.6 Could not be verified

- Query plans and performance on realistic volumes (no data, no `EXPLAIN` run; the scale-test rule applies).
- The behaviour of earlier data migrations against production-shaped data; only the list and the two newest migrations were read, and the newest two were exercised up/down/up earlier in this project.
- Time zone database availability in the production container (`Europe/London` lookups would throw if it were missing).
- Whether the migration role may create the `pg_trgm` extension in the target database (`Phase4Productivity` runs `CREATE EXTENSION IF NOT EXISTS`).
- Runtime reproduction of BE-001, BE-003, BE-007 and BE-008; all rest on the code paths cited. No data was changed.
- The Graph-dependent parts of the mail/ticket integration (see section 16.9).

## 17.7 Notes on earlier findings with a backend consequence

- **QA-004 (concurrency coverage):** the two decisions it asks for are answered technically here: portal and agent message posts are not idempotent (BE-003); bulk is last-write-wins on the fields it sets (BE-010).
- **SEC-003 (abuse controls):** portal create and reply have no per-account limit, and participants have no limit (BE-001); both belong in the same gate.
- **SRE-005:** the missing outbox age and failure signals are the operational side of BE-005.
- **UX-016 / UX-003:** the client-side keys and the Knowledge publish retry have no backend defect; the server rejects stale versions correctly.


---

# 18. Lead Architect review (added after the Backend section)

Specialist: Lead Architect / Technical Lead (AGENTS.md section 7). Baseline: commit `081f660`. Method: single agent, read-only. I checked the module graph (project references, the architecture tests), the cross-module contracts and the raw SQL and foreign keys that cross schemas, the three after-commit mechanisms, the migration runner, Hangfire and SignalR wiring, the ADR set against what has been built, and the places where the same business rule is written more than once. I did not re-run the other reviews; section 18.2 uses their findings as evidence.

**Result in one line:** the modular monolith is healthy and should not be restructured. The module graph is a clean one-way DAG enforced by tests, the contracts are inverted correctly, and the concurrency and idempotency tools are used consistently. Nearly every serious finding in this report traces to **four patterns applied unevenly**, not to a wrong architecture. Fixing those four patterns, each in its own small place, would resolve about fifteen findings.

Counts for this section (new architectural findings; nothing here is a production blocker of its own, the blockers it explains are listed in 18.2):

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 0 |
| Medium | 4 (ARC-001 to ARC-004: systemic root causes that consolidate earlier findings; they add no new defect, see note) |
| Low | 5 (ARC-005 to ARC-009: new) |
| Polish | 0 |
| **Total** | **9** |

*Note on counting.* ARC-001 to ARC-004 are included so the root causes appear in the report's severity list, but each is the parent of existing findings (shown in 18.2). When totals across the report are used for planning, count them as four decisions about patterns, not four additional defects. Production blockers from this section: **0 new**; Decisions required: **0 new** (the decisions already listed under MSG-002, BE-004, BE-006, BE-007 and SEC-012 settle ARC-001 to ARC-004).

## 18.1 Strengths of the current architecture (do not disturb)

- **Module graph.** A one-way DAG: Audit and SharedKernel at the bottom, then Identity, Organisation, Attachments, Knowledge, Mail, and Ticketing at the top; the Host alone references everything. Mail does not reference Ticketing: it defines the interfaces it needs (`IInboundTicketSink`, `IDeliveryStateSink`, outbound contracts) and Ticketing implements them. Knowledge defines `IRequesterAudience`, implemented in Ticketing. The architecture tests enforce this (no use of another module's internals, only contracts and the registration class are public, contracts expose no persistence entities, only the Host references all modules, the project reference DAG).
- **Authorisation structure.** Three declared patterns (department route permission, resource permission through a resolver, authenticated-only) and architecture tests that pin every endpoint's declaration, the reviewed anonymous list, the authenticated-only list, CSRF exemptions, and every `IgnoreDepartmentScope()` call site.
- **Atomicity where it matters.** Ticket change plus audit entry plus SLA clocks plus notification-outbox row plus outbound mail row commit together (audit and mail writes use the caller's transaction by design, ADR-013). Realtime is advisory and rebuilt from REST.
- **Concurrency model.** One consistent toolkit: `xmin` version tokens at the API, `SELECT … FOR UPDATE` for ticket mutations (sorted lock order for merge), advisory locks for mailbox sync, atomic `UPDATE … WHERE state = …` claims for the outbound sender and the notification outbox, `FOR UPDATE SKIP LOCKED` for the SLA monitor, deterministic keys for automation and unique indexes for idempotency.
- **Notification outbox.** Staged by a `SaveChanges` interceptor in the same transaction as the qualifying change, claimed and recorded in one transaction, drained by a fast path and a recurring job. The interceptor's limit (it sees only EF-tracked changes) is documented in the code, and today no path writes status, assignee, priority or public messages by raw SQL or `ExecuteUpdate`, so the limit holds.
- **Shared read definitions.** The queue filters and the dashboard use one set of predicates (`QueuePredicates`), so a dashboard figure equals the queue it links to; SLA business-time arithmetic is a pure, deterministic class.
- **Hangfire use.** Jobs are thin entry points into module-owned runners; all logic and idempotency live in the modules, so the scheduler stays replaceable.
- **SignalR boundary.** Server-decided groups, one client-callable method, content-free payloads, separate note group, separate requester group, periodic re-check of access.
- **Migration ownership.** Each module owns its DbContext, schema and migrations; an explicit order number per module and a runner (`ModuleMigrationRunner`); migrations are off at startup by default.
- **Time handling.** `TimeProvider` is injected throughout; UK time is handled in the SLA calendar and in the Knowledge overdue check.

## 18.2 Root-cause map: which findings share one cause, and which are local

| Root cause | Findings it explains | Systemic or local | One fix that resolves several |
|---|---|---|---|
| **RC-1. After-commit side effects are durable in some places and best-effort in others.** Durable: notification outbox, mail outbox, delivery-state re-report. Not durable: automation dispatch, SLA breach follow-up, hub access refresh (re-checked every minute), realtime publish (advisory by design). | BE-004, BE-005, BE-015, SRE-005; QA-001 is the client-side mirror | Systemic for automation and SLA follow-up; the rest is local to the outbox | Reuse the existing outbox pattern for automation and SLA follow-up (one small intent table drained by the existing recurring job, keeping the deterministic keys), and add attempt count and last error to the outbox row. Resolves BE-004 and BE-005, and makes BE-015 a one-line change (store the message id). Not a general event framework. |
| **RC-2. Nothing defines who is a party to a ticket.** The requester, the participants (a bag of addresses with no provenance), message authors and merged tickets' requesters are decided separately by inbound mail, the portal, merge and outbound Cc. | MSG-002, SEC-006, MSG-003, BE-001, SEC-004, BE-002 | Systemic | One rule in one service: a correlated message attaches only from the requester or an existing participant; participants record who added them, are capped and removable; a merge across different requesters is handled by an explicit rule. Resolves MSG-002/SEC-006 and BE-001, and narrows MSG-003 and SEC-004/BE-002 to the same decision. |
| **RC-3. The send outcome crosses the Mail/Ticketing boundary as a status plus a magic string.** Mail has explicit `Uncertain` and bounce handling; Ticketing's delivery state has five values and encodes "uncertain" and "bounced" as `Failed` plus a code string. | MSG-001, BE-009, MSG-007, and the UI-only retry guard | Partly systemic. The MSG-001 defect itself is local (retry keeps the draft id) | Local fix first: a retry of an already-submitted message clears the draft id and is recorded as a deliberate re-send. Then the systemic step: give uncertain and bounced their own states, so the string checks disappear (see ARC-001). |
| **RC-4. Isolation outside the scope-filtered Ticketing set rests on convention, and administrative permissions pass for a Platform Admin without membership.** | SEC-001, SEC-005, SEC-012, QA-007 | Systemic, but small in extent: two endpoints are affected today | State one rule: any field derived from ticket or mail content is returned only to a caller who holds `tickets.read` in that department; administrative permission alone returns configuration only. Fix the two endpoints, and extend the existing endpoint-authorisation test with a non-member Platform Admin probe of every administrative endpoint. Resolves SEC-001, SEC-005 and the structural half of SEC-012 and QA-007. |
| **RC-5. Lifecycle rules decided once and never re-evaluated.** SLA policy chosen at creation before routing; first-response clock independent of status; lifecycle timestamps overwritten on reopen. | BE-006, BE-007, BE-014 | Local to the SLA and ticket-lifecycle code; all three need product decisions, not design work | One working session on three decisions; the code change is then small and in one module. |
| **RC-6. Client-initiated writes at the edges lack replay and staleness protection while the core is strict.** | BE-003, BE-010, QA-004, UX-016 | Local, repeated | Optional client message id as the unique source reference (the mechanism already exists for portal creation); optional per-row versions for bulk. |
| **RC-7. Production hosting is not yet a deliverable.** | SRE-001, 002, 003, 005, 006, 007, 010, 011, SEC-011 | Systemic, outside the application code | The deployment work already planned. |
| **RC-8. Decisions are recorded in dense bullets, not ADRs.** | DOC-001 to DOC-004, ARC-006, ARC-007 | Systemic in process, cheap to fix | Write the missing ADRs (ARC-006). |

**What is local, not systemic:** MSG-001's defect, BE-008 (SLA sort), BE-013, BE-012, BE-011, BE-016, SEC-002, SEC-003, SEC-007 to SEC-010, SEC-013, MSG-004 to MSG-006, MSG-008 to MSG-012, all UX, QA and DOC items. These need a local fix each and no design change.

**Blocker explanation.** Of the blockers outside hosting, MSG-002 traces to RC-2, MSG-001 to RC-3 and SEC-001 to RC-4. MSG-004, SEC-002 and SEC-003 have no architectural cause: they are a missing verification step and decision (who may register a mailbox), the attachment-scanning gate (D-08) and the abuse-controls gate.

## 18.3 Medium (systemic)

### ARC-001: The mail send outcome is encoded as strings across three layers
- **Specialist / category:** Lead Architect / Architecture. **Severity:** Medium. **Confidence:** High.
- **Evidence:** the same literal `outcome_uncertain` is defined three times inside `MailErrors.cs` plus the exception text in `OutboundMail.cs:460`, used as a business predicate in SQL (`QueuePredicates.cs:34`, `DeliveryError == "outcome_uncertain"` to separate "failed" from "uncertain" for the dashboard and the queue filter), and as a UI rule in `Conversation.tsx:66,110` (hide Retry) and `errors.ts:5`. Ticketing's `DeliveryState` has `None, NotDelivered, Pending, Sent, Failed` and a CHECK constraint on those five. A bounce is `Failed` with `ndr_delivery_failed`. The only server-side guard against retrying an uncertain message is incidental: the Mail module's own state check (`state = 'Failed'`) does not match its `Uncertain` row.
- **Impact:** rules that must agree live in a status in one module, a code string in another, a SQL predicate in a third place and the browser. That is the structural reason MSG-001 (bounce retry becomes Sent), BE-009 (no transition rules) and MSG-007 (forged bounce) exist, and a future change to the code string would silently break a dashboard figure and a button.
- **Smallest recommended fix:** fix MSG-001 locally first. Then add explicit `Uncertain` and `Bounced` values to the delivery state (one migration widening the CHECK constraint) and compare on those, not strings. Do not add a new abstraction.
- **Decision required:** No. **Production blocker:** No (MSG-001 is the blocker).
- **Related:** MSG-001, BE-009, MSG-007, QA-002.

### ARC-002: Isolation outside the Ticketing scope filter is by convention, and content can ride on administrative endpoints
- **Category:** Architecture / Authorization. **Severity:** Medium. **Confidence:** High.
- **Evidence:** only ten Ticketing entities carry the department scope query filter (Ticket, Message, Requester, TicketSla, SlaEvent, TicketFieldValue, TicketFollower, TicketLink, TicketParticipant, CannedResponse). Everything else in Ticketing (for example `AutomationExecution`, `SavedView`, request forms), and every query in Mail, Knowledge and Audit, relies on each query repeating `DepartmentId == …`. The architecture tests pin where the scope bypass is called and which endpoints declare which permission, but nothing asserts what an administrative endpoint may return. `PermissionKind.Administrative` grants a Platform Admin access to any department without membership (`PermissionAuthorizationHandler.cs:44-48`), so any administrative endpoint that returns content-derived data (SEC-001, SEC-005) leaks to that role.
- **Impact:** the pattern that caused two confirmed leaks can recur with the next administrative endpoint. Nothing detects it.
- **Smallest recommended fix:** write down the rule (content-derived fields need `tickets.read` by membership; administrative permission returns configuration), fix SEC-001 and SEC-005, and extend `EndpointAuthorizationTests` with one test that signs in as a non-member Platform Admin and calls every GET under an administrative permission, asserting the response contains none of the fields classified as content. No new filter layer for Mail or Knowledge.
- **Decision required:** No (the SEC-012 decision on the Platform Admin boundary comes first). **Production blocker:** No (SEC-001 is the blocker).
- **Related:** SEC-001, SEC-005, SEC-012, QA-007, D-02.

### ARC-003: "Who is a party to a ticket" has no single owner
- **Category:** Architecture / Data Integrity. **Severity:** Medium. **Confidence:** High.
- **Evidence:** the requester is a column; participants are an unattributed set of addresses (`ticket_participants`: address, name, created time, no source and no author); message authors are any requester row of the department (no constraint ties them to the ticket's parties); inbound mail, the portal, merge and outbound Cc each decide independently who counts (`MailBridge.cs:302-311`, `PortalRequestService.cs:224-230`, `TicketMerge.cs`, `TicketService.cs:462-470`). ADR-015 says CC participants get no portal access, but nothing says who may become one.
- **Impact:** the rule exists only as habit, so each new path (portal reply, mail correlation, merge, the planned participant features) can add a party without the others noticing. This produced MSG-002, BE-001 and BE-002, and it will produce the next one.
- **Smallest recommended fix:** one small domain service with three questions (may this sender attach? who may be added as a participant, from what? what happens to parties on merge?), called from the mail bridge, the merge service and the Cc builder, plus provenance on participants. Do not create a participant framework or a roles system.
- **Decision required:** No new decision (settled by the MSG-002 and SEC-004/BE-002 decisions). **Production blocker:** No (MSG-002 is the blocker).
- **Related:** MSG-002, SEC-006, MSG-003, BE-001, SEC-004, BE-002.

### ARC-004: Durable intent is applied to some after-commit work and not to the rest
- **Category:** Architecture / Reliability. **Severity:** Medium. **Confidence:** High.
- **Evidence:** the notification outbox, the mail outbox and the delivery re-report all store intent in the same transaction and are drained or re-reported by a recurring job. Automation dispatch and the SLA monitor's follow-up run after commit with no stored intent (`TicketLifecycle.cs:39-52, 115-127`), and the automation lease only resumes an execution that is delivered again (BE-004). The notification outbox has no attempt tracking or dead-letter state (BE-005). The code comment already warns against turning the outbox into "a general event framework", which is the right instinct and should stay.
- **Impact:** a restart between commit and dispatch silently loses routing or escalation rules; there is no record that anything was lost.
- **Smallest recommended fix:** see RC-1: extend the same pattern (a small intent row in the same transaction, the existing job to drain it, deterministic keys already in place) to automation triggers and SLA follow-up, and add attempts and last error to the outbox row.
- **Decision required:** No new decision (BE-004 holds it). **Production blocker:** No.
- **Related:** BE-004, BE-005, BE-015, SRE-005.

## 18.4 Low (new)

### ARC-005: "Open work" is restated in four other files besides the shared predicate
- **Category:** Maintainability / Reporting. **Confidence:** High. **Evidence:** the shared `QueuePredicates.Backlog` (Open or Pending, not merged) is used by the queue filters and the dashboard only. The same expression is restated in `RequesterHistoryService.cs` (its own `ActiveStatuses` array and four uses), `TicketProjection.cs:56`, `RequesterIdentityAdmin.cs:127`, and `TicketQueryService.cs:144, 319, 330`. **Impact:** a future change (for example whether Pending counts as open) updates some screens and not others, and Historical Reports would add a seventh definition. **Fix:** reuse `QueuePredicates` in those places when they are next touched; do not move code now. **Decision:** No. **Blocker:** No. **Related:** Data/Reporting review (not yet run), BE-007.

### ARC-006: Significant decisions since ADR-015 are recorded in long bullets, not ADRs
- **Category:** Documentation / Architecture governance. **Confidence:** High. **Evidence:** project rules require an ADR for significant changes to shared contracts, auth, permissions or database architecture. Not covered by an ADR: the notification outbox via a `SaveChanges` interceptor; the requester identity-link model and the form audiences (ADR-015 was amended inline with parenthetical notes); presence and mentions (the realtime boundary); the server-side per-user preference table; the Knowledge reply-link contract between two modules; the meaning of administrative permission for Platform Admins (D-02 says "no automatic access", `PERMISSIONS.md` describes the mechanism); help topics. They live in single-line bullets in `TICKETING.md` of several hundred words. **Impact:** reviewers cannot see what was decided, when, or what was rejected; amendments inside an accepted ADR blur history. **Fix:** a short ADR for each of the first four, no more than a page each, written from the existing text. **Decision:** No. **Blocker:** No.

### ARC-007: Cross-schema database coupling and migration order are accepted in practice but not written down or tested
- **Category:** Architecture / Deployment. **Confidence:** High. **Evidence:** Ticketing migrations create foreign keys into `organisation.departments`, `organisation.teams`, `organisation.department_memberships`, `identity.users` and `mail.mailboxes`; Ticketing code inserts into `mail.messages` on its own connection (ADR-013 states this); one connection string and one database role serve every module. The runner orders modules by an integer; my own scratch-database run showed that migrating Ticketing first fails for want of the `organisation` schema. SRE-007 found the documented order lists three of eight modules. **Impact:** "modules own their schema" is true for tables, but not for deployment, restore or rollback: a Down migration of Organisation or Mail cannot run while Ticketing exists, and a single-module restore is not possible. That is acceptable for a modular monolith, as long as it is stated. **Fix:** one paragraph (in the ADR for ARC-006 or in ADR-001) stating one database, one role, hard cross-schema keys, the order and that rollback is by restore; one test that the registered migration orders satisfy the foreign key dependencies. **Decision:** No. **Blocker:** No. **Related:** SRE-007.

### ARC-008: A cross-module contract carries a security precondition only in prose
- **Category:** Architecture / Security (latent). **Confidence:** High. **Evidence:** `IReplyLinkArticles.SearchAsync(departmentId, query)` (Refinement 10) returns every article a *linked requester* of that department could open, for any caller. Its comment says the caller has already established the link; the signature does not require it. Today the single caller (`ReplyLinkService`) does. **Impact:** a future caller that omits the check offers requester-only articles to an unlinked requester. **Fix:** make the precondition part of the call (for example pass the linked user id and have Knowledge apply the audience rule itself), when the contract is next touched. **Decision:** No. **Blocker:** No.

### ARC-009: Maintainability: one very large service and an unchecked client contract
- **Category:** Maintainability. **Confidence:** Medium. **Evidence:** `TicketService.cs` is 999 lines covering creation, update, assignment, messaging, delivery retry, attachments, bulk, activity and projection helpers; `Macros.cs` (738), `MailboxService.cs` (533), `TicketQueryService.cs` (504) and `PortalRequestService.cs` (478) are the next largest. The web client's response shapes are hand-written Zod schemas; `ARCHITECTURE.md` says types are generated from OpenAPI, but no generator exists (DOC-002), and nothing checks a schema against the server DTO. **Impact:** no defect today; new rules (for example the party rule) will be added in whichever file is nearest, and a server field rename is caught only by tests that happen to render it. **Fix:** do not refactor now. Split `TicketService` along existing seams (messages, bulk) only when a change forces it; correct the documentation about generated types, or add a contract test for the highest-traffic DTOs. **Decision:** No. **Blocker:** No.

## 18.5 Questions the Architect had to answer (AGENTS.md section 7)

- **Are modules genuinely separated?** Yes at the code level and enforced by tests; at the database level they share one database, one role and hard foreign keys, deliberately (ARC-007).
- **Are post-commit operations safe?** Safe for notifications and mail; not for automation and SLA follow-up (ARC-004).
- **Is durable work actually durable?** The outbox and mail queue are; the recurring jobs are idempotent. See RC-1 for the gaps.
- **Are transaction boundaries correct?** Yes for ticket changes. Two edges stretch them: the portal reply (message, then attachments, BE-003) and the SLA monitor (commit, then dispatch, BE-004).
- **Are failure and retry semantics explicit?** Mostly. The outbound sender is the model case. Gaps: no attempt tracking on the notification outbox (BE-005), no automatic reconcile of uncertain sends (MSG-008), retry of a submitted message (MSG-001).
- **Are shared predicates used consistently?** For the queue and the dashboard, yes; elsewhere no (ARC-005).
- **Can reporting drift from queue semantics?** Yes, through ARC-005 and the first-response rule (BE-007), unless the definitions are fixed before Historical Reports starts.
- **Duplicate mechanisms?** Several idempotency keys of different shapes (source reference, unique unread notification, automation trigger key, atomic claim), but each fits its job. `ITicketEvents.PublishAsync` also drains the notification outbox, so a method named "publish" has a side effect that a reader would not expect; harmless, worth knowing.
- **Are old abstractions still necessary / are new ones justified?** The mail provider interface is still justified (a simulator and Graph). No new abstraction is recommended in this review.
- **Circular dependencies?** None at project level. Ticketing references Knowledge (it calls `IReplyLinkArticles`) and also implements Knowledge's `IRequesterAudience`; the project reference runs one way, and the interface is implemented in the opposite direction, which is the intended inversion.
- **Reversible migrations?** Each module's migrations have Down methods; the two newest were exercised up/down/up. Cross-module reversal is by restore (ARC-007).
- **Data states the application cannot represent or recover from?** A participant that should be removed (BE-001); a message marked Sent after a bounce (MSG-001); a notification that cannot be processed and stays queued (BE-005).

## 18.6 Could not be verified

- Behaviour with more than one application instance (presence and the hub's connection registry are in memory; there is no backplane): SRE-008 stands, nothing was run.
- That the migration order numbers satisfy every foreign key dependency from a clean database in the production role (only the two newest migrations and one scratch-database cycle were run, for earlier work).
- Performance of the shared predicates on large tables, and the cost of the in-request outbox drain after a long backlog.
- Whether the scope filter's omission on the entities listed in ARC-002 hides any other administrative endpoint that returns content: I checked the administrative endpoints I could identify (mail attention, automation history, requester identities, mailboxes, forms, knowledge, members, audit); the proposed probe test would check them all.


---

# 19. ITSM Product / Service Desk review (added after the Architect section)

Specialist: ITSM Product & Service Desk Lead (AGENTS.md section 8). Baseline: commit `081f660`. Method: single agent, read-only. I judged the product as an internal service desk run by first-line agents, specialist resolvers, a team lead, a department administrator and ordinary employees as requesters, reading the status and queue logic, the composer and message path, assignment and permission rules, SLA behaviour, the portal mapping, macros and automation triggers, the dashboard and the department setup overview. I walked no screen in a browser. Technical findings from the other reviews were reused, not repeated; where they matter to a workflow they are assessed in 19.1.

Classification used: **broken** (behaves wrongly), **confusing** (works but misleads), **safeguard** (a missing guard against a mistake), **enhancement** (not a defect; listed only so it is not mistaken for one). Nothing here proposes an excluded feature (scoring, tags, time tracking, CRM, forwarding, telephony, Team security boundaries).

**Result in one line:** the lifecycle, the conversation model and the dashboard are coherent and compare well with the mature desks the project cites. The weak spots are all about *knowing who must act next*: after an agent answers, the ticket looks untouched; agents cannot hand work on; some tickets can never be answered by email; and the requester-facing words do not say what the requester should do.

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 0 |
| Medium | 3 |
| Low | 4 |
| Polish | 0 |
| **Total** | **7** |

Production blockers from this review: **0** (ITSM-003 should be settled before the portal goes live, see its entry). Decisions required: **5** (ITSM-001, ITSM-002, ITSM-003, ITSM-004, ITSM-005). Four enhancements are listed in 19.4 and are not counted.

## 19.1 Assessment of the specific questions

**BE-006, SLA policy chosen once at creation.** Seen from the desk: the behaviour is coherent only for tickets whose team, type and priority are known when they are created, which in practice means agent-created tickets and portal requests (a request form carries a default team, type and priority). Email is the main channel of most desks, and an email ticket always arrives with no team, the department's default type and Normal priority; routing, classification and priority changes come later, from an automation rule or an agent. So team-, type- and priority-based policies will not apply to most email tickets, and an agent who raises a ticket to Urgent does not get an Urgent SLA. The administrator is offered those conditions with no hint that they only work at creation. Conclusion: a real functional gap, not a preferred workflow. Recommended direction for the decision (not a decision): re-evaluate the policy when team, type or priority changes, with the original start time kept, so that the clock does not restart on re-triage; the alternative is to say plainly in the policy editor which conditions can match which kinds of ticket.

**BE-007, first-response clock on resolved tickets.** Seen from the desk this is not an edge case. A normal service-desk flow is "log and resolve at first contact" (a phone call, a walk-up, an agent who fixes it and records an internal note), and spam and duplicates are closed without a reply. Every such ticket, under a policy with a first-response target, goes on to record a first-response warning, a breach and a `SlaBreached` automation run after it was resolved, and any future attainment report would count it as a miss. Conclusion: a real defect in expected SLA semantics. The three coherent behaviours are: the clock stops when the ticket is resolved or closed and counts as not applicable; it stops and counts as met at that moment; or a reply is required (today). The first is the usual desk convention. The choice is the owner's.

**MSG-002 / BE-001, participants.** Normal desk behaviour: people the requester copies become participants and get the replies; the agent can see who is on the Cc line, add a colleague and remove someone; a stranger cannot join by sending mail. Today people can join from any sender who holds a thread identifier, nobody can see the list by name when replying, and nobody can remove anyone. Conclusion: the participant model is missing its safeguards, which is why it is part of the MSG-002 blocker. No participant feature beyond sender rule, visibility and removal is needed.

**SEC-004 / BE-002, merging different requesters.** Seen from the desk, merging tickets of different people is legitimate and common: one outage reported by twelve users, or one person writing from two addresses. Blocking it would hurt. The mature convention is to keep one working ticket and to treat the other requesters as participants who receive the answers. The current product does the first half only: the merged requesters' tickets leave their portals and they stop receiving replies. Conclusion: the right outcome is "merge allowed, source requester becomes a participant (and sees the canonical request)"; the decision is the owner's (BE-002 lists the options).

**Are status, type, priority and team semantics coherent?** Yes, apart from BE-006. Open means in progress; Pending means waiting for the requester or a third party and pauses the resolution clock only where the policy says so; Resolved means solved and still reopenable; Closed is final for agents (no public reply without reopening) but any late requester mail reopens it by an owner decision (D-15), so Closed means "no further work expected", not "locked". Priority is the four-level ladder without an impact and urgency matrix, which is the right size. Type classifies and shapes the fields. Teams are routing groups, not a boundary. One wrinkle: "Open" does not tell an agent who must act (ITSM-001).

**Can a workflow leave a ticket misleading or operationally impossible?** Four cases found: answered but still Open and possibly Unassigned (ITSM-001); a ticket with no stored mailbox that can never be answered by email (ITSM-003); resolved with a first-response breach recorded afterwards (BE-007); the merged requester's request disappearing (BE-002). Cleanly handled cases: a member removed from the department has their assignments cleared by the database so their tickets return to Unassigned; a deactivated mailbox blocks replies visibly and never switches sender; a merged ticket is read-only and out of queues.

**Do the queue and dashboard expose the work an agent or team lead needs?** Largely yes: My, Team, Unassigned, All and Following queues with saved views, default view, columns and quick filters (awaiting first response, failed or uncertain sends, no team, type, SLA states); the dashboard shows open and pending, unassigned, SLA overdue and due soon, awaiting first human response, ageing, team workload including "No team", the oldest tickets and mail send problems, every figure linking to the queue it counts. The gap is the one that matters most to a lead after the first reply: "the requester answered and we have not responded" has no view or figure of its own except through the status (ITSM-001). Stale Pending tickets (waiting a long time) are not visible either (enhancement E-3).

**Do acknowledgements, notifications and requester-visible behaviour match normal expectations?** Acknowledgements are per department and per channel, sent from the ticket's mailbox with no agent signature, and are not treated as a human response: this matches mature desks. Requester emails are sent when an agent replies; nothing is sent when a ticket is resolved or closed without a reply (ITSM-005). Internal notifications cover assignment, requester reply, followed update and mention; they do not cover a new ticket in a team's queue or an SLA warning (enhancements E-1, E-2, already in the backlog). In the portal a "Waiting" status does not say whose turn it is (ITSM-004).

## 19.2 Medium

### ITSM-001: After an agent answers, the ticket looks untouched
- **Specialist / category:** ITSM / ITSM Workflow. **Classification:** confusing workflow (misleading state). **Severity:** Medium. **Confidence:** High (code and docs).
- **Evidence:** the composer's "Set status after" defaults to "Keep" (`Composer.tsx`), so a public reply leaves the ticket Open unless the agent chooses Pending. The automation triggers are ticket created, requester message, status, priority, team, assignee, SLA approaching and SLA breached (`RuleModel.cs:8-33`): there is no agent-reply trigger, so a department cannot automate "set Pending after a reply". An agent's public reply does not assign the ticket (`TicketService.PostMessageAsync`). The queue list has no last-speaker indicator, and "awaiting first response" covers only the first reply. A requester reply returns the ticket to Open (documented), but an Open ticket may just as well be one the agent has already answered, so Open does not say whose turn it is.
- **Impact / scenario:** an agent answers a new, unassigned ticket and moves on. It is still Open, still Unassigned, indistinguishable in the queue from one nobody has looked at (apart from the SLA badge); a colleague takes it and answers again, or the lead sees a long "Open and Unassigned" list that is actually answered. For every later exchange, an Open ticket may be "waiting for us" or "waiting for them" and the queue cannot say which.
- **Smallest recommended fix:** the owner's choice among: a per-department default for "status after a public reply" (for example Pending), an agent-reply automation trigger, or a last-speaker marker in the queue row. Do not add a new status.
- **Decision required:** Yes. **Production blocker:** No.
- **Related:** BE-007, UX (queue columns).

### ITSM-002: Agents cannot hand a ticket to another team or colleague, and macros that do are invisible to them
- **Classification:** confusing workflow / missing safeguard. **Severity:** Medium. **Confidence:** High.
- **Evidence:** the Agent role holds `tickets.assign.self` only; `tickets.assign.any` belongs to Team Lead and Department Admin (`PermissionCatalogue.cs`, role grants). With self-assignment an agent may claim an unassigned ticket or release one they hold, leaving the team unchanged (`TicketService.RequireAssignmentRightsAsync`); changing the team or choosing another assignee needs assign.any. A macro whose actions need assignment is filtered out of the macro picker for anyone without the right (`Macros.cs`, `Needs(...).All(allowed.Contains)`), so it is not offered and not explained.
- **Impact / scenario:** first-line escalation to a specialist team, the most common desk action after "reply", needs a team lead every time. An administrator who builds an "Escalate to Network" macro finds that agents never see it. The only handoff open to an agent is an internal note with an @mention, which tells a colleague but does not route the ticket.
- **Smallest recommended fix:** the owner decides whether Agents may change the team (routing only, with assignee changes staying with leads) or whether the macro picker should show unavailable macros as disabled with a reason.
- **Decision required:** Yes (this changes the permission model). **Production blocker:** No.
- **Related:** UX (macro picker), PERMISSIONS.md.

### ITSM-003: A ticket without a stored mailbox can never be answered by email, and nothing repairs it
- **Classification:** broken (operationally impossible state, by an earlier locked decision). **Severity:** Medium. **Confidence:** High.
- **Evidence:** the department default mailbox is chosen only when a ticket is created (`PortalRequestService.cs:369`); if the department has no default, or has several active mailboxes and none marked default, the portal ticket is stored with no mailbox. Public replies on such a ticket are recorded as not delivered (`no_ticket_mailbox`, `TicketService.cs:399`) and never fall back (locked rule). The project rules state that a human must assign a valid mailbox first and ask before designing that workflow; no such action exists. Portal requests are accepted whether or not a mailbox exists. The department setup overview does warn ("No mailbox", "No default mailbox").
- **Impact / scenario:** the portal is switched on a week before the shared mailbox is connected, or an administrator removes the default. Every request created meanwhile can be answered in the portal only; the requester gets no email, the agent sees "not delivered", and connecting the mailbox later does not help those tickets. In a department that uses the portal only, ticket replies are never emailed by design, so requesters must be told to look at the portal.
- **Smallest recommended fix:** a decision first: either refuse portal submissions (or disable the form) until the department has a usable default mailbox, or design the explicit "assign a mailbox to this ticket" action for an authorised user that the rules already anticipate. Until then, make the rollout order explicit in the setup guide: mailbox first, forms second.
- **Decision required:** Yes. **Production blocker:** No, but settle it before the portal is opened to requesters.
- **Related:** DOC-010, UX-005.

## 19.3 Low

### ITSM-004: The portal status "Waiting" does not say whose turn it is
- **Classification:** confusing workflow (requester experience). **Confidence:** High. **Evidence:** Pending is defined as "waiting for the requester, a third party or another dependency" (`TICKETING.md`), and the portal maps it to the single label "Waiting" (`PortalRequestService.cs:23-37`); Open is "In progress". **Impact:** a requester who sees "Waiting" cannot tell whether they must reply or whether support is waiting on someone else; they either do not answer or chase unnecessarily. **Fix:** decide whether Pending always means "waiting for you" in requester language, or give the requester a plain line of explanation (for example "We are waiting for more information" versus "We are waiting on another team"). **Decision required:** Yes (wording and meaning). **Blocker:** No.

### ITSM-005: Resolving or closing without a public message tells the requester nothing, and nothing ever closes a resolved ticket
- **Classification:** safeguard (partly a known enhancement). **Confidence:** High. **Evidence:** a status change made without a reply sends no mail to anyone (only an agent public reply, or an acknowledgement at creation, is emailed); there are no timers: no auto-close and no reminder for Pending (BACKLOG lists auto-close timers). **Impact:** an agent who changes the status to Resolved from the properties rail leaves the requester unaware, and Resolved tickets remain Resolved until someone closes them or the requester writes back (which reopens them, by decision D-15). **Fix:** at minimum prompt the agent when resolving without a public reply, with the choice to send one now. Timers stay in the backlog. **Decision required:** Yes (whether to prompt, and whether silent resolution is acceptable). **Blocker:** No.
- **Related:** BE-007.

### ITSM-006: The department setup overview can say "1 active form" when no requester can use it
- **Classification:** confusing workflow (admin). **Confidence:** High. **Evidence:** a new request form is created active and visible but restricted to "Selected people" with nobody selected (`FormsAdmin.cs:63-80`), and the overview counts active forms only (`DepartmentOverview.tsx`, "N active form(s)"); the warning "Requesters cannot raise tickets through the portal" appears only when there is no active form. The overview also has no row for acknowledgements, signatures, help topics or Knowledge. **Impact:** an administrator reads the overview as "portal ready" while the catalogue is empty for everyone. **Fix:** count forms that at least one requester can use, and add the acknowledgement setting to the overview. **Decision required:** No. **Blocker:** No.

### ITSM-007: New email tickets are unrouted and only an administrator-built rule routes them
- **Classification:** confusing workflow. **Confidence:** Medium. **Evidence:** a mailbox has no default team (only request forms do); an email ticket is created with no team and no assignee, so it appears in "Unassigned", "All" and the "No team" filter, not in anyone's Team queue. Routing needs an automation rule on ticket creation, which runs after commit (BE-004) and, for SLA, after the policy was chosen (BE-006). **Impact:** a department that relies on the Team queue sees new mail only when someone looks at Unassigned. The dashboard's Unassigned figure and the "No team" row mitigate this. **Fix:** none required; if it proves a problem, the smallest remedy is a default team on the mailbox (enhancement E-4). **Decision required:** No. **Blocker:** No.

## 19.4 Enhancements (not defects; listed so they are not mistaken for defects)

- **E-1. Notify a team when an unassigned ticket arrives in its queue.** Today only the assignee, followers and mentioned colleagues are notified. Listed in the backlog as a notification channel item.
- **E-2. Notify the assignee or lead of an SLA warning or breach.** Warnings and breaches are visible in the queue and the dashboard and can run an automation, but nobody is notified. Backlog item (Phase 5).
- **E-3. A view or dashboard figure for Pending tickets that have waited a long time.** The queue's age and the dashboard's ageing help, but waiting time per status is not shown.
- **E-4. A default team on a mailbox,** so that email arrives already routed (see ITSM-007).

## 19.5 Workflow strengths (do not disturb)

- **Lifecycle:** four statuses with defined meaning, timestamps that follow status, atomic "reply and set status", reopen on requester reply with team and assignee kept, closed tickets protected from agent replies, merged tickets read-only and out of the queues.
- **One conversation:** email and portal replies are one thread; public replies and Internal Notes are visually and technically separate; Internal Notes never reach the requester.
- **Truthful delivery states:** the reply is shown as "accepted for delivery, not confirmed as delivered", with clear failed and uncertain states, an attention list, and no silent change of sender.
- **Agent collaboration:** followers, @mentions limited to people who can read the note, in-app notifications, presence and a collision hint that never blocks.
- **Work organisation:** My, Team, Unassigned, All and Following queues, saved views, default view, column choice, quick filters, search that includes message text but never hidden notes.
- **Dashboard:** current-state figures that equal the queues they link to, no per-agent ranking.
- **SLA:** business-hours calendars with holidays, pause on Pending by policy, new resolution cycle on reopen, first response measured by a human public reply (an acknowledgement does not count).
- **Requester side:** request forms with explicit audiences and plain names, help topics ahead of forms, status in requester words, My Requests with live updates, article suggestions, Knowledge with owner and review date, requester-safe article links.
- **Administration:** grouped department shell, a real overview with setup warnings, safe defaults (new forms restricted, mailbox sender never switched), audit of configuration changes, mailbox health.
- **Merge and links:** same-department only, preserved provenance, related, duplicate and parent links, follower transfer.
- **Macros:** manual actions, a public-reply macro only prepares a draft, never sends, never exceeds the caller's permissions.

## 19.6 Could not be verified

- How agents actually use the status-after-reply control and the queues day to day; findings ITSM-001, ITSM-002 and ITSM-007 are judged from the design and the code, not from observed use or from the DEMO screens.
- The rules an administrator actually configures (the DEMO department has no automation rules), so the effect of BE-006 and ITSM-007 on a real configuration is reasoned, not measured.
- Real email behaviour (Graph, threading, bounces): see section 16; nothing here depends on it except ITSM-003, which depends only on the stored mailbox.
- Requester comprehension of the portal wording (ITSM-004); that needs a few real users.
- Volumes: whether the queues and dashboard stay usable with thousands of open tickets.


---

# 20. Data / Reporting review (added after the ITSM section)

Specialist: Data / Reporting Engineer (AGENTS.md section 15). Baseline: commit `081f660`. Method: single agent, read-only, scoped to reporting and data semantics. I read the dashboard and the queue-count code, the shared predicates, the SLA engine's stored facts, the audit stream and its constraints, the ticket, message and mail state columns, and every code path that changes a ticket's status or SLA, to see which facts exist today, which are overwritten, and which a truthful report could be built on. I did not design reports, run queries or touch data. Findings from the other reviews are cross-referenced, not repeated.

Locked rules applied: the dashboard answers "what needs attention now" and reports will answer "what changed over time"; created, resolved and current-state counts stay separate; reopened volume is separate; first-response and resolution attainment are reported separately; ageing and backlog trends must be truthful; UK-facing date boundaries are Europe/London; no history is fabricated from current state; no per-person figures.

**Result in one line:** the product captures far more history than it shows. Messages are immutable and timestamped, every status, priority, team, assignee, type and merge change is written to an append-only audit stream with previous and next values, and every SLA transition is stored as an event with its cycle. A truthful historical report can be built from those without invented snapshots. What stands in the way is not missing capture but a handful of definitions that are wrong or undecided today (the headline figures agents see already disagree), and one dependency: the audit stream is the report's source of truth, yet it is also the place the undecided retention policy (D-09) would have to touch.

| Severity | Count |
|---|---|
| Critical | 0 |
| High | 0 |
| Medium | 3 |
| Low | 6 |
| Polish | 0 |
| **Total** | **9** |

Production blockers: **0**. Decisions required: **4** (DATA-001, DATA-002, DATA-003, DATA-005).

## 20.1 What can already support truthful reports

| Fact | Where it is kept | Quality |
|---|---|---|
| Ticket created time, source (agent, email, portal), department, requester, mailbox, request form | `tickets` columns, immutable | Complete |
| Status, priority, team, assignee, type, subject over time | audit stream (`ticket.created`, `ticket.status_changed`, `…priority…`, `…team…`, `…assignee…`, `…type…`) with previous and next, time and provenance | Complete: all seven status-changing code paths (agent update, reply with status, bulk, macro, automation, inbound reopen, portal reopen) write an event |
| Initial values | `ticket.created` event (agent, portal) and the ticket row; for email the event omits the type, but the initial type is the previous value of the first type change, or the current type if it never changed | Reconstructable (DATA-004) |
| First human response | first agent public message: `messages` are append-only with created time and author type; acknowledgements are not ticket messages | Complete for every ticket, with or without an SLA |
| Who spoke last, response backlog over time | latest public message per ticket at any past time | Complete (see ITSM-001) |
| Resolution, reopen | audit status events (reason `requester_reply` for reopen by mail or portal), plus SLA `Reopened` event with the previous met time and cycle number | Complete; the `resolved_at` and `closed_at` columns are current values only (DATA-006) |
| SLA applied, warned, breached, met (late or on time), paused, resumed, stopped on merge | `sla_events` with clock, cycle, time and detail; the policy and calendar are frozen in the ticket's snapshot | Complete for tickets that matched a policy; none for tickets that did not |
| Merge | `merged_into_ticket_id`, `merged_at`, `merged_by`, audit `merged_into` and `merged_in` | Complete |
| Outbound mail outcomes | mail audit events (sent, failed, uncertain, reconciled, delivery failure reported) plus the mail row's current state, attempts and queue time | Complete as events; the row holds the current state only |
| Inbound mail volume and ignored reasons | `mail.messages` rows with state, ignore reason and time | Complete |
| Automation | `automation_executions` with trigger, outcome and timing | Complete |

Nothing is deleted anywhere in the ticket domain today (no ticket, message or event deletion exists), so reports will not meet missing rows until a retention policy is applied.

## 20.2 Assessment of the findings that affect reporting

| Finding | Effect on reporting | Conclusion |
|---|---|---|
| BE-006 SLA chosen at creation | Attainment is measured only for tickets that matched a policy at creation; email tickets routed or classified later often match none or the default | Attainment must always be shown with coverage ("N of M tickets had an SLA") and never as a headline percentage of all tickets. The semantics decision (re-evaluate or document) should come before reports, because re-evaluation changes what the stored events mean. |
| BE-007 first-response breach after resolution | Breach events can be recorded after the ticket is resolved or closed, and would count as misses | The raw events are truthful but their meaning is undecided. A report can already tell them apart by comparing the event time with the status history, but this should be decided once, at the source, not re-derived in every report. |
| BE-008 SLA sort | None (a sort option, not a metric) | No reporting impact. |
| ITSM-001 answered versus needs response | "Needs a response" can be derived exactly from the message stream, now or historically | Data is sufficient; the definition just needs deciding and sharing with the queue. |
| Merged tickets | The merge flag is separate from status, so a merged source keeps its old status (often Open) | Every count must go through the shared definition or exclude merged sources explicitly; a plain status count over-counts (DATA-005). |
| Failed and uncertain mail | "Uncertain" and "bounced" are a failure code in text, not states (ARC-001) | Delivery-outcome reports are possible from the mail audit events, but should wait until those states are explicit, or they will break with that change. |

## 20.3 Medium

### DATA-001: The sidebar queue counts do not use the same definitions as the queue list or the dashboard
- **Specialist / category:** Data / Reporting. **Severity:** Medium. **Confidence:** High (code).
- **Evidence:** `QueueCountsAsync` (`TicketQueryService.cs:311-333`) counts tickets with status **Open only** for My, Team, Unassigned, All and Following; the queue list's default is Open **and** Pending (`QueuePane`/`parseFilters`), and the dashboard's backlog is Open and Pending. The two SLA badges count `ticket_sla` rows whose deadline has passed or is approaching with **no condition on the ticket's status or merge flag**, whereas the queue's SLA filters and the dashboard use the backlog (Open and Pending, not merged). A resolved or closed ticket whose first-response clock never stopped (BE-007) is counted in the badge and absent from the list and the dashboard. The existing dashboard test proves the dashboard equals the queue filters; there is no test for the sidebar counts.
- **Impact:** an agent sees "SLA breached 12" in the sidebar, opens it, finds fewer rows, and the dashboard shows a third number. The same happens with "My queue 5" against a list of 8. The numbers that agents look at first are the ones that disagree, and a later report would be a fourth source.
- **Smallest recommended fix:** make the badges use the same predicates as the list and the dashboard (`QueuePredicates`), and decide whether Pending counts as open work in the badges. Add one test that compares each badge with the list it opens.
- **Decision required:** Yes: whether Pending tickets belong in the sidebar counts (Open only is a legitimate "needs us" reading, but then the list's default and the dashboard should say so). The SLA-badge inconsistency is a plain bug. **Production blocker:** No.
- **Related:** BE-007, ARC-005, ITSM-001.

### DATA-002: "Awaiting first response" is silently an SLA figure
- **Category:** Reporting. **Severity:** Medium. **Confidence:** High.
- **Evidence:** `QueuePredicates.AwaitingFirstResponse` is true only when the ticket's SLA row has a first-response target that is not yet met (`QueuePredicates.cs:29-30`); the dashboard and the queue filter use it, and the label says nothing about SLA.
- **Impact:** in a department with no SLA policy, or for tickets that matched no policy or a policy without a first-response target (BE-006), the figure is zero however many tickets have never been answered by a person. An administrator or lead reads "0 awaiting first response" as "everything is answered". The same tickets would still be unanswered by the message stream.
- **Smallest recommended fix:** define the figure from the message stream (no public agent message yet), independent of SLA configuration, and keep the SLA-specific view under its own name; or rename the existing one "SLA first response pending". Both the queue filter and the dashboard use one predicate, so one change covers both.
- **Decision required:** Yes (which definition the label should mean). **Production blocker:** No.
- **Related:** BE-006, ITSM-001.

### DATA-003: The audit stream is both the source of truth for history and the place retention would have to change
- **Category:** Reporting / Privacy. **Severity:** Medium. **Confidence:** High for the mechanism; the policy itself is open (D-09).
- **Evidence:** `audit.audit_events` is append-only by database triggers that reject update, delete and truncate (`AuditDbContext` migration `Initial`). Ticket events carry the lifecycle facts reports need (content-free ids and enumerations) but also personal data: the actor's display name on every event and the full old and new **subject text** on subject changes. D-09 (retention, export, anonymisation, legal hold) is unresolved; no purge exists, and growth is unmanaged (SRE-011).
- **Impact:** any future erasure or anonymisation will collide with the triggers, and if audit rows are deleted or rewritten to meet it, every historical report built on them changes or loses periods without warning. Equally, if reports read the stream with its personal data, a report extract inherits that data. Individual-level facts (actor ids) also exist in the same rows, which a report must never group by.
- **Smallest recommended fix:** before building reports, decide which facts are retained for reporting (content-free lifecycle facts: ticket, department, kind, previous and next values, time) separately from personal data (actor names, subject text), and state how anonymisation will treat the second group while keeping the first. Do not add a data warehouse; a view or a small content-free event table filled by the same audit write is enough if separation is needed.
- **Decision required:** Yes (belongs with D-09). **Production blocker:** No (the retention policy itself is already a listed gate).
- **Related:** SRE-011, D-09, MSG-011.

## 20.4 Low

### DATA-004: The three ticket-creation audit events have different shapes
- **Confidence:** High. **Evidence:** the agent event holds status, priority, team, assignee and type (no source); the portal event holds status, priority, team, source, form and type (no assignee); the email event holds status, priority, team, assignee, source and mailbox (no type) (`TicketService.cs:180-189`, `PortalRequestService.cs:404`, `MailBridge.cs:185-188`). **Impact:** a report that replays history must special-case each source; the initial type of an email ticket is only recoverable indirectly. **Fix:** one common payload for all three (source, mailbox, form, type, team, assignee, status, priority). **Decision:** No. **Blocker:** No.

### DATA-005: A merged ticket keeps its status and is easy to count by mistake
- **Confidence:** High. **Evidence:** merging sets only the merge columns; the source stays Open, Pending, Resolved or Closed (`TicketMerge.cs`). The shared backlog predicate excludes merged tickets, but the requester profile's "Total" counts them (`RequesterHistoryService.cs:44-56`), and four other places restate the open-work rule (ARC-005). **Impact:** a duplicate that was merged away would count as a created ticket, an open ticket or a resolved ticket in any report that does not exclude it, and as demand twice. **Fix:** one shared definition for reports ("counted tickets"), and a rule for created counts. **Decision required:** Yes: are merged duplicates counted in "created during the period" (they were raised) or excluded (they are not separate demand)? Either is defensible, it must be stated once. **Blocker:** No. **Related:** BE-002, ARC-005.

### DATA-006: Lifecycle columns hold the current value only
- **Confidence:** High. **Evidence:** `Ticket.ApplyStatus` clears `resolved_at` and `closed_at` on reopen and resets `resolved_at` when a closed ticket becomes resolved; `ticket_sla` holds only the current resolution cycle (earlier cycles are in `sla_events`); the requester's name and address are rewritten by later mail and portal use. **Impact:** "resolved during the period" taken from the column misses tickets that were resolved and later reopened, and counts a re-resolved ticket at its last resolution. Counting reopens needs the events. **Fix:** document that the audit and SLA events are the sources for history and the columns are for the current state only. **Decision:** No. **Blocker:** No. **Related:** BE-014.

### DATA-007: Two ideas of "about to breach", and an age that mixes waiting on us with waiting on the requester
- **Confidence:** High. **Evidence:** the SLA "approaching" state uses each policy's own warning lead in minutes (`SlaEngine.RecordDue`); the dashboard's and the queue's "due soon" use a fixed four hours of wall-clock time (`QueuePredicates.DueSoonWindow`). The dashboard's age buckets and "oldest" use the ticket's creation time for Open and Pending alike. **Impact:** the same ticket can be "approaching" in one place and not "due soon" in another, and a ticket waiting a week for the requester ages like one nobody has touched. Truthful, but unlabelled. **Fix:** label the two concepts, and when age trends are built, split them by status. **Decision:** No. **Blocker:** No.

### DATA-008: Some facts are not captured at all and cannot be rebuilt later
- **Confidence:** High. **Evidence:** mailbox synchronisation outcomes are kept as the latest state only (last sync time, current error, consecutive failures are overwritten); article views are not recorded and an article's helpful or not-helpful vote is the latest answer per person; nothing records why a ticket received no SLA. **Impact:** history of mailbox outages, Knowledge usage over time and SLA coverage reasons cannot be produced after the fact. None of these is needed for ticket reports; they matter only if such reports are wanted. **Fix:** decide now whether any of them is wanted, because capture cannot be added retroactively. **Decision:** No. **Blocker:** No.

### DATA-009: One person can be several requesters
- **Confidence:** High. **Evidence:** requesters are per department and per address; only a linked account joins rows (`RequesterPeople`); unlinked senders group by address among themselves. **Impact:** "distinct requesters" counts are approximate (one person using two addresses counts twice, a linked account's many rows count once), and "repeat requesters" cannot be exact. **Fix:** label such counts as approximate, or restrict them to linked accounts. **Decision:** No. **Blocker:** No. **Related:** requester identity decisions.

## 20.5 Questions answered

- **Dashboard metric definitions and queue consistency:** dashboard and queue filters share one predicate set and are tested to agree; the sidebar badges do not (DATA-001); "awaiting first response" depends on SLA configuration (DATA-002).
- **First-response and resolution semantics:** first response is a human public reply (acknowledgements and notes do not count); resolution is Resolved or Closed with a new cycle on reopen; both are separate clocks with separate events. The undecided parts are BE-007 and BE-006.
- **Reopened tickets:** recorded as events with a reason and an SLA cycle; the columns are overwritten (DATA-006). Reopened volume must be counted from events, separately from created and resolved.
- **Created, resolved and current-state counts:** all three are derivable and distinct, as long as merged sources are handled (DATA-005) and the history comes from events.
- **Backlog and ageing, truthfully over time:** exactly reconstructable by replaying the status, merge and creation events; a daily snapshot is not needed and must not be invented from current state. Age trends should separate Open from Pending (DATA-007).
- **Team, source and type breakdowns:** source is immutable; team and type at a point in time come from the events, not the current column; names are current names (a renamed team shows its new name).
- **Failed and uncertain delivery metrics:** possible from mail audit events; wait for explicit uncertain and bounced states (ARC-001). Dashboard figures count tickets with a currently failed message, which is correct for "now".
- **Mailbox health:** current state only; no history (DATA-008).
- **Merge effects:** DATA-005; also SLA of a merged source is stopped and marked, so it falls out of attainment denominators as long as the report excludes `Stopped (merged)`.
- **Requester identity effects:** DATA-009.
- **Time zones and date boundaries:** every instant is stored as an absolute time; the dashboard uses none; SLA calendars carry their own zone and handle clock changes. Period boundaries for reports (day, week, month) must be computed in Europe/London at query time; no reporting time-zone setting exists yet and none is needed beyond that rule.
- **Pagination and filter accuracy:** lists use keyset paging on mutable keys (a ticket updated between pages can move), which is acceptable for work queues but wrong for reports; reports must aggregate in the database, not by paging lists.
- **Can existing data support truthful future reports without fabricated snapshots?** Yes, for ticket volume, backlog, ageing, source, team, type, first response, resolution, reopen, SLA attainment (with coverage) and delivery outcomes. The conditions are the decisions below and the retention approach (DATA-003).
- **Individual measures:** the stream records actor ids on every event. The reports must not use them; no report should offer an actor dimension.

## 20.6 Data gaps and decisions to settle before Historical Reports

1. **Retention and audit separation** (DATA-003, D-09).
2. **First-response clock after resolution** (BE-007) and **SLA selection** (BE-006): decide, then freeze, because they change the meaning of stored events.
3. **Merged duplicates in counts** (DATA-005).
4. **One definition of "counted ticket", "open work" and "awaiting first response"** shared by the sidebar, the list, the dashboard and reports (DATA-001, DATA-002, ARC-005).
5. **Explicit uncertain and bounced delivery states** (ARC-001) before delivery-outcome reports.
6. **One creation-event payload** (DATA-004), a minor tidy that makes replay simpler.
7. **Whether any uncaptured history is wanted** (DATA-008), because it cannot be added retroactively.

Nothing here requires a new data store, a warehouse, or stored snapshots.

## 20.7 Could not be verified

- No query was run, so the claim that history can be rebuilt exactly from events rests on reading every status-changing code path; unusual historical data from earlier phases (before an event type existed) was not examined.
- Performance of replaying events at scale, and the audit table's growth, were not measured (SRE-011).
- Whether audit rows exist for tickets created before each event type was introduced was not checked; a database check on the real data should precede any report.
- The exact content of the DEMO department's SLA and automation set-up (it has no automation rules), so BE-006's effect on real coverage is reasoned, not measured.
- The behaviour of the sidebar badges at runtime was not exercised; DATA-001 rests on the code.

---

# 21. Documentation, Frontend, SRE and QA findings (evidence from the first four reviews)

The first four specialists reported before the others. Their findings, evidence and limits are kept here unchanged except for the section titles. The consolidated summary is in sections 1 to 14.

## 21.1 High findings (SRE)

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

## 21.2 Medium findings (SRE, UX, DOC, QA)

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

## 21.3 Low findings (SRE, UX, DOC, QA)

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

## 21.4 Polish / copy / visual findings (UX, DOC, QA)

- **UX-017:** Dashboard subtitle exposes roadmap wording ("Not a history: reports come later") in `pages/DashboardPage.tsx`. Remove the sentence.
- **UX-018:** Dead realtime foreign-change code (`lib/realtime.ts` `useRemoteChange`/`clearRemoteChange` have no non-test callers; the module-level `remote` map grows for the session). **Decision required:** remove, or wire an "updated by someone else" notice?
- **UX-019:** Small items: breadcrumb in `DepartmentPage.tsx` where "Administration" and "Departments" both link to `/admin`; unnamed trailing `<th />` in `KnowledgeSection.tsx` and `MailboxesSection.tsx`; Knowledge title search is not debounced; every failed message renders `role=alert` in `Conversation.tsx` with no live region for new messages; `.notify-panel` hard-coded at `left:190px; top:5.25rem`; no loading state in `NotificationBell`; spacing typo `defaultStatuses =mode` in `QueueFilters.tsx`.
- **DOC-007:** `MembersSection.tsx` shows roles as "DepartmentAdmin" / "TeamLead" (should read as names).
- **DOC-009:** Server 400 messages read "q: q must be at least 2 characters." (not verified at runtime).
- **DOC-010:** The `CLAUDE.md` collaboration "planned order" is already built, and `MVP.md` has no historical marker; the mailbox-assignment workflow question exists only in `CLAUDE.md`. **Decision required:** register it as a row in `OPEN_DECISIONS.md`?
- **DOC-011:** Documentation mixes "authorization" and "authorised" (pick one spelling convention).
- **QA-008:** Bulk edit on a merged source is untested; e2e has `retries: 0` and a fixed `waitForTimeout` at `portal.spec.ts:273`.


## 21.5 Test gaps noted by QA, UX and SRE (carried into section 9)

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


## 21.6 Strengths noted by the first four reviews (carried into section 13)

- **Dialogs and focus:** `Dialog.tsx` Esc handling, focus trap, autofocus hook, opener restore (UX).
- **Composer:** mode clarity (words, icon, banner, never colour alone), per-ticket drafts, collision hint (UX).
- **Status semantics in the UI:** status, priority, SLA and health marks are glyph plus text; light/dark tokens have decent contrast; a global focus-visible ring exists (UX).
- **Caching and concurrency in the UI:** `useApplyTicket` cache handling, version tokens on edits, mutation buttons disabled while pending (UX).
- **Queue:** table semantics, URL-held filters, bulk selection cleared on filter change (UX).
- **Realtime stance:** presence and realtime are advisory only; REST is the source of truth (UX).
- **Portal:** idempotent `submissionId`, attachment partial-failure message, skip link; two-step requester-link confirmation; uncertain-send duplicate warning (UX).
- **Copy:** portal copy consistently says "request", never "ticket"; "Internal note" naming is consistent; British spelling holds in user-visible strings; non-disclosing 404 wording is accurate; mailbox-unavailable delivery copy matches the locked rules; empty states are actionable; `OPEN_DECISIONS` matches the known gates (DOC).
- **Operations basics:** environment guards on dev sign-in and the Fake provider; Secure/HttpOnly/SameSite cookies; HSTS; read-only Hangfire dashboard limited to platform admins; migrations off at startup by default with an explicit `migrate` command; non-root image; loopback-bound dev ports; secrets kept out of appsettings; separate live and ready probes (SRE).
- **Test foundation:** see section 9.


## 21.7 Limits of the first four reviews (carried into section 14)

- DOC: grep-based sample. Did not read the API docs, threat models, reports, ADR bodies, `UX.md` or `AGENTS.md` line by line; no runtime checks.
- UX: static reading only, no browser, Playwright or Vitest; layout points are estimates; Automations, RuleEditors, Forms, TicketFields, TicketTypes, SLA, BusinessHours, Macros, Canned, TicketProperties/Followers, RequesterDrawer, TeamPage, ProfilePage, markdown/RichText, PortalKnowledge, StatusPage and DepartmentsPage were only skimmed or grep-checked; contrast judged from token values; server-side behaviours (version conflicts, bulk authorisation, idempotency scope) were not verified.
- SRE: no Docker build or run, no restore drill, no live DB/container; ForwardedHeaders and EF advisory-lock behaviour unexercised.
- QA: about 60 test files assessed by name inventory and grep, not read in depth; nothing was run; mail protocol and Fake-provider versus Graph fidelity not audited. Only QA-001 is a code-proven product defect; QA-002 to QA-004 are evidence gaps, not demonstrated bugs.
- Not reviewed by anyone at that stage: the Refinement 9 and 10 backend code (queue preferences, queue filters, knowledge owner/review date, reply-link endpoint). Update: the later Security, Backend and Architect reviews covered the points they raised; see section 14.


