# Post-Refinement-10 Master Report

The single document for the state of the Helpdesk platform after Refinement 10 (commit `081f660`).

**Source and method.** This report is compiled only from the consolidated specialist review, `docs/reports/POST-REFINEMENT-10-REVIEW.md` (the "source report"). Nothing in it comes from a fresh inspection of the repository, and no new findings were created. Where the source report did not establish something (for example an advantage or disadvantage of a decision option), this report says so instead of filling the gap.

**What this report is not.** It is not a compliance statement and not a claim of production readiness. It does not fix anything.

**How the sections fit.** Sections 1 and 2 give the overall picture and the limits of the review. Sections 3 to 6 are the registers (findings, blockers, gates, decisions). Sections 7 to 9 explain why the problems cluster and what to do about them. Sections 10 to 15 summarise each discipline. Sections 16 and 17 record what is strong and the final assessment.

---

# 1. Executive Summary

## 1.1 Overall state of the product

Helpdesk is a coherent, carefully built internal service desk. The module structure is clean and enforced by tests; tickets, messages and requesters are protected by a department scope filter with non-disclosing 404s; Internal Notes are kept out of every requester-facing path; concurrency and idempotency tools are used consistently; the SLA engine does real business-time arithmetic; delivery states are honest about what is known; and the conversation model, queues, dashboard and portal match what a working service desk needs.

It is **not ready for production**. The reasons are specific and fixable, and most of them sit outside the day-to-day ticket workflow: the production hosting work has not been done, a handful of mail and isolation correctness defects need fixing, and the live Microsoft 365 validation (D-16) has never been run.

## 1.2 Total findings

Ten specialist reviews raised **118 findings**. After removing four architectural root-cause findings that explain earlier defects, and two duplicate halves of the same issue, there are **112 distinct issues**. Four service-desk enhancements are noted separately and are not counted as defects.

| Severity | Raw findings | Distinct issues |
|---|---|---|
| Critical | 0 | 0 |
| High | 7 | 7 |
| Medium | 43 | 38 |
| Low | 57 | 56 |
| Polish | 11 | 11 |
| **Total** | **118** | **112** |

Raw findings by specialist: Documentation 11, Frontend/UX 19, SRE 14, QA 8, Security 13, Messaging 12, Backend 16, Architect 9, ITSM 7, Data/Reporting 9.

## 1.3 Production blockers

**Eleven** unique finding-level blockers (seven High, four Medium), plus the standing gate D-16 (section 5):

1. SEC-001: a Platform Admin can read mail subjects and sender addresses of any department.
2. MSG-002 (same issue as SEC-006): a correlated email from any sender joins a ticket and captures later replies.
3. MSG-001: retrying a reply after a bounce sends nothing but marks it Sent.
4. MSG-004: any Department Admin can register any mailbox; only unverified Exchange-side scoping stops them.
5. SRE-001: no production deployment artefact.
6. SRE-006: proxy trust ships empty (unverified, needs one check).
7. SRE-002: Data Protection keys not persisted.
8. SRE-003: no backup or restore procedure or evidence.
9. SRE-005: no metrics; stuck work is unobservable.
10. SEC-003: no rate limiting or abuse controls.
11. SEC-002: attachment policy is a thin blocklist with no scanner.

## 1.4 Major risks

- **Mail correctness and trust.** A stranger who holds a thread identifier can reopen a ticket, appear to the requester and receive all later agent replies (MSG-002, with BE-001, MSG-003). A bounced reply cannot be retried in any meaningful way (MSG-001). A forged bounce can mark a delivered reply as failed (MSG-007).
- **Department isolation edge.** The ticket content boundary is strong, but two administrative endpoints expose content-derived data to a Platform Admin without membership (SEC-001, SEC-005). The pattern can recur because nothing detects it (ARC-002).
- **Mailbox authority.** Registering a mailbox is open to any Department Admin and is only safe if the tenant is scoped correctly, which has never been verified (MSG-004, D-16).
- **Operational blindness.** No metrics, a readiness check that looks only at the database, and a notification outbox with no failure tracking (SRE-005, SRE-004, BE-005).
- **Silent loss of automation.** Routing and escalation rules run after commit with no stored intent; a restart can skip them without a trace (BE-004).
- **Misleading numbers and states.** The sidebar counts disagree with the list and the dashboard (DATA-001), "awaiting first response" reads zero when no SLA applies (DATA-002), first-response breaches are recorded on resolved tickets (BE-007), and the SLA policy is chosen before routing (BE-006).
- **Workflow gaps that cost time.** An answered ticket looks untouched (ITSM-001), agents cannot hand work on (ITSM-002), and tickets without a mailbox cannot be answered by email (ITSM-003).

## 1.5 Major strengths

Module graph enforced by architecture tests; one consistent concurrency and idempotency toolkit; a working transactional notification outbox; shared queue and dashboard predicates; strict constraints and department-scoped foreign keys; Internal Note protection across every path reviewed; careful mail sender identity with no silent fallback; honest delivery states; business-time SLA arithmetic; a dashboard with no per-agent ranking; request forms with explicit audiences; a history stream (messages, audit, SLA events) good enough to support truthful reports without invented snapshots. Details in section 16.

## 1.6 What prevents production readiness today

1. Hosting is not a finished deliverable: no deployment artefact, durable Data Protection keys, backup and restore with a drill, metrics, or confirmed proxy settings (B-5 to B-9).
2. Four application correctness and security blockers need fixing: administrative endpoint isolation, the ticket-party rule, the bounce-retry defect and mailbox registration authority (B-1 to B-4).
3. Abuse controls and the attachment scanning gate are open (B-10, B-11, D-08).
4. The live Graph tenant validation (D-16) has never been run, so no Microsoft 365 behaviour is verified.
5. The retention and data lifecycle policy (D-09) is undecided.
6. Thirty product and operational decisions are waiting for the owner (section 6); four of them gate the blocker work directly (D8, D9, D10, D22) and several more gate the production-readiness blockers (D1, D2, D3, D13).

---

# 2. Review Coverage

## 2.1 Disciplines

All ten specialist disciplines completed their reviews.

| # | Discipline | Prefix | Findings | Source section |
|---|---|---|---|---|
| 1 | Lead Architect / Technical Lead | ARC | 9 | 18 |
| 2 | ITSM Product & Service Desk Lead | ITSM | 7 | 19 |
| 3 | Backend / Domain Engineer | BE | 16 | 17 |
| 4 | Frontend / UX Engineer | UX | 19 | 21 |
| 5 | Microsoft 365 / Messaging Engineer | MSG | 12 | 16 |
| 6 | Security / Privacy Engineer | SEC | 13 | 15 |
| 7 | QA / Reliability Engineer | QA | 8 | 21 |
| 8 | SRE / Production Operations Engineer | SRE | 14 | 21 |
| 9 | Data / Reporting Engineer | DATA | 9 | 20 |
| 10 | Documentation / Content Designer | DOC | 11 | 21 |

The review ran in two passes: DOC, UX, SRE and QA first; after a session limit interrupted the run, the other six one at a time.

## 2.2 Limitations of the review

These limits apply to every statement in this report.

- **D-16 is still open.** The live Graph validation runbook has never been run, the result table for experiments E-1 to E-38 is empty, and the Refinement 3 attempt was recorded as blocked (no approved tenant). Every statement about what Graph or Exchange actually does is code, simulator or documentation evidence.
- **No live Graph tenant evidence** exists. Nothing here is tenant evidence.
- **No full regression suite** (backend, frontend or end to end) was run; test suites were reviewed statically.
- **No broad Playwright or browser pass** was run; no screen was walked in a browser.
- **No load, scale or query-plan testing**; no production-scale data. Performance statements concern query shape only.
- **Runtime checks were not performed**, apart from the Security review's few read-only requests against the local development API as the bootstrap Platform Admin, which confirmed SEC-001. The development API may predate the newest endpoints (queue preferences, queue types, article links), which were judged from code.
- **No restore drill, no Docker build or run, no deployment.** Hosting findings concern missing artefacts and configuration, not observed failures.
- **No data was changed.** The DEMO department was not reset or mutated, no real email was sent, and no Graph call was made.
- **Sampling.** The Documentation, Frontend and QA reviews were sampled (grep-based or inventory-based) rather than exhaustive; the later six read the areas they name, not every file.
- **Refinement 9 and 10 code** (queue preferences, queue filters, Knowledge maintenance, reply links) was reviewed by the Security, Backend and Architect passes for the points they raised; it had no separate dedicated review.
- **Some confidence levels are low or unstated.** SRE-006 is unverified; SEC-009 relies on a framework default that was recalled, not exercised; several Graph-dependent statements need tenant evidence (section 11).
- **Unresolved product behaviour is recorded as decisions** (section 6). No specialist decided it on the owner's behalf.

---

# 3. Complete Findings Register

All 118 raw findings from the ten specialists, ordered by severity, then discipline. Nothing is omitted, including Low and Polish. Four architectural findings (ARC-001 to ARC-004) are root-cause entries that explain earlier defects, and SEC-004 / SEC-006 are duplicate halves of BE-002 / MSG-002; they stay listed so each specialist's finding is visible, but are counted once in the distinct total of 112. "Blocker" means one of the 11 production blockers in section 4. "Decision" means a decision in section 6 gates the fix. Titles and categories are as recorded in the source report; where it recorded no category, a discipline-level category is shown. Severity tallies: High 7, Medium 43, Low 57, Polish 11; Critical 0.

| ID | Severity | Category | Title | Short explanation | Production blocker | Decision required | Related / root cause / duplicate | Workstream |
|---|---|---|---|---|---|---|---|---|
| SRE-001 | High | SRE / Deployment, Production Readiness | No production deployment artefact | No production deployment artefact exists: nothing serves the web app and there is no proxy or WebSocket guidance. | Yes (B-5) | Yes (D1) | Related: SEC-011 | WS-5 |
| SRE-002 | High | SRE / Production Readiness | Data Protection keys not persisted (D-13) | Data Protection keys are not persisted, so every container replacement invalidates sessions and antiforgery tokens (D-13). | Yes (B-7) | Yes (D2) | Related: D-13 | WS-6 |
| SRE-003 | High | SRE / Backup/Recovery | No backup or restore procedure, script or evidence | There is no backup or restore procedure, script or evidence for the database and the object store. | Yes (B-8) | Yes (D3) | Related: ARC-007 | WS-6 |
| SRE-005 | High | SRE / Observability | No metrics; stuck work is not observable | No metrics exist, so stuck mail sync, outbox rows, failed jobs and SLA lag cannot be alerted on; a poison outbox row retries every minute forever. | Yes (B-9) | No | Related: BE-005 | WS-7 |
| SEC-001 | High | Security / Authorization, Privacy | Platform Admin reads sender addresses and subjects of any department's problem mail | A Platform Admin without a membership reads sender addresses and subjects of any department's failed, uncertain and ignored mail (confirmed at runtime). | Yes (B-1) | No | Related: SEC-005, SEC-012, ARC-002 | WS-1 |
| MSG-001 | High | Messaging / Bug | Retry after a confirmed bounce does not send anything and reports the message as Sent | Retrying a reply after a confirmed bounce sends nothing and marks the message Sent. | Yes (B-3) | No | Related: BE-009, MSG-007, ARC-001 | WS-3 |
| MSG-002 | High | Messaging / Security, Privacy | Correlated mail is accepted from any sender and can add recipients to the ticket (SEC-006) | Correlated mail is accepted from any sender; the sender and its To/Cc join the ticket, which reopens, and receive every later reply. Master entry for SEC-006. | Yes (B-2) | Yes (D8) | Same issue as SEC-006 (counted once); Related: SEC-006, MSG-003, BE-001, ARC-003 | WS-2 |
| SRE-004 | Medium | Operations | Readiness check covers PostgreSQL only | The readiness check covers PostgreSQL only; object store, job processing and migration state are not checked. | No | Yes (D4) | Related: SRE-005 | WS-7 |
| SRE-006 | Medium | SRE / Security (deployment) | Proxy trust configuration ships empty | The proxy trust setting ships empty; forwarded headers may be trusted from any caller (unverified, needs one check). | Yes (B-6) | No | Related: SEC-009, SEC-011 | WS-5 |
| SRE-007 | Medium | Deployment | Per-module migrations with no cross-module atomicity or rollback policy | Module migrations run one by one with no cross-module atomicity or rollback policy; the documented order lists three of eight modules. | No | Yes (D5) | Related: ARC-007 | WS-6 |
| SRE-008 | Medium | Architecture/Operations | Single-host assumptions (presence in memory; ROLE not implemented) | Presence and the connection registry are in memory and the web/worker role split in the documents is not implemented. | No | Yes (D6) | Related: DOC-002, ARC-007 | WS-6 |
| SRE-009 | Medium | Reliability | Mail sync and dispatch share a small worker pool with a fixed lock and no retry | Mail sync and dispatch jobs share four workers with a fixed lock and no retry; a slow Graph can stall them. | No | No | - | WS-7 |
| SRE-010 | Medium | Deployment/Security | Production accepts unsafe storage/scan settings | Production accepts in-memory storage and the scan requirement switched off; the guards key on the environment name only. | No | No | Related: SEC-002 | WS-5 |
| SRE-011 | Medium | Operations | No pruning or sizing for growing tables; console-only logs | No pruning or sizing for the outbox, notifications, mail or attachment orphans; logs go to the console only. | No | Yes (D7) | Related: MSG-011, DATA-003, D-09 | WS-7 |
| SEC-002 | Medium | Security / Production Readiness | Attachment policy is a short extension blocklist with no scanner behind it (D-08) | The attachment policy is a short extension blocklist with no scanner behind it (D-08). | Yes (B-11) | Yes (D13) | Related: SRE-010 | WS-8 |
| SEC-003 | Medium | Security / Production Readiness | No rate limiting or abuse controls on authenticated endpoints; large uploads are buffered in memory | No rate limiting or abuse controls on authenticated endpoints; large uploads are buffered in memory. | Yes (B-10) | No | Related: MSG-005, BE-001 | WS-8 |
| SEC-004 | Medium | Security / Privacy | Merging tickets of different requesters shares one conversation with both people | Merging tickets of different requesters shares one conversation with both people. DUPLICATE of BE-002 (kept as the exposure view). | No | Yes (D9) | DUPLICATE of BE-002 (same issue found by two specialists); Related: BE-002 | WS-2 |
| MSG-003 | Medium | Messaging | conversationId is accepted as sole evidence although it behaves like a subject match | The Exchange conversation id is accepted as sole evidence although it behaves like a subject match. | No | Yes (D8) | Related: MSG-002 | WS-2 |
| MSG-004 | Medium | Messaging / Authorization | Any Department Admin can register an arbitrary mailbox; only Exchange-side scoping stops them | Any Department Admin can register an arbitrary mailbox; only unverified Exchange-side scoping stops them. | Yes (B-4) | Yes (D10) | Related: D-16 | WS-4 |
| MSG-005 | Medium | Messaging / Operations | Acknowledgements go to an unauthenticated sender and carry attacker-chosen text | Acknowledgements go to an unauthenticated sender and can carry attacker-chosen text; rotating senders bypass the per-sender limit. | No | Yes (D11) | Related: SEC-003 | WS-8 |
| MSG-006 | Medium | Messaging | Acknowledgements are not marked as automatic | Acknowledgements are not marked as automatic, so peer auto-responders cannot suppress replies. | No | No | - | WS-3 |
| BE-001 | Medium | Backend / Data Integrity | Ticket participants have no limit and no way to be removed | Ticket participants have no cap and cannot be removed. | No | Yes (D8) | Related: MSG-002, ARC-003 | WS-2 |
| BE-002 | Medium | Backend / ITSM Workflow | Merging tickets of different requesters orphans the source requester (backend side of SEC-004) | Merging tickets of different requesters orphans the source requester: the request leaves their portal and they stop receiving replies. Master entry for SEC-004. | No | Yes (D9) | Same issue as SEC-004 (counted once); Related: SEC-004, ARC-003 | WS-2 |
| BE-003 | Medium | Backend / Reliability | Posting a public message has no idempotency key; the portal reply is not atomic with its attachments | Posting a public message has no idempotency key and the portal reply is not atomic with its attachments. | No | No | Related: QA-004, UX-016 | WS-10 |
| BE-004 | Medium | Backend / Reliability | Automation and SLA-triggered work after commit is not durable | Automation and SLA follow-up after commit have no stored intent and are lost on a restart. | No | Yes (D16) | Related: ARC-004, SRE-005 | WS-9 |
| BE-005 | Medium | Backend / Reliability | One failing notification outbox item can block the whole queue; no attempt count or error is kept | One persistently failing notification outbox item can block the queue; attempts and errors are not kept. | No | No | Related: SRE-005, ARC-004 | WS-9 |
| BE-006 | Medium | ITSM Workflow / Backend | The SLA policy is chosen once at creation, before automation routes or classifies the ticket | The SLA policy is chosen once at creation, before automation routes or classifies the ticket. | No | Yes (D17) | Related: ITSM-007, DATA-002 | WS-12 |
| BE-007 | Medium | ITSM Workflow / Reporting | The first-response clock keeps running after a ticket is resolved or closed without a public reply | The first-response clock keeps running after a ticket is resolved or closed without a public reply. | No | Yes (D18) | Related: DATA-001 | WS-12 |
| BE-008 | Medium | Bug / UX-visible | Sorting the queue by SLA hides every ticket that has no deadline | Sorting the queue by SLA hides every ticket that has no deadline. | No | No | - | WS-12 |
| ARC-001 | Medium | Lead Architect / Architecture | The mail send outcome is encoded as strings across three layers | ROOT CAUSE. The mail send outcome crosses modules as a status plus a magic string in three layers. | No | No | ROOT CAUSE: explains MSG-001, BE-009, MSG-007, QA-002; Related: MSG-001, BE-009, MSG-007 | WS-3 |
| ARC-002 | Medium | Architecture / Authorization | Isolation outside the Ticketing scope filter is by convention, and content can ride on administrative endpoints | ROOT CAUSE. Isolation outside the ticket scope filter is by convention and content can leave on administrative endpoints. | No | No | ROOT CAUSE: explains SEC-001, SEC-005, SEC-012, QA-007; Related: SEC-001, SEC-005, SEC-012, QA-007 | WS-1 |
| ARC-003 | Medium | Architecture / Data Integrity | "Who is a party to a ticket" has no single owner | ROOT CAUSE. Who is a party to a ticket has no single owner. | No | No | ROOT CAUSE: explains MSG-002, MSG-003, BE-001, SEC-004, BE-002; Related: MSG-002, SEC-006, MSG-003, BE-001, SEC-004, BE-002 | WS-2 |
| ARC-004 | Medium | Architecture / Reliability | Durable intent is applied to some after-commit work and not to the rest | ROOT CAUSE. Durable intent is applied to some after-commit work and not the rest. | No | No | ROOT CAUSE: explains BE-004, BE-005, BE-015; Related: BE-004, BE-005, BE-015, SRE-005 | WS-9 |
| ITSM-001 | Medium | ITSM / ITSM Workflow | After an agent answers, the ticket looks untouched | After an agent answers, the ticket stays Open (and possibly Unassigned) and looks untouched; the queue cannot separate "needs us" from "answered". | No | Yes (D20) | Related: DATA-002 | WS-13 |
| ITSM-002 | Medium | ITSM workflow | Agents cannot hand a ticket to another team or colleague, and macros that do are invisible to them | Agents cannot change a ticket's team or hand it to a colleague, and macros that do are invisible to them. | No | Yes (D21) | - | WS-13 |
| ITSM-003 | Medium | ITSM workflow | A ticket without a stored mailbox can never be answered by email, and nothing repairs it | A ticket without a stored mailbox can never be answered by email and nothing repairs it. | No | Yes (D22) | Related: DOC-010, UX-005 | WS-13 |
| DATA-001 | Medium | Data / Reporting | The sidebar queue counts do not use the same definitions as the queue list or the dashboard | The sidebar queue counts use different definitions from the queue list and the dashboard; the SLA badges count resolved and closed tickets. | No | Yes (D25) | Related: BE-007, ARC-005 | WS-14 |
| DATA-002 | Medium | Reporting | "Awaiting first response" is silently an SLA figure | "Awaiting first response" is silently an SLA figure and reads zero when no policy applies. | No | Yes (D26) | Related: BE-006, ITSM-001 | WS-14 |
| DATA-003 | Medium | Reporting / Privacy | The audit stream is both the source of truth for history and the place retention would have to change | The append-only audit stream is both the source of truth for history and the place retention would have to change. | No | Yes (D7) | Related: SRE-011, MSG-011, D-09 | WS-14 |
| UX-001 | Medium | Frontend / Accessibility | Workspace shortcuts stay live behind open dialogs | Workspace keyboard shortcuts stay live behind open dialogs; a key press can tick a row checkbox behind a modal and feed a bulk action. | No | No | - | WS-15 |
| UX-002 | Medium | Bug | Notification deep link to #msg- is unreliable | The deep link from a mention notification to its message is unreliable because the bottom scroll wins. | No | No | - | WS-15 |
| UX-003 | Medium | Bug | Article Publish leaves a stale version after a partial failure | After a failed publish the Knowledge editor keeps a stale version and every retry conflicts until it is reopened. | No | No | Related: RC-6 | WS-10 |
| UX-004 | Medium | Accessibility | Row action buttons in admin tables share identical accessible names | Row action buttons in admin tables share identical accessible names. | No | No | - | WS-15 |
| UX-005 | Medium | Administration | Mailbox Deactivate is one click with no consequence warning | Mailbox Deactivate is one click with no warning that replies on tickets using that mailbox will not be sent. | No | Yes (D28) | Related: DOC-008 (part) | WS-15 |
| QA-001 | Medium | Reliability | Realtime client does not reconcile after a long outage | The realtime client does not refetch after a long outage and the hook is untested. | No | No | Related: RC-6 | WS-10 |
| QA-002 | Medium | Testing | Stuck-Sending outbound recovery is untested | Recovery of an outbound message stuck in Sending is untested. | No | No | Related: MSG-001 | WS-11 |
| QA-003 | Medium | Testing | Portal test helper auto-links requesters by raw SQL | The portal test helper links requesters by raw SQL, hiding an unreachable ownership state. | No | No | - | WS-11 |
| QA-004 | Medium | Testing / Concurrency | Concurrency and duplicate coverage stops at ticket and organisation writes | Concurrency and duplicate coverage stops at ticket and organisation writes. | No | Yes (D19) | Related: BE-003, BE-010 | WS-10 |
| DOC-001 | Medium | Documentation | API/email docs contradict the locked "no fallback sender" rule | Two API and email documents say the department default mailbox is a fallback sender, contradicting the locked no-fallback rule. | No | No | Related: MSG-001 context | WS-16 |
| DOC-002 | Medium | Documentation | Docs claim features that do not exist | Documents claim a web/worker role mode and generated API types that do not exist. | No | No | Related: SRE-008, ARC-009 | WS-16 |
| SRE-012 | Low | Observability/Privacy | Log privacy is convention only | Log privacy rests on convention; the documented redaction denylist is not implemented. | No | No | - | WS-7 |
| SRE-013 | Low | Operations | No runbook for dependency-down cases | There is no runbook for PostgreSQL, the object store or Graph being unavailable. | No | No | - | WS-7 |
| SRE-014 | Low | Deployment | Floating image tags, :latest SeaweedFS, AllowedHosts=* | Floating image tags, a latest tag for the object store and an unrestricted allowed-hosts setting. | No | No | - | WS-5 |
| SEC-005 | Low | Authorization | Automation execution history is not covered by the department scope filter | Automation execution history is not covered by the department scope filter; a Platform Admin can list ticket references and requester domains. | No | No | Related: SEC-001, ARC-002 | WS-1 |
| SEC-006 | Low | Messaging / Security | A correlated inbound email is accepted from any sender into an existing ticket | A correlated inbound email is accepted from any sender. DUPLICATE of MSG-002 (escalated there). | No | Yes (D8) | DUPLICATE of MSG-002 (same issue found by two specialists); Related: MSG-002, MSG-003, BE-001 | WS-2 |
| SEC-007 | Low | Authentication | Entra email/preferred_username is treated as a verified address | The Entra mail claim is treated as a verified address when linking and refreshing requester contact data. | No | Yes (D15) | - | WS-13 |
| SEC-008 | Low | Privacy | Any Department Admin can search the whole organisation's directory | Any Department Admin can search the whole organisation's directory by name or email. | No | No | - | WS-13 |
| SEC-009 | Low | Security (defence in depth) | SignalR hub has no WebSocket origin allow-list | The SignalR hub has no WebSocket origin allow-list (low confidence; relies on the framework default). | No | No | Related: SRE-006 | WS-5 |
| SEC-010 | Low | Security | Sanitiser keeps data: links in HTML mail and signatures | The sanitiser keeps data: links in inbound HTML and signatures. | No | No | - | WS-5 |
| SEC-011 | Low | Deployment / Security | Document responses carry no clickjacking or content-type headers | The application page carries no clickjacking, content-type or CSP headers; the host does not serve it. | No | No | Related: SRE-001 | WS-5 |
| SEC-012 | Low | Authorization | Platform Admin's reach over non-content department data is implicit and undocumented | A Platform Admin's reach over non-content department data (mail retry, Knowledge drafts, form audience lists) is implicit and undocumented. | No | Yes (D14) | Related: SEC-001, SEC-005, ARC-002 | WS-1 |
| MSG-007 | Low | Messaging | Any sender can pose as a bounce and mark a sent reply as failed | Any sender can pose as a bounce and mark a sent reply as failed. | No | No | Related: MSG-001 | WS-3 |
| MSG-008 | Low | Reliability | Uncertain sends are never reconciled automatically | Uncertain sends are never reconciled automatically. | No | No | Related: SRE-009 | WS-3 |
| MSG-009 | Low | ITSM Workflow / Administration | Ignored inbound mail cannot be released to a ticket | Ignored inbound mail cannot be released to a ticket from the attention list. | No | Yes (D12) | - | WS-13 |
| MSG-010 | Low | Data Integrity | A re-delivered message with the same Message-ID after 2 seconds becomes a second ticket | A re-delivered message with the same Message-ID after two seconds becomes a second ticket. | No | No | - | WS-3 |
| MSG-011 | Low | Privacy | Full bodies of all inbound mail, including ignored and bounce mail, are kept with no purge | Full bodies of all inbound mail, including ignored and bounce mail, are kept with no purge. | No | No | Related: SRE-011, DATA-003 | WS-14 |
| BE-009 | Low | Data Integrity | The delivery-state report has no transition rules | The delivery-state report has no transition rules. | No | No | Related: MSG-001, ARC-001 | WS-3 |
| BE-010 | Low | Concurrency | Bulk edits apply to whatever state a ticket is in now | Bulk edits apply to whatever state a ticket is in now. | No | Yes (D19) | Related: QA-004, UX-009 | WS-10 |
| BE-011 | Low | API Consistency | Invalid references answer with 400 in some places and 409 in others | Invalid references answer 400 in some places and 409 in others. | No | No | - | WS-16 |
| BE-012 | Low | Administration | Requester identity resolution is capped at 100 cases with no paging or search | Requester identity resolution is capped at 100 cases with no paging or search. | No | No | - | WS-13 |
| BE-013 | Low | Concurrency (Refinement 9 code) | Saving queue preferences for the first time twice at once returns a server error | Saving queue preferences for the first time twice at once returns a server error. | No | No | - | WS-10 |
| BE-014 | Low | Data Integrity / Reporting | Lifecycle timestamps are overwritten, so the first resolution time is not kept | Lifecycle timestamps are overwritten, so the first resolution time is not kept. | No | No | Related: DATA-006 | WS-12 |
| BE-015 | Low | Reliability | A queued notification is classified from the ticket's state at drain time, not at event time | A queued notification is classified from the ticket's state at drain time, not at event time. | No | No | Related: BE-005 | WS-9 |
| ARC-005 | Low | Maintainability / Reporting | "Open work" is restated in four other files besides the shared predicate | Open work is restated in four other files besides the shared predicate. | No | No | Related: DATA-001, DATA-005 | WS-14 |
| ARC-006 | Low | Documentation / Architecture governance | Significant decisions since ADR-015 are recorded in long bullets, not ADRs | Significant decisions since ADR-015 are recorded in long bullets, not ADRs. | No | No | Related: DOC-001 to DOC-004 | WS-16 |
| ARC-007 | Low | Architecture / Deployment | Cross-schema database coupling and migration order are accepted in practice but not written down or tested | Cross-schema database coupling and migration order are accepted in practice but not written down or tested. | No | No | Related: SRE-007, SRE-003 | WS-6 |
| ARC-008 | Low | Architecture / Security (latent) | A cross-module contract carries a security precondition only in prose | A cross-module contract carries a security precondition only in prose. | No | No | - | WS-16 |
| ARC-009 | Low | Maintainability | Maintainability: one very large service and an unchecked client contract | One very large service and a hand-written client contract with no drift check. | No | No | Related: DOC-002 | WS-16 |
| ITSM-004 | Low | ITSM workflow | The portal status "Waiting" does not say whose turn it is | The portal status "Waiting" does not say whose turn it is. | No | Yes (D23) | - | WS-13 |
| ITSM-005 | Low | ITSM workflow | Resolving or closing without a public message tells the requester nothing, and nothing ever closes a resolved ticket | Resolving or closing without a public message tells the requester nothing, and nothing auto-closes a resolved ticket. | No | Yes (D24) | Related: BE-007 | WS-13 |
| ITSM-006 | Low | ITSM workflow | The department setup overview can say "1 active form" when no requester can use it | The department setup overview can say "1 active form" when no requester can use it. | No | No | - | WS-13 |
| ITSM-007 | Low | ITSM workflow | New email tickets are unrouted and only an administrator-built rule routes them | New email tickets are unrouted and only an administrator-built rule routes them. | No | No | Related: BE-006 | WS-13 |
| DATA-004 | Low | Reporting / Data | The three ticket-creation audit events have different shapes | The three ticket-creation audit events have different shapes. | No | No | - | WS-14 |
| DATA-005 | Low | Reporting / Data | A merged ticket keeps its status and is easy to count by mistake | A merged ticket keeps its status and is easy to count by mistake. | No | Yes (D27) | Related: BE-002, ARC-005 | WS-14 |
| DATA-006 | Low | Reporting / Data | Lifecycle columns hold the current value only | Lifecycle columns hold the current value only; history lives in events. | No | No | Related: BE-014 | WS-14 |
| DATA-007 | Low | Reporting / Data | Two ideas of "about to breach", and an age that mixes waiting on us with waiting on the requester | Two ideas of "about to breach", and an age figure that mixes waiting on us with waiting on the requester. | No | No | - | WS-12 |
| DATA-008 | Low | Reporting / Data | Some facts are not captured at all and cannot be rebuilt later | Some facts are not captured at all (mailbox health history, Knowledge views, why no SLA applied). | No | No | - | WS-14 |
| DATA-009 | Low | Reporting / Data | One person can be several requesters | One person can be several requesters, so distinct-requester counts are approximate. | No | No | - | WS-14 |
| UX-006 | Low | Accessibility | Reorder arrows lose focus; one reorder dialog can race | Reorder arrows lose focus and one reorder dialog can send mismatched orders. | No | No | - | WS-15 |
| UX-007 | Low | Accessibility | Focus not restored after choosing from the ticket More menu | Focus is not restored after choosing an item from the ticket More menu. | No | No | - | WS-15 |
| UX-008 | Low | UX | Esc or Close silently discards typed work | Esc or Close silently discards typed work in some dialogs. | No | No | - | WS-15 |
| UX-009 | Low | Authorization (UX) | BulkBar sends a hidden Team/Assignee choice after the selection spans departments | The bulk bar keeps a hidden Team or Assignee choice after the selection spans departments; the server rejects it per ticket. | No | No | Related: BE-010 | WS-15 |
| UX-010 | Low | Accessibility | Portal "My requests" tabs use ARIA tab roles without the keyboard model | Portal My requests tabs use tab roles without the keyboard model. | No | No | - | WS-15 |
| UX-011 | Low | UX | Saved-view Delete is immediate; Archive saves the draft without saying so | Saved-view Delete has no confirmation and Archive saves the draft without saying so. | No | No | - | WS-15 |
| UX-012 | Low | Authorization (UX) | Retry sending checks permission against any department | Retry sending checks the permission against any department rather than the ticket's (UX only; the server authorises). | No | No | - | WS-15 |
| UX-013 | Low | Accessibility | Mention picker ARIA does not match behaviour | The mention picker's roles do not match its behaviour and results are not announced. | No | No | - | WS-15 |
| UX-014 | Low | Requester Experience | Portal file picker silently truncates to 5; no feedback after reply or submit | The portal file picker silently keeps only five files and there is no feedback after reply or submit. | No | No | - | WS-15 |
| UX-015 | Low | Administration | Mailbox edit shows a stale name; 8-column table has no overflow handling | Mailbox edit can overwrite a concurrent rename and the wide table has no overflow handling. | No | No | - | WS-15 |
| UX-016 | Low | Requester Experience | Portal state persists across route params | Portal drafts and the submission id persist across route changes. | No | No | Related: BE-003, QA-004 | WS-10 |
| QA-005 | Low | Testing | Negative realtime leak tests rely on fixed sleeps | Negative realtime tests rely on fixed sleeps. | No | No | - | WS-11 |
| QA-006 | Low | Testing | Recurring-job registration is never verified | Registration of the recurring jobs is never verified. | No | No | - | WS-11 |
| QA-007 | Low | Testing | Structural guard coverage is Ticketing only | Structural guard coverage stops at Ticketing. | No | Yes (D30) | Related: ARC-002 | WS-1 |
| DOC-003 | Low | Documentation | Stale phase and module statements | Stale phase and module statements; tags still listed; refinements missing from the plan. | No | No | - | WS-16 |
| DOC-004 | Low | Documentation | Docs say "full suite at milestones", contradicting CLAUDE.md | Documents recommend a full suite at milestones, contradicting the project testing rules. | No | No | - | WS-16 |
| DOC-005 | Low | Documentation | Demo tooling and operations runbook missing from docs | Demo tooling and an operations runbook are missing from the documents. | No | No | Related: SRE-013 | WS-16 |
| DOC-006 | Low | Copy/Terminology | Automation history shows raw codes | The automation history screen shows raw codes. | No | No | - | WS-16 |
| DOC-008 | Low | Copy/Terminology | Weak confirmation copy | Weak confirmation wording: the merge dialog does not say it cannot be undone and the member removal confirmation is vague. | No | No | Related: UX-005 | WS-16 |
| SEC-013 | Polish | Visual Polish / Security hygiene | Knowledge markdown accepts /\host as an in-app link | Knowledge markdown accepts /\host as an in-app link. | No | No | - | WS-16 |
| MSG-012 | Polish | Copy/Terminology | Every reply shows an opaque token to the requester | Every reply shows an opaque correlation token to the requester. | No | No | Related: MSG-002 | WS-2 |
| BE-016 | Polish | Copy/Terminology | A validation message lists the wrong allowed sources | A validation message lists the wrong allowed sources. | No | No | - | WS-16 |
| UX-017 | Polish | Frontend / UX | Dashboard subtitle exposes roadmap wording | The dashboard subtitle exposes roadmap wording ("reports come later"). | No | No | - | WS-15 |
| UX-018 | Polish | Frontend / UX | Dead realtime foreign-change code | Dead realtime foreign-change code remains and grows state for the session. | No | Yes (D29) | - | WS-15 |
| UX-019 | Polish | Frontend / UX | Small visual items (breadcrumb, unnamed headers, debounce, live regions, panel position, loading state, spacing typo) | A group of small visual items: breadcrumb links, unnamed table headers, un-debounced search, live regions, notification panel position, loading state, a spacing typo. | No | No | - | WS-15 |
| QA-008 | Polish | Reliability / Test coverage | Merged-source bulk edit untested; fixed wait in an end-to-end test | Bulk edit of a merged source is untested and one end-to-end test uses a fixed wait. | No | No | - | WS-11 |
| DOC-007 | Polish | Documentation | Roles shown as DepartmentAdmin / TeamLead in Members | The Members list shows role names as DepartmentAdmin and TeamLead. | No | No | - | WS-16 |
| DOC-009 | Polish | Documentation | Server 400 messages repeat the field name | Server validation messages repeat the field name. | No | No | - | WS-16 |
| DOC-010 | Polish | Documentation | Mailbox-assignment question missing from the open-decisions list; roadmap wording stale | The mailbox-assignment question is recorded only in the project rules, and roadmap wording is stale. | No | Yes (D22) | Related: ITSM-003 | WS-16 |
| DOC-011 | Polish | Documentation | Mixed "authorization" / "authorised" spelling in documents | Documents mix "authorization" and "authorised". | No | No | - | WS-16 |

Four ITSM service-desk enhancements are noted in the source report as enhancements rather than defects and are not counted here (see section 10).

---

# 4. Production Blockers

Eleven unique blockers. D-16 is a separate standing gate (section 5) and is not in this table. "Workstream" refers to section 8; "Decision" refers to section 6.

| # | Finding ID(s) | Severity | Problem | Impact | Root cause | Required fix | Decision required | Workstream |
|---|---|---|---|---|---|---|---|---|
| B-1 | SEC-001 (related: SEC-005, SEC-012, ARC-002) | High | A Platform Admin without a membership reads the sender addresses and subjects of failed, uncertain and ignored mail of any department. Confirmed at runtime: the same caller gets 404 on the department dashboard. | Breaks the locked rule that a Platform Admin has no implicit access to department content. Subjects can name people or grievances. | RC-4 / ARC-002: administrative permission passes for a Platform Admin without membership, and the mail data has no scope filter. | Return subject and sender address only to callers who also hold ticket read in that department; others get operational fields only. Add one test for a non-member Platform Admin. | No for the fix; D14 for the wider boundary | WS-1 (P0) |
| B-2 | MSG-002, same issue as SEC-006 (related: MSG-003, BE-001, MSG-012, ARC-003) | High | Correlated mail is accepted from any sender. The sender and every To/Cc address become ticket participants, the ticket reopens, the text appears to the requester, and every later public agent reply is also emailed to those participants. | A person who holds a thread identifier (for example from a forwarded email) can capture future replies on a ticket without anyone being warned. | RC-2 / ARC-003: nothing defines who is a party to a ticket. | Do not attach, reopen or add participants for a message whose sender is not the requester or an existing participant; at minimum never create participants from a non-participant's message; show the Cc recipients by name in the composer. | Yes: D8 | WS-2 (P0) |
| B-3 | MSG-001 (related: BE-009, MSG-007, ARC-001, QA-002) | High | Retrying a reply after a confirmed bounce sends nothing and marks the message Sent. The retry keeps the old provider draft, finds the original in Sent Items and skips the send. | The requester never receives the answer while the ticket says "Sent by email"; the bounce evidence is cleared from the row. | RC-3 / ARC-001 (local cause: the retry does not clear the draft id). | A retry of an already-submitted message creates a new draft (clear the draft id and submit flag) and is recorded as a deliberate re-send, or block Retry for a delivery failure and tell the agent to fix the address and send a new reply; guard the delivery report (BE-009). Add a test: send, bounce, retry, assert a second provider send. | No | WS-3 (P0) |
| B-4 | MSG-004 (related: D-16 E-2, E-3) | Medium | Any Department Admin can register an arbitrary mailbox address; creation does not ask Graph whether the mailbox exists or is in the application's scope. | If the application registration has tenant-wide mail permissions, a Department Admin could ingest another person's mailbox as tickets and reply from that identity. With the runbook's scoping the action only produces permission errors, but that scoping is unverified. | No creation-time check, plus the unrun tenant validation. | Probe the mailbox through Graph on create and refuse on 403/404; decide whether registering a mailbox is a Platform Admin action or needs an approved-address list; prove the scoping in the tenant run. | Yes: D10 | WS-4 (P0) |
| B-5 | SRE-001 (related: SEC-011) | High | No production deployment artefact. The compose file is for development; the host does not serve the web app; no proxy or WebSocket guidance. | There is no defined way to run the product in production. | RC-7: hosting not yet a deliverable. | Decide how the web app is served and which proxy is used; provide one reference production configuration and a deployment runbook. | Yes: D1 | WS-5 (P1) |
| B-6 | SRE-006 | Medium (unverified) | The proxy trust setting ships empty; nothing enforces it outside development. By the specialist's recollection of the framework, forwarded headers may then be trusted from any caller. | A client could spoof its address, affecting rate limiting, logging and the audit source address. | RC-7. | Confirm the framework behaviour with one targeted check; if confirmed, require explicit proxy configuration in production (fail start-up if absent). | No (verify first) | WS-5 (P1) |
| B-7 | SRE-002 (D-13) | High | Data Protection keys are not persisted. | Every container replacement invalidates all sessions and antiforgery tokens; with more than one instance cookies would not validate across them. | RC-7. | Persist the keys to a durable store with the application name set. | Yes: D2 | WS-6 (P1) |
| B-8 | SRE-003 (related: ARC-007) | High | No backup or restore procedure, script or evidence. The database and the object store must be restored consistently. | Recoverability is not demonstrated; a backup without a restore drill must not be treated as recoverability. | RC-7. | Document and script backup of PostgreSQL and attachments; perform and record one restore drill. | Yes: D3 | WS-6 (P1) |
| B-9 | SRE-005 (related: BE-005, SRE-004) | High | No metrics exist (tracing only). Nothing can alert on stale mail sync, stuck outbox rows, failed jobs or SLA lag. A poison notification row is retried every minute forever. | Failures are silent; a stuck outbox could hold back all new notifications without any sign. | RC-1 and RC-7. | Add a small set of counters and gauges (oldest unprocessed outbox age, failed jobs, last mail-sync age, SLA job lag) and cap or park poison rows with a visible state. | No | WS-7 (P1) |
| B-10 | SEC-003 (related: MSG-005, BE-001) | Medium | No rate limiting or abuse controls on authenticated endpoints; reply uploads of up to five 25 MB files are buffered in memory; portal creation and replies have no per-account limit. This is a listed project gate. | Any signed-in employee, or a compromised account, can exhaust memory, storage, the mail queue or the database. | RC-7 / missing controls. | Use the built-in ASP.NET Core rate limiter with per-user policies for portal create, reply and search, and explicit request-body limits for the multipart endpoint. No new dependency. | No (limit values to agree) | WS-8 (P1) |
| B-11 | SEC-002 (related: SRE-010, D-08) | Medium | The attachment policy is a 26-extension blocklist plus signature checks, with no scanner. Production accepts the scan requirement switched off. | An external sender mails a macro-enabled workbook or disk image to a department mailbox; an agent downloads and opens it. Download is correctly forced as an attachment, so the risk is endpoint malware, not in-app script. | Scanner not yet chosen (D-08); thin blocklist. | Extend the blocked types; fail start-up in production when the scan requirement is off until a scanner exists. | Yes: D13 (and the D-08 engine choice) | WS-8 (P1) |

Application correctness and security (B-1 to B-4) are band P0; production readiness (B-5 to B-11) is band P1.

---

# 5. Production Readiness Gates

This report does not claim production readiness, compliance or certification. The gates below are open now.

| Gate | Status | What it needs | Related findings |
|---|---|---|---|
| **D-16 live Graph tenant validation** (standing gate, separate from the blockers) | **Open.** The runbook is unrun, the E-1 to E-38 result table is empty, and the Refinement 3 attempt was recorded as blocked for want of an approved tenant. | A test tenant, shared mailboxes and an Exchange administrator; the runbook run recorded. The experiments that the findings depend on are in the Messaging section (E-21/E-22 sender identity, E-25/E-12/E-10 sent copy and identifiers, E-23/E-24/E-26 reply headers, E-15 to E-17 conversation id, E-2/E-3 scoping, E-30 to E-35 subscriptions, E-37/E-38 loop and header samples, E-13/E-14 Message-ID behaviour). | MSG-001 (real path), MSG-003, MSG-004, MSG-006, MSG-007, MSG-008, MSG-010 |
| **D-09 retention and data lifecycle policy** | Open (organisational policy). | Retention periods, export, anonymisation and legal hold. Pruning, raw mail retention and the audit stream's separation from personal data wait on it. | SRE-011, MSG-011, DATA-003 |
| **D-08 malware scanning engine** | Open (engine choice). Scanning is required before production releases that accept attachments. | Choose and integrate a scanner; keep the scan requirement enforced in production. | SEC-002, SRE-010 |
| **D-13 Data Protection key persistence** | Open. | A durable key store (PostgreSQL or a persistent volume, per the open-decisions list). | SRE-002 (blocker B-7) |
| **Backup and restore evidence** | Open. | Backup procedure for the database and object store, plus a recorded restore drill. | SRE-003 (blocker B-8) |
| **Abuse and rate controls** | Open (listed project gate). | Per-user rate limits and request-size limits. | SEC-003 (blocker B-10), MSG-005 |
| **Production hosting and deployment** | Not built. | Deployment artefact, serving of the web app, proxy and WebSocket configuration, production start-up guards. | SRE-001 (blocker B-5), SEC-011, SRE-010, SRE-014 |
| **Observability and metrics** | Not built. | Counters for mail sync age, outbox age, failed jobs and SLA lag; a meaningful readiness check; log redaction. | SRE-005 (blocker B-9), SRE-004, SRE-012 |
| **Proxy configuration** | Unverified. | Confirm the forwarded-header default; require explicit proxy settings in production. | SRE-006 (blocker B-6) |
| **Mailbox scoping and registration authority** | Unverified. | Tenant scoping proven (E-2, E-3) and the decision on who may register mailboxes. | MSG-004 (blocker B-4) |
| **Migration and rollback policy** | Undocumented. | One paragraph and a test for the module order; rollback by restore stated. | SRE-007, ARC-007 |
| **Evidence not yet produced** | Not run (by instruction). | A full regression run, a broad browser pass, load and query testing at scale, and a restore drill. Each needs explicit permission to run. | Review limitations (section 2.2) |

Precondition rather than gate: tickets without a stored mailbox (ITSM-003) should be settled before the portal is opened to requesters.

---

# 6. Full Decision Register

Thirty distinct questions, collapsed from 37 raw "decision required" flags (several specialists asked the same question). Each entry gives the options the review supports and what the review says about them. Where the review says nothing about an advantage or disadvantage, the entry says "Not assessed in the review"; nothing has been added. A "recommended default" appears only where the review established one. "Remediation blocked" says whether the related work cannot be finished until you decide; work that does not depend on the decision can proceed.

Decisions are grouped by theme. Workstreams (WS) are in section 8.

## Production and operations

### D1. How is the web app served in production, and which reverse proxy?
- **Related findings:** SRE-001 (blocker B-5), SEC-011.
- **Question:** Will the host application serve the built web app, or will a separate web server do it, and which proxy terminates TLS and WebSockets?
- **Why needed:** There is no production deployment artefact, nothing in the host serves the web app, and no proxy or WebSocket guidance exists. The security headers for the application page belong to whichever layer serves it.
- **Option A:** The host serves the built web app (static files plus a fallback route). *Advantages/disadvantages:* not assessed in the review.
- **Option B:** A separate static web server, behind the same proxy, serves it. *Advantages/disadvantages:* not assessed in the review.
- **What the review does say for both:** the serving layer must set a content security policy with frame protection, content-type and referrer headers (SEC-011) and carry WebSocket traffic for the live updates.
- **Recommended default:** None established.
- **Remediation blocked:** Yes: WS-5 cannot be finished.

### D2. Where do the Data Protection keys live?
- **Related findings:** SRE-002 (blocker B-7), D-13.
- **Question:** Which durable store holds the key ring?
- **Why needed:** Without durable keys every container replacement signs everyone out and invalidates antiforgery tokens; several instances could not share cookies.
- **Option A:** PostgreSQL. *Advantages/disadvantages:* not assessed in the review.
- **Option B:** A persistent volume. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established (the open-decisions list names both stores).
- **Remediation blocked:** Yes: WS-6.

### D3. What are the backup targets, recovery objectives, and who runs the restore drill?
- **Related findings:** SRE-003 (blocker B-8), ARC-007.
- **Question:** Set the recovery point and recovery time objectives, the storage target for backups of PostgreSQL and the attachment store, and the owner of a recorded restore drill.
- **Why needed:** There is no backup or restore procedure, script or evidence; the database and object store must be restored consistently.
- **Option A:** Define targets and an owner, script the backups, run and record one drill. *Advantages/disadvantages:* not assessed in the review.
- **Option B:** The review offers no alternative.
- **Recommended default:** None established.
- **Remediation blocked:** Yes: WS-6.

### D4. What makes an instance "not ready"?
- **Related findings:** SRE-004.
- **Question:** Beyond PostgreSQL, which conditions should fail the readiness check?
- **Why needed:** An instance with a broken object store or stalled jobs currently reports ready.
- **Option A:** Add the object store. **Option B:** Add job processing. **Option C:** Add migration state. The review lists these as candidates and does not rank them or say whether they are exclusive. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established.
- **Remediation blocked:** No (the rest of WS-7 can proceed).

### D5. What is the migration order and rollback policy across the eight modules?
- **Related findings:** SRE-007, ARC-007.
- **Question:** State the order and decide how a failed or unwanted migration is reversed.
- **Why needed:** Modules share one database with hard cross-schema keys; a down migration of an earlier module cannot run while later modules exist; the documented order lists three of eight modules.
- **Option A:** Document one database, one role, the order, and rollback by restore from backup. *Advantage (per the Architect):* acceptable for a modular monolith as long as it is written down. *Disadvantage:* a single module cannot be restored or rolled back on its own.
- **Option B:** Rely on per-module down migrations. *Disadvantage (per the Architect):* cross-module reversal is not possible while dependent modules exist. No advantage stated.
- **Recommended default:** The Architect treats Option A as acceptable if stated; the review does not call it a decision made.
- **Remediation blocked:** Only the documentation part of WS-6.

### D6. Is a single instance the supported model, or is the web/worker split to be built?
- **Related findings:** SRE-008, DOC-002.
- **Question:** Document single-instance as the supported topology, or schedule the role split that the documents already describe.
- **Why needed:** Presence and the connection registry live in memory with no backplane; the documented role mode is not implemented.
- **Option A:** State single-instance as supported and correct the documents. *Advantages/disadvantages:* not assessed in the review beyond the in-memory limits above.
- **Option B:** Schedule the split. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established.
- **Remediation blocked:** No.

### D7. Retention, what is kept for reporting, pruning, and the log sink.
- **Related findings:** DATA-003, MSG-011, SRE-011, D-09.
- **Question:** Decide retention periods; decide which facts are retained for reporting separately from personal data; decide pruning for the outbox, notifications, mail, attachment orphans and logs; decide the log sink.
- **Why needed:** The append-only audit stream is the source of truth for history and also holds personal data (actor names, old and new subject text); raw mail bodies, including ignored mail, are kept indefinitely; tables grow without bounds.
- **Option A:** Keep a content-free lifecycle record separate from personal data (a view or a small table written by the same audit write). *Advantage (per the Data review):* reports survive any later anonymisation, and no warehouse is needed. *Disadvantage:* not assessed beyond being additional work.
- **Option B:** Leave the audit stream as the only source and let reports read it. *Disadvantage (per the Data review):* any later erasure or anonymisation collides with the append-only triggers and silently changes history; report extracts inherit personal data. No advantage stated.
- **Recommended default:** None established. Retention periods themselves are an organisational policy (D-09).
- **Remediation blocked:** Yes for WS-14 (reporting foundation) and the pruning part of WS-7.

## Mail and ticket parties

### D8. What happens to mail that carries a valid thread identifier from a sender who is neither the requester nor a participant? Also: conversation-id evidence, participant cap and removal.
- **Related findings:** MSG-002 (blocker B-2), SEC-006, MSG-003, BE-001, MSG-012, ARC-003.
- **Question:** For such mail, choose the outcome; decide whether the conversation id may remain sufficient evidence; decide the participant cap and whether agents get a "remove from Cc" action.
- **Why needed:** Today one email can join a ticket, reopen it and capture all later replies; participants have no cap and cannot be removed; a very large Cc list makes every later reply fail.
- **Option A:** Treat the mail as a new ticket with a note naming the suggested ticket (as already done for a subject hint). *Advantages/disadvantages:* not assessed in the review beyond that precedent.
- **Option B:** Hold it for review on the suggested ticket without showing it to the requester or reopening. *Advantages/disadvantages:* not assessed in the review.
- **Option C:** Attach it but without adding participants and without reopening. *Advantage (per the Messaging review):* the smallest change. *Disadvantage:* it still shows the sender's text to the requester.
- **Conversation id:** keep it only together with a participant sender, or remove it. *Advantages/disadvantages:* not assessed beyond the weakness described in MSG-003 (E-15 to E-17 unverified).
- **Recommended default:** The review's minimum, independent of the choice: never create participants from the To/Cc of a message sent by a non-participant, and show the Cc recipients by name in the composer. It does not recommend A, B or C.
- **Remediation blocked:** Yes: WS-2 (blocker B-2) cannot be finished.

### D9. What happens when tickets of different requesters are merged?
- **Related findings:** SEC-004, BE-002, ARC-003.
- **Question:** Block, warn, or allow with the source requester becoming a participant?
- **Why needed:** The source requester's request disappears from their portal and they stop receiving replies; the canonical requester sees the source requester's later emails as "Another participant".
- **Option A:** Block merging across different requesters. *Disadvantage (per the ITSM review):* it would hurt; merging different people's reports of one outage, or one person writing from two addresses, is common.
- **Option B:** Warn the agent in the merge dialog. *Advantages/disadvantages:* not assessed in the review.
- **Option C:** Allow it, add the source requester as a participant and show them the canonical request. *Advantage (per the ITSM review):* the usual convention in mature desks; keeps one working ticket and keeps the other people informed.
- **Recommended default:** The ITSM review states that Option C is "the right outcome", while saying the choice is the owner's.
- **Remediation blocked:** Yes for the merge part of WS-2.

### D10. Who may register a mailbox, and should creation probe the mailbox?
- **Related findings:** MSG-004 (blocker B-4), D-16.
- **Question:** Restrict mailbox registration to Platform Admins, require an approved-address list, and/or probe the mailbox through Graph on creation.
- **Why needed:** Registration only checks the address format; with tenant-wide mail permissions a Department Admin could register another person's mailbox.
- **Option A:** Platform Admin only. *Advantages/disadvantages:* not assessed in the review.
- **Option B:** An approved-address list. *Advantages/disadvantages:* not assessed in the review.
- **Option C:** A creation-time probe that refuses on 403/404 (can be combined with A or B). *Advantage:* detects a mailbox outside the application's scope. *Limit stated:* it checks scope, it does not decide who may register.
- **Recommended default:** None established. With the runbook's Exchange-side scoping in place, the action only produces permission errors; that scoping is unverified.
- **Remediation blocked:** Yes: WS-4 (blocker B-4).

### D11. Which control stops acknowledgements going to forged or unauthenticated senders?
- **Related findings:** MSG-005, SEC-003.
- **Question:** Choose among a per-mailbox cap, authentication-based acknowledgement, and removing the subject placeholder from the default template.
- **Why needed:** The only inbound throttle is per sender address, so rotating senders bypass it; the acknowledgement can carry attacker text to a forged address.
- **Option A:** A per-mailbox cap on acknowledgements per window. *Advantages/disadvantages:* not assessed in the review beyond bounding the volume.
- **Option B:** Acknowledge only messages that pass SPF/DKIM/DMARC. *Limit stated:* the availability of the authentication header through Graph is unverified.
- **Option C:** Leave the subject placeholder out of the default template. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established.
- **Remediation blocked:** Yes: the acknowledgement part of WS-8.

### D12. Should an operator be able to create a ticket from ignored inbound mail?
- **Related findings:** MSG-009.
- **Question:** Add an audited "create ticket from this message" action, and for which ignore reasons?
- **Why needed:** Legitimate mail ignored by a rule (auto-submitted, bulk, list headers, rate limit) can be read but never becomes a ticket.
- **Option A:** Add the action for chosen reasons. **Option B:** Leave as is. *Advantages/disadvantages:* not assessed in the review beyond the impact above.
- **Recommended default:** None established.
- **Remediation blocked:** No.

## Security and permissions

### D13. Which further attachment types are blocked, and may legitimate macro-enabled files be allowed after scanning?
- **Related findings:** SEC-002 (blocker B-11), SRE-010, D-08.
- **Question:** Extend the blocklist, and decide the policy for macro-enabled Office files.
- **Why needed:** Until a scanner exists the blocklist is the only control; macro-enabled Office files, disk images, HTML/SVG, shortcuts and similar types currently pass.
- **Option A:** Extend the blocked types and fail production start-up when the scan requirement is off. *Advantages/disadvantages:* not assessed beyond closing the gap.
- **Option B:** Allow legitimate macro workbooks once a scanner exists. *Limit stated:* depends on the scanner (D-08 engine choice).
- **Recommended default:** None established.
- **Remediation blocked:** Yes: WS-8 (blocker B-11).

### D14. Which administrative capabilities does a Platform Admin keep without a membership?
- **Related findings:** SEC-012, SEC-001, SEC-005, ARC-002.
- **Question:** Confirm the intended boundary, in particular mail retry and resend, Knowledge draft access, form audience lists, member lists and the department audit view.
- **Why needed:** Administrative permissions pass for a Platform Admin in every department; the boundary is not written down, and two of its side doors leaked content-derived data.
- **Option A:** Keep these capabilities and document them. **Option B:** Remove content-adjacent capabilities (mail retry and resend, Knowledge drafts) from non-members. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established.
- **Remediation blocked:** Only the documentation part of WS-1; the SEC-001 and SEC-005 fixes do not wait.

### D15. Is the tenant-controlled mail attribute trusted as the requester's verified address?
- **Related findings:** SEC-007.
- **Question:** Accept and document the trust assumption, or require an immutable-identity match before any address-based link?
- **Why needed:** A user who can set their own mail attribute could pre-claim someone's unused requester row; the precondition is tenant-level control.
- **Option A:** Accept and document. **Option B:** Tighten linking. *Advantages/disadvantages:* not assessed beyond the scenario above.
- **Recommended default:** None established.
- **Remediation blocked:** No.

### D30. Extend structural guards beyond Ticketing, or accept example-based coverage?
- **Related findings:** QA-007, ARC-002.
- **Question:** Add structural guards for Knowledge, Mail and Attachments queries, or accept the current coverage?
- **Why needed:** Isolation outside the ticket set is by convention; the existing architecture tests guard Ticketing's scope bypass only.
- **Option A:** Add guards. **Option B:** Accept example-based coverage. *Advantages/disadvantages:* not assessed in the review.
- **Separately supported:** the administrative-endpoint probe test (ARC-002) is recommended regardless.
- **Recommended default:** The probe test is supported; the broader guards are optional.
- **Remediation blocked:** No.

## Reliability and replay

### D16. Is best-effort automation after commit acceptable, or must it be durable?
- **Related findings:** BE-004, ARC-004, BE-005.
- **Question:** Accept the current behaviour, or store a small intent in the same transaction and drain it with the existing job?
- **Why needed:** A restart between commit and dispatch silently skips routing and escalation rules, with no record.
- **Option A:** Accept best-effort (the current stated behaviour). *Advantages/disadvantages:* not assessed beyond the loss scenario.
- **Option B:** Reuse the existing outbox pattern (intent row in the same transaction, existing recurring job, deterministic keys already present). *Advantage:* closes BE-004 and shapes BE-005 and BE-015. *Limit stated:* do not widen it into a general event framework.
- **Recommended default:** The Architect's direction is Option B (reuse the outbox pattern); it is presented as the smallest fix, not as a made decision.
- **Remediation blocked:** Yes for the automation part of WS-9.

### D19. Is bulk edit last-write-wins by design, or should it check each row's version?
- **Related findings:** BE-010, QA-004, UX-009.
- **Question:** Keep it, or send per-row versions and report "changed since you selected it" per ticket?
- **Why needed:** A stale selection can overwrite an intermediate decision (for example resolving a ticket the requester has just reopened).
- **Option A:** Keep. *Advantage (per the Backend review):* each ticket is processed atomically, audited with previous and next values, and permissions and validity are re-checked per ticket; no inconsistent state results.
- **Option B:** Per-row versions. *Advantage:* protects the intermediate decision. *Disadvantage:* not assessed beyond added work.
- **Recommended default:** None established.
- **Remediation blocked:** No.

### D29. Remove the dead foreign-change realtime code, or use it?
- **Related findings:** UX-018.
- **Question:** Remove it, or wire an "updated by someone else" notice into the ticket pane?
- **Why needed:** The code has no callers except tests and its module-level state grows during a session.
- **Option A:** Remove. **Option B:** Wire a notice. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established.
- **Remediation blocked:** No.

## SLA and ticket lifecycle

### D17. When is the SLA policy chosen?
- **Related findings:** BE-006, ITSM-007, DATA-002.
- **Question:** Keep creation-time selection, or re-evaluate when team, type or priority changes?
- **Why needed:** Email tickets arrive unrouted; team-, type- and priority-based policies rarely match them, and raising a ticket to Urgent changes nothing.
- **Option A:** Keep creation-time selection, document it and hide the conditions that cannot match. *Advantages/disadvantages:* not assessed beyond removing the misleading configuration.
- **Option B:** Re-evaluate on change, keeping the original start time so the clock does not restart on re-triage. *Advantage (per the ITSM review):* matches how the configuration looks. *Limit stated:* how running clocks restart is part of the decision.
- **Option C:** Run automation before the SLA is applied. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** The ITSM review gives Option B as its "recommended direction for the decision (not a decision)".
- **Remediation blocked:** Yes: WS-12.

### D18. Does resolving or closing without a public reply stop the first-response clock, and how does it count?
- **Related findings:** BE-007, DATA-001, ITSM-005.
- **Question:** Stop and count as not applicable, stop and count as met, or require a reply (today)?
- **Why needed:** Every first-contact resolution, spam and duplicate otherwise records a first-response breach afterwards, fires the breach automation, and would count as a miss in any attainment report.
- **Option A:** Stop, count as not applicable. *Advantage (per the ITSM review):* the usual desk convention.
- **Option B:** Stop, count as met at that moment. *Advantages/disadvantages:* not assessed in the review.
- **Option C:** Require a reply (current). *Disadvantage:* the breaches above.
- **Recommended default:** The ITSM review names Option A as the usual convention and leaves the choice to the owner.
- **Remediation blocked:** Yes: WS-12 and the reporting definitions in WS-14.

## Service-desk workflow

### D20. How should "needs us" be told from "answered" after the first reply?
- **Related findings:** ITSM-001, DATA-002.
- **Question:** Which mechanism: a per-department default status after a public reply, an agent-reply automation trigger, or a last-speaker marker in the queue? (No new status.)
- **Why needed:** After an agent replies the ticket stays Open, and Unassigned if nobody claimed it; the queue cannot separate unanswered from answered.
- **Option A:** Default status after reply (for example Pending). **Option B:** Agent-reply trigger. **Option C:** Last-speaker marker. The review lists them as alternatives and does not rank them. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established.
- **Remediation blocked:** Yes for the ITSM-001 part of WS-13.

### D21. May Agents change a ticket's team?
- **Related findings:** ITSM-002.
- **Question:** Allow team changes for the Agent role (assignee changes staying with leads), or keep the permission and show unavailable macros as disabled with a reason?
- **Why needed:** Escalation to a specialist team needs a team lead every time, and a macro built to escalate is invisible to agents without explanation.
- **Option A:** Allow Agents to change the team (routing). *Note:* this changes the permission model.
- **Option B:** Keep permissions, show unavailable macros disabled with a reason. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established.
- **Remediation blocked:** Yes for the ITSM-002 part of WS-13.

### D22. How are tickets without a stored mailbox handled?
- **Related findings:** ITSM-003, DOC-010.
- **Question:** Refuse portal submissions until the department has a usable default mailbox, or design the explicit "assign a mailbox to this ticket" action? Also: record the question in the open-decisions list.
- **Why needed:** Such tickets can never be answered by email, connecting a mailbox later does not help them, and the project rule is to ask before designing the assignment workflow.
- **Option A:** Refuse submissions (or disable the form) until a default mailbox exists. *Advantages/disadvantages:* not assessed in the review.
- **Option B:** Design the explicit assignment action for an authorised user. *Note:* the project rules say to ask before designing it.
- **Stopgap named by the review:** make the rollout order explicit in the setup guide (mailbox first, forms second).
- **Recommended default:** None established. Settle before the portal is opened to requesters.
- **Remediation blocked:** Yes for this part of WS-13.

### D23. Does "Waiting" always mean "waiting for you" in requester language?
- **Related findings:** ITSM-004.
- **Question:** Rename the status for requesters, or add a line of explanation?
- **Why needed:** Pending covers waiting for the requester, a third party or another dependency; the requester cannot tell whose turn it is.
- **Option A:** Rename. **Option B:** Explain in a line. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established.
- **Remediation blocked:** No.

### D24. Should resolving without a public reply prompt the agent?
- **Related findings:** ITSM-005, BE-007.
- **Question:** Prompt with the option to send a reply now, or accept silent resolution?
- **Why needed:** A status change alone emails no one, and nothing closes a resolved ticket (timers are already in the backlog).
- **Option A:** Prompt. *Note:* the review calls this the "at minimum" safeguard. **Option B:** Accept silent resolution.
- **Recommended default:** None established.
- **Remediation blocked:** No.

### D28. Should the interface block deactivating the default mailbox?
- **Related findings:** UX-005.
- **Question:** A consequence confirmation only, or also a block on the default mailbox?
- **Why needed:** One click blocks replies on every ticket that uses the mailbox.
- **Option A:** Confirmation stating that replies on tickets using this mailbox will not be sent until it is reactivated (no decision needed).
- **Option B:** Also block deactivating the default mailbox. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** Confirmation only.
- **Remediation blocked:** No.

## Reporting definitions

### D25. Do Pending tickets belong in the sidebar counts?
- **Related findings:** DATA-001, BE-007, ARC-005.
- **Question:** Count Open only, or Open and Pending?
- **Why needed:** The sidebar counts Open only; the list and the dashboard use Open and Pending. (That the SLA badges also count resolved and closed tickets is a plain bug and needs no decision.)
- **Option A:** Open only, and say so everywhere. *Advantage (per the Data review):* a legitimate "needs us" reading.
- **Option B:** Open and Pending, matching the list and dashboard. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established.
- **Remediation blocked:** Yes for the sidebar part of WS-14.

### D26. What does "awaiting first response" mean?
- **Related findings:** DATA-002, BE-006, ITSM-001.
- **Question:** Define it from the message stream, or rename the existing SLA-based figure?
- **Why needed:** It reads zero when no SLA policy applies, however many tickets nobody has answered.
- **Option A:** No public agent message yet, independent of SLA. *Advantage:* does not depend on configuration. **Option B:** Rename the existing one "SLA first response pending" (the queue filter and dashboard share one definition, so one change covers both). *Advantages/disadvantages:* not assessed further.
- **Recommended default:** None established.
- **Remediation blocked:** Yes for the definition part of WS-14.

### D27. Are merged duplicates counted in "created during the period"?
- **Related findings:** DATA-005, BE-002, ARC-005.
- **Question:** Count them (they were raised) or exclude them (they are not separate demand)?
- **Why needed:** A merged source keeps its status, so it can be counted as open, resolved or created demand twice.
- **Option A:** Count. **Option B:** Exclude. The review calls both defensible and asks that it be stated once. *Advantages/disadvantages:* not assessed in the review.
- **Recommended default:** None established.
- **Remediation blocked:** Yes for the reporting definitions in WS-14.

---

**Summary of what blocks what.** D8, D9, D10 and D22 gate the work that closes four of the eleven blockers (B-2, B-4 and parts of WS-2 and WS-13). D1, D2, D3 and D13 gate the production-readiness blockers B-5, B-7, B-8 and B-11. D7, D16, D17, D18, D20, D21, D22 (workflow part), D25, D26 and D27 gate parts of their workstreams. D4, D6, D12, D15, D19, D23, D24, D28, D29 and D30 do not block anything else, and D5 and D14 gate only their documentation parts.

---

# 7. Cross-Cutting Root Causes

The Lead Architect found that most of the serious findings share a small number of causes, and that the application's architecture itself should not be restructured. Fixing the cause once closes several findings; fixing each finding separately would repeat the work.

## RC-1. Uneven durability of post-commit work
Some work that follows a change is stored as intent in the same transaction and drained by a recurring job: the notification outbox, the outbound mail queue, the delivery-state re-report. Other work is done after commit with nothing stored: automation dispatch and the follow-up to SLA warnings and breaches. A restart between commit and dispatch silently skips the second kind, and there is no record that anything was lost. The notification outbox itself keeps no attempt count or error, so one persistently failing item could block newer ones, and it classifies an event from the ticket's state at drain time rather than at event time.
- **Explains:** BE-004, BE-005, BE-015, ARC-004 (root-cause entry), SRE-005 (the missing operational signal).
- **Systemic or local:** systemic for automation and SLA follow-up; local for the outbox row.
- **One fix:** extend the existing outbox pattern to automation triggers and SLA follow-up (one small intent row, the existing job, the deterministic keys already in place); add attempts and last error to the outbox row; store the message id on the item. Not a general event framework.

## RC-2. No rule for who is a party to a ticket
The requester, the participants (a set of addresses with no record of who added them or from which message), message authors and merged tickets' requesters are decided separately by inbound mail, the portal, merge and outbound Cc. Nothing ties a message author or a participant to the ticket's parties, so each new path can add one without the others noticing.
- **Explains:** MSG-002, SEC-006 (same issue), MSG-003, BE-001, SEC-004 and BE-002 (same issue), MSG-012, ARC-003 (root-cause entry).
- **Systemic or local:** systemic.
- **One fix:** one small domain rule used by the mail bridge, merge and the Cc builder: a correlated message attaches only from the requester or an existing participant; participants record who added them, are capped and removable; a merge across different requesters has an explicit rule. Not a participant framework or a roles system.

## RC-3. The mail outcome crosses module boundaries as strings
The Mail module has explicit Uncertain and bounce handling; Ticketing's delivery state has five values and encodes "uncertain" and "bounced" as Failed plus a code string. The literal `outcome_uncertain` appears in the Mail module (several times), in a SQL predicate that separates failed from uncertain for the dashboard and queue filter, and in the browser (hiding the Retry button). A retry of an already-submitted message keeps its provider draft id, so the sender finds the sent copy and skips the send.
- **Explains:** MSG-001, BE-009, MSG-007, QA-002, ARC-001 (root-cause entry).
- **Systemic or local:** partly systemic; the MSG-001 defect itself is local.
- **One fix:** local first (a retry of a submitted message clears the draft id and is a recorded re-send; guard the delivery report), then explicit Uncertain and Bounced states so no string comparison remains.

## RC-4. Convention-only isolation outside the ticket scope filter
Only ten Ticketing entities carry the department scope filter. Everything else in Ticketing, and every query in Mail, Knowledge and Audit, relies on each query repeating a department condition. The architecture tests pin where the scope bypass is called and what permission each endpoint declares, but nothing asserts what an administrative endpoint may return. Administrative permissions pass for a Platform Admin without membership, so any administrative endpoint that returns content-derived data leaks to that role.
- **Explains:** SEC-001, SEC-005, SEC-012, QA-007, ARC-002 (root-cause entry).
- **Systemic or local:** systemic but small in extent: two endpoints today.
- **One fix:** one stated rule (content-derived fields need ticket read by membership; administrative permission returns configuration only), the two endpoint fixes, and one probe test that calls every administrative GET as a non-member Platform Admin.

## RC-5. Lifecycle rules decided once and not re-evaluated
The SLA policy is chosen once at creation, before automation routes or classifies the ticket; the first-response clock is independent of ticket status; lifecycle timestamps are overwritten on reopen. Each is a rule that made sense at one moment and does not follow later changes.
- **Explains:** BE-006, BE-007, BE-014, DATA-006, DATA-007, and the reporting meaning of stored SLA events.
- **Systemic or local:** local to the SLA and lifecycle code; needs two product decisions (D17, D18), not design work.
- **One fix:** one decision session, then a small change in one module.

## RC-6. Replay, idempotency and staleness gaps at the write edges
The core is strict (versions, row locks, unique keys), but several client-initiated writes carry no replay or staleness protection: public message posts (agent and portal) have no idempotency key, a portal reply is not atomic with its attachments, bulk edit ignores versions, the first save of preferences can race, and some client state survives route changes.
- **Explains:** BE-003, BE-010, QA-004, UX-016, UX-003, QA-001, BE-013.
- **Systemic or local:** local, repeated.
- **One fix:** an optional client-generated message id used as the unique source reference (the mechanism already exists for portal creation and email); optional per-row versions for bulk.

## RC-7. Production hosting is not yet a finished deliverable
The build targets development. Production hosting, key persistence, backup and restore, metrics, proxy settings, start-up guards, migration policy and runbooks are missing or undocumented.
- **Explains:** SRE-001 to SRE-014, SEC-009, SEC-010, SEC-011, ARC-007.
- **Systemic or local:** systemic, outside the application code.
- **One fix:** the planned deployment work (WS-5 to WS-7).

## RC-8. Architecture decision and documentation debt
Significant decisions since ADR-015 are recorded in long bullets in `TICKETING.md` rather than as ADRs: the notification outbox interceptor, the requester identity-link model and form audiences (ADR-015 was amended inline), presence and mentions, the per-user preference table, the Knowledge reply-link contract, and the meaning of administrative permission for a Platform Admin. Several documents contradict locked rules or describe features that do not exist.
- **Explains:** DOC-001 to DOC-011, ARC-006, ARC-008, ARC-009.
- **Systemic or local:** systemic in process, cheap to fix.
- **One fix:** write the missing ADRs (a page each, from the existing text) and refresh the documents.

## RC-9. Headline figures defined separately
Open work is restated in at least four other files besides the shared predicate; the sidebar counts, the queue list and the dashboard use different definitions; "awaiting first response" is an SLA figure; merged duplicates are easy to count.
- **Explains:** DATA-001, DATA-002, DATA-005, ARC-005.
- **Systemic or local:** systemic, easy to fix once decided.
- **One fix:** one shared definition of "counted ticket", "open work" and "awaiting first response" used by the sidebar, the list, the dashboard and any future report.

## Which blockers trace to which cause
B-1 (SEC-001) traces to RC-4, B-2 (MSG-002) to RC-2, B-3 (MSG-001) to RC-3, B-5 to B-9 to RC-7 (B-9 also RC-1). B-4 (MSG-004), B-10 (SEC-003) and B-11 (SEC-002) have no architectural cause: a missing verification step and decision, the abuse-controls gate, and the attachment-scanning gate.

---

# 8. Remediation Workstreams

Sixteen workstreams in five bands. Each of the 118 findings belongs to exactly one workstream. Nothing here is implementation code, and no workstream introduces a new subsystem, framework or dependency; each reuses an existing mechanism. Suggested tests are targeted tests only, following the project's testing rules (no full suites).

## P0. Security, data and mail correctness

### WS-1. Administrative endpoint isolation
- **Goal:** no content-derived data reaches a Platform Admin without a membership through an administrative endpoint, the rule is written down, and a test detects recurrence.
- **Findings closed:** SEC-001, SEC-005, SEC-012, ARC-002, QA-007.
- **Decisions first:** none for the endpoint fixes. D14 for the documented boundary; D30 is optional.
- **Dependencies:** none.
- **Suggested implementation order:** (1) mail attention: withhold subject and sender address from callers without ticket read in that department; (2) automation history: return it only to callers who hold ticket read in that department, or scope the entity; (3) after D14, state the rule and the Platform Admin boundary in the permissions document; (4) add the probe test.
- **Suggested targeted tests:** a non-member Platform Admin calling the mail attention list and the automation history gets no subject, address or ticket reference; a member Department Admin still gets them; one probe test that calls every administrative GET as a non-member Platform Admin and asserts no content-classified field appears.
- **Completion criteria:** both endpoints fixed and covered; the probe test passes; the rule and the boundary are documented.

### WS-2. Ticket-party rule (inbound sender, participants, merge)
- **Goal:** one rule decides who may attach to a ticket, who may be a participant, and what happens to parties on merge.
- **Findings closed:** MSG-002, SEC-006, MSG-003, BE-001, SEC-004, BE-002, ARC-003, MSG-012.
- **Decisions first:** D8 and D9.
- **Dependencies:** none technical. Land it before the D-16 tenant run so the run exercises the final threading behaviour.
- **Suggested implementation order:** (1) the minimum that needs no decision: never create participants from the To/Cc of a message sent by a non-participant; (2) the sender rule as decided in D8; (3) the conversation-id decision; (4) participant provenance, cap, the agent removal action if decided, and the Cc recipients shown by name in the composer; (5) the merge rule as decided in D9; (6) the correlation-token wording (MSG-012) once the rule is settled.
- **Suggested targeted tests:** a message with a valid In-Reply-To from a stranger does not attach, reopen or add participants; Cc recipients added by the requester are still added; the cap is enforced; an agent can remove a Cc recipient (if decided); after a cross-requester merge both requesters have the outcome decided in D9; the composer lists Cc recipients by name.
- **Completion criteria:** behaviour matches D8 and D9; tests pass; the participant model and the merge behaviour are documented.

### WS-3. Mail delivery-state and retry correctness
- **Goal:** retry, bounce and uncertain states behave truthfully and the outcome no longer travels as strings.
- **Findings closed:** MSG-001, BE-009, MSG-007, MSG-008, MSG-010, MSG-006, ARC-001.
- **Decisions first:** none.
- **Dependencies:** land before the D-16 tenant run. MSG-006, MSG-007 and the final shape of the acknowledgement header depend on real header samples (E-37, E-38).
- **Suggested implementation order:** (1) MSG-001: a retry of an already-submitted message clears the draft id and submit flag and is recorded as a deliberate re-send (or Retry is blocked for a delivery failure); (2) BE-009: ignore a delivery report older than the stored one; (3) ARC-001: explicit Uncertain and Bounced states; (4) MSG-008: automatic read-only reconciliation of uncertain sends after a delay; (5) MSG-006: mark acknowledgements as automatic; (6) MSG-007: require a bounce to come from the tenant's own transport once real headers are known; (7) MSG-010: treat same Message-ID, subject and sender as a duplicate regardless of the time gap.
- **Suggested targeted tests:** send, bounce, retry, assert a second provider send; a late or replayed delivery report cannot overwrite a newer one; an uncertain message found in Sent Items is marked Sent without a send; an acknowledgement carries the suppression header (simulator); a forged bounce does not change a reply's state once the rule exists.
- **Completion criteria:** the tests pass; the dashboard and queue filter use explicit states, not the string; the tenant run confirms the real behaviour.

### WS-4. Mailbox registration and tenant scoping
- **Goal:** only an authorised actor can register a mailbox, and only mailboxes inside the application's scope can be registered.
- **Findings closed:** MSG-004.
- **Decisions first:** D10.
- **Dependencies:** the D-16 tenant evidence (E-2, E-3) is needed to prove the scoping.
- **Suggested implementation order:** decision; creation-time probe; authority restriction as decided; tenant verification.
- **Suggested targeted tests:** registering an out-of-scope mailbox is refused (simulator); only the permitted role can register.
- **Completion criteria:** the decision is implemented and the tenant scoping evidence is recorded in the D-16 run.

## P1. Production readiness

### WS-5. Hosting and security hardening
- **Goal:** a defined production deployment with trustworthy proxy handling and safe start-up.
- **Findings closed:** SRE-001, SRE-006, SRE-010, SRE-014, SEC-011, SEC-009, SEC-010.
- **Decisions first:** D1.
- **Dependencies:** none; verify SRE-006 first.
- **Suggested implementation order:** (1) verify the forwarded-header default; (2) production artefact and proxy configuration per D1; (3) production start-up guards (storage provider, scan requirement); (4) pin image tags and set allowed hosts; (5) security headers for the application page; (6) WebSocket origin allow-list; (7) restrict `data:` links to images.
- **Suggested targeted tests:** start-up fails in production with in-memory storage or the scan requirement off; the forwarded-header check; the sanitiser keeps `data:` only on images.
- **Completion criteria:** the reference configuration serves the application and live updates behind the proxy (a deployment check, which needs permission to run); the headers are present on the application page.

### WS-6. Recovery: backup and restore, Data Protection keys, migration policy
- **Goal:** the system can be restarted, replaced and restored without losing sessions or data.
- **Findings closed:** SRE-002, SRE-003, SRE-007, SRE-008, ARC-007.
- **Decisions first:** D2, D3, D5, D6.
- **Dependencies:** none.
- **Suggested implementation order:** persistent key store; backup procedure and script for the database and attachment store; one recorded restore drill; the migration-order paragraph and a test that the registered orders satisfy the foreign-key dependencies; the supported topology statement.
- **Suggested targeted tests:** sessions survive a container replacement; the migration-order test; the restore drill record.
- **Completion criteria:** a recorded restore drill exists (a backup alone is not recoverability).

### WS-7. Observability and operations
- **Goal:** operators can see stuck or failing work and diagnose a dependency outage.
- **Findings closed:** SRE-004, SRE-005, SRE-009, SRE-011, SRE-012, SRE-013.
- **Decisions first:** D4; D7 for pruning.
- **Dependencies:** the outbox age signal is easier after WS-9 adds attempts and last error; pruning waits on D-09.
- **Suggested implementation order:** counters (oldest pending outbox age, failed jobs, last mail-sync age, SLA job lag); readiness per D4; log redaction; dependency-down runbook; worker sizing and lock review; pruning after D-09.
- **Suggested targeted tests:** readiness fails when the object store is down; the outbox age metric moves with a pending row; log templates carry no addresses or subjects.
- **Completion criteria:** each stuck-work condition named in SRE-005 has a signal; the runbook exists.

### WS-8. Abuse controls and attachment safety
- **Goal:** one account cannot exhaust the system, and risky attachments are blocked until a scanner exists.
- **Findings closed:** SEC-002, SEC-003, MSG-005.
- **Decisions first:** D11, D13 (and the D-08 engine choice).
- **Dependencies:** none.
- **Suggested implementation order:** built-in rate limiter with per-user policies and explicit multipart limits; extend blocked types; acknowledgement control per D11; scanner when chosen.
- **Suggested targeted tests:** the limiter rejects the excess; the multipart limit holds; each added blocked type is rejected; the acknowledgement cap holds.
- **Completion criteria:** the abuse gate and the attachment policy are closed as decided.

## P2. Workflow, data integrity and reliability

### WS-9. Durable after-commit processing
- **Goal:** automation, SLA follow-up and notifications survive restarts and persistent failures.
- **Findings closed:** BE-004, BE-005, BE-015, ARC-004.
- **Decisions first:** D16.
- **Dependencies:** the pattern already exists (notification outbox).
- **Suggested implementation order:** attempts and last error on the outbox row (BE-005); store the message id on the item (BE-015); automation and SLA follow-up intent per D16.
- **Suggested targeted tests:** a restart between commit and dispatch still runs the rule; one persistently failing item does not block others; a late drain classifies by the event's message.
- **Completion criteria:** the tests pass; the deterministic keys prevent repeats.

### WS-10. Replay, idempotency and staleness protection
- **Goal:** a retried or stale client request cannot duplicate or overwrite work.
- **Findings closed:** BE-003, BE-010, QA-004, UX-016, UX-003, QA-001, BE-013.
- **Decisions first:** D19.
- **Dependencies:** none.
- **Suggested implementation order:** optional client message id as the unique source reference (agent and portal); portal reply with attachments as one outcome; first-save race for preferences; realtime reconcile on every reconnect path; Knowledge editor cache after a partial failure; portal state keyed by route; per-row versions for bulk if D19 chooses them.
- **Suggested targeted tests:** a duplicate post returns the earlier result; an attachment failure leaves a single reported outcome; two concurrent first saves both succeed; a reconnect refetches the lists; a failed publish does not leave a stale version.
- **Completion criteria:** the targeted tests pass.

### WS-11. Test debt for recovery and concurrency
- **Goal:** the recovery and concurrency paths that production depends on are covered.
- **Findings closed:** QA-002, QA-003, QA-005, QA-006, QA-008.
- **Decisions first:** none.
- **Dependencies:** best done alongside the related fixes (WS-3, WS-9, WS-10).
- **Suggested implementation order:** stuck-Sending recovery for both branches; a negative control for the raw-SQL requester link; sentinel-based negative realtime assertions; recurring job registration; bulk edit of a merged source.
- **Suggested targeted tests:** these are the tests.
- **Completion criteria:** each listed gap has a passing targeted test.

## P3. ITSM, reporting and operations

### WS-12. SLA lifecycle and policy re-evaluation
- **Goal:** SLA clocks and policy selection behave the way the desk expects, and stored events keep one meaning.
- **Findings closed:** BE-006, BE-007, BE-014, BE-008, DATA-007.
- **Decisions first:** D17 and D18.
- **Dependencies:** settle before WS-14 (the reporting definitions depend on them).
- **Suggested implementation order:** decide; change the SLA code in one module; order the SLA sort so tickets without a deadline come last (BE-008, no decision); document where history lives (BE-014); label the two "about to breach" concepts (DATA-007).
- **Suggested targeted tests:** re-evaluation scenarios as decided; a ticket resolved without a reply does not record a breach afterwards; the SLA sort includes tickets without a deadline.
- **Completion criteria:** behaviour matches D17 and D18; tests pass.

### WS-13. Service-desk workflow clarity
- **Goal:** agents, leads and requesters can tell whose turn it is and can hand work on.
- **Findings closed:** ITSM-001 to ITSM-007, MSG-009, BE-012, SEC-007, SEC-008.
- **Decisions first:** D20, D21, D22, D23, D24, D12, D15. Settle D22 before the portal is opened to requesters.
- **Dependencies:** D20 relates to DATA-002 (WS-14).
- **Suggested implementation order:** D22 first; then D20 and D21; then D23, D24, D12; the setup overview (ITSM-006); identity-case paging (BE-012); the directory search and address-trust items last.
- **Suggested targeted tests:** per decision made (for example the chosen needs-response signal, the macro picker behaviour for Agents, a portal submission without a mailbox, the resolve prompt).
- **Completion criteria:** each decision implemented as made; ITSM-007 and the four enhancements remain optional.

### WS-14. Reporting foundation and audit-retention interaction
- **Goal:** one set of definitions for the headline figures, and a history source that survives any retention policy.
- **Findings closed:** DATA-001 to DATA-006, DATA-008, DATA-009, ARC-005, MSG-011.
- **Decisions first:** D7, D25, D26, D27, and D18.
- **Dependencies:** WS-12 and the D-09 policy; do not start Historical Reports before these are settled.
- **Suggested implementation order:** shared definitions of counted ticket, open work and awaiting first response; fix the sidebar SLA badges (a bug, no decision) and the count definitions per D25; consistent creation events; the retention separation per D7; label approximate counts; decide whether any uncaptured history is wanted (DATA-008).
- **Suggested targeted tests:** each sidebar badge equals the list it opens; awaiting-first-response equals the message-stream definition; merged sources are excluded or included as decided; the three creation events share one payload.
- **Completion criteria:** definitions written once and used everywhere; the retention approach decided; no stored snapshot or warehouse is needed.

## P4. UX, accessibility and polish

### WS-15. UX and accessibility
- **Goal:** the interface is consistent and accessible at laptop width.
- **Findings closed:** UX-001, UX-002, UX-004 to UX-015, UX-017, UX-018, UX-019.
- **Decisions first:** D28 and D29.
- **Dependencies:** none.
- **Suggested implementation order:** the dialog and shortcut contract (UX-001, 006, 007, 008, 013 share one cause); the notification deep link; accessible names for row actions; confirmations (saved views, mailbox deactivation); the bulk bar and retry permission; portal feedback; remaining small items.
- **Suggested targeted tests:** shortcut suppression behind dialogs; the bulk bar clearing hidden choices; component tests for the changed dialogs.
- **Completion criteria:** each finding fixed or consciously deferred; tests for changed components pass.

### WS-16. ADR, documentation and copy debt
- **Goal:** documents tell the truth and decisions are recorded where reviewers look.
- **Findings closed:** DOC-001 to DOC-011, ARC-006, ARC-008, ARC-009, SEC-013, BE-011, BE-016.
- **Decisions first:** none.
- **Dependencies:** write the ADRs after the related decisions are made.
- **Suggested implementation order:** documents that contradict locked rules (DOC-001, DOC-002, DOC-004, DOC-003); the missing ADRs (outbox interceptor, identity links and audiences, presence and mentions, user preferences, reply-link contract, the meaning of administrative permission); the cross-module contract precondition (ARC-008); the client contract note (ARC-009); copy and terminology; small hygiene items.
- **Suggested targeted tests:** none beyond lint and spelling for changed text.
- **Completion criteria:** no document contradicts a locked rule; the ADRs exist.

---

# 9. Recommended Remediation Sequence

The order below uses the Architect's analysis so each root cause is fixed once. Fixes that need no decision start first.

1. **Decisions, no code.** Make D8, D9, D10 and D22 (they gate four blockers or the portal opening), then D1, D2, D3 and D13 (they gate the production-readiness blockers). The workstreams that need no decision can start at once.
2. **MSG-001, retry after a bounce (WS-3).** Local, no decision, closes blocker B-3. Land it with the guard on the delivery report.
3. **SEC-001 and SEC-005, administrative isolation (WS-1).** No decision for the fix, closes blocker B-1. Add the probe test at the same time so the pattern cannot recur.
4. **The ticket-party and participant model (WS-2)** once D8 and D9 are made, closing blocker B-2 and the related findings in one change. Begin with the minimum that needs no decision (no participants from non-participants).
5. **Mailbox registration authority (WS-4)** once D10 is made, with the tenant scoping evidence in step 7.
6. **The rest of WS-3** (explicit uncertain and bounced states, acknowledgement marking, bounce trust once real headers are known).
7. **Run the D-16 tenant validation** after steps 2 to 6, so the real-tenant run exercises the final threading and retry behaviour rather than code that is about to change.
8. **Production readiness (P1).** Verify SRE-006 first (one check), then WS-5, WS-6, WS-7 and WS-8. WS-6 counts as closed only with a recorded restore drill.
9. **Durability and replay (P2):** WS-9, WS-10, WS-11, with the tests written alongside the fixes.
10. **SLA, workflow and reporting (P3):** settle D17 and D18 first, then WS-12, WS-13 (D22 before the portal opens), and WS-14. Do not start Historical Reports before the WS-14 definitions and the retention separation (D7) are settled.
11. **UX, accessibility and documentation (P4):** WS-15 and WS-16; the documentation fixes that remove contradictions with locked rules can be done at any time.
12. **After the P0 and P1 fixes,** ask for permission to run broader verification: a full regression run, a broad browser pass, load and query testing, and the restore drill. These have not been run and are the evidence readiness needs.

Shared fixes that close several findings at once:

| Fix | Closes |
|---|---|
| One ticket-party rule (sender must be requester or participant; participants have provenance, a cap and removal) | MSG-002, SEC-006, MSG-003, BE-001; shapes SEC-004 and BE-002 |
| Reuse the outbox pattern for automation and SLA follow-up; add attempts and last error | BE-004, BE-005, BE-015, ARC-004 |
| Classify content on administrative endpoints, fix two endpoints, add one probe test | SEC-001, SEC-005, SEC-012 (structure), ARC-002 |
| Clear the draft id on retry, guard the delivery report, then explicit uncertain and bounced states | MSG-001, BE-009, MSG-007, ARC-001 |
| One shared definition of counted ticket, open work and awaiting first response | DATA-001, DATA-002, DATA-005, ARC-005 |
| Optional client message id as the unique source reference | BE-003 (agent and portal), part of QA-004 |
| Decide D17 and D18 once | BE-006, BE-007, BE-014 and the meaning of stored SLA events |

---

# 10. ITSM / Workflow Findings

Specialist: ITSM Product & Service Desk Lead. Seven findings (three Medium, four Low), no production blockers, five decisions (D20 to D24). Four service-desk enhancements are listed separately and are not defects. Nothing in this review proposes an excluded feature (tags, forwarding, time tracking, scoring, leaderboards, CRM).

## 10.1 Findings

| ID | Severity | Finding | Decision | Workstream |
|---|---|---|---|---|
| ITSM-001 | Medium | After an agent answers, the ticket stays Open (and possibly Unassigned) and looks untouched; the queue cannot separate "needs us" from "answered". | D20 | WS-13 |
| ITSM-002 | Medium | Agents cannot change a ticket's team or hand it to a colleague, and macros that do are invisible to them. | D21 | WS-13 |
| ITSM-003 | Medium | A ticket without a stored mailbox can never be answered by email and nothing repairs it. Should be settled before the portal is opened to requesters. | D22 | WS-13 |
| ITSM-004 | Low | The portal status "Waiting" does not say whose turn it is. | D23 | WS-13 |
| ITSM-005 | Low | Resolving or closing without a public message tells the requester nothing, and nothing auto-closes a resolved ticket. | D24 | WS-13 |
| ITSM-006 | Low | The department setup overview can say "1 active form" when no requester can use it. | No | WS-13 |
| ITSM-007 | Low | New email tickets are unrouted; only an administrator-built rule routes them. | No (optional) | WS-13 |

## 10.2 Related findings from other disciplines

- BE-002 / SEC-004: merging tickets of different requesters orphans the source requester (D9).
- BE-006 / BE-007: SLA policy chosen before routing; first-response clock continues after resolution without a reply (D17, D18).
- MSG-009: ignored inbound mail cannot be released to a ticket (D12).
- DATA-002 and DATA-001: "awaiting first response" and queue counts (D26, D25).
- BE-012: requester identity cases capped at 100 with no paging.
- UX-005: mailbox deactivation has no warning (D28).

## 10.3 Enhancements (not defects, not counted)

- E-1: notify a team when an unassigned ticket arrives in its queue (backlog item).
- E-2: notify the assignee or lead of an SLA warning or breach (backlog item, Phase 5).
- E-3: a view or dashboard figure for Pending tickets that have waited a long time.
- E-4: a default team on a mailbox, so email arrives already routed (see ITSM-007).

## 10.4 Workflow assessment

The queue-first workspace largely exposes what an agent or team lead needs: My, Team, Unassigned, All and Following queues, saved views, a default view, columns and quick filters, and a dashboard with no per-agent ranking. Acknowledgements are per department and channel, sent from the ticket mailbox with no agent signature, and do not count as a human first response. The gaps are in handing work on, showing whose turn it is, and the mailbox-less ticket case.

---

# 11. Messaging / Graph Findings

Specialist: Microsoft 365 / Messaging Engineer. Twelve findings (two High, four Medium, five Low, one Polish), three production blockers (MSG-001, MSG-002, MSG-004) plus the standing gate D-16. Five decisions (MSG-002, MSG-003, MSG-004, MSG-005, MSG-009). Three findings from other disciplines (SEC-004/BE-002, SEC-006, BE-009) overlap this area.

## 11.1 Evidence classes

- **Code evidence:** what the implementation does when read.
- **Simulator evidence:** behaviour of the Fake mail provider and recorded-shape tests. Not tenant evidence.
- **Real tenant evidence:** none exists. The D-16 result table for E-1 to E-38 is empty, so every statement about what Graph or Exchange actually does is unverified.

## 11.2 Findings supported by code or simulator evidence

| ID | Severity | Finding | Evidence |
|---|---|---|---|
| MSG-001 | High | Retry after a confirmed bounce sends nothing and marks the message Sent (the retry keeps the old provider draft, finds the original in Sent Items and skips the send). | Code; behaviour on a real tenant is still to be confirmed (E-25, E-12, E-10) |
| MSG-002 | High | Correlated mail is accepted from any sender; the sender and every To/Cc address join the ticket, which reopens, and later replies reach them. (Same issue as SEC-006.) | Code |
| MSG-005 | Medium | Acknowledgements go to an unauthenticated sender and can carry attacker-chosen subject text; rotating senders bypass the per-sender limit. | Code |
| MSG-008 | Low | Uncertain sends are never reconciled automatically. | Code |
| MSG-009 | Low | Ignored inbound mail cannot be released to a ticket from the attention list. | Code |
| MSG-010 | Low | A re-delivered message with the same Message-ID after two seconds becomes a second ticket. | Code |
| MSG-011 | Low | Full bodies of all inbound mail, including ignored and bounce mail, are kept with no purge (waits on D-09). | Code |
| MSG-012 | Polish | Every reply shows an opaque correlation token to the requester. | Code |

## 11.3 Findings that need tenant verification

| ID | Severity | Finding | Tenant experiments needed |
|---|---|---|---|
| MSG-003 | Medium | The Exchange conversation id is accepted as sole evidence although it behaves like a subject match. | E-15 to E-17 (conversation id derivation) |
| MSG-004 | Medium | Any Department Admin can register an arbitrary mailbox; only Exchange-side scoping stops them, and that scoping is unverified. | E-2, E-3 (application access scoping) |
| MSG-006 | Medium | Acknowledgements are not marked as automatic, so peer auto-responders cannot suppress replies. | E-37, E-38 (loop and header samples) |
| MSG-007 | Low | Any sender can pose as a bounce and mark a sent reply as failed. | E-37, E-38 (real bounce header samples) |
| MSG-001 (real path) | High | Confirmation of the retry behaviour against real sent-copy lookup. | E-25, E-12, E-10; E-21/E-22 (sender identity); E-23/E-24/E-26 (reply headers); E-13/E-14 (Message-ID behaviour); E-30 to E-35 (subscriptions) |

## 11.4 What is well built

Sender identity from the ticket's own mailbox with no silent fallback; the outbound state model (Queued, Sending, Sent, Failed, Uncertain); never retrying an uncertain send blindly; honest "accepted for delivery, not confirmed as delivered" wording; evidence pointing into another department or mailbox discarded; a subject reference alone never attaches; loop and noise protection. None of this has been proved against a live tenant.

## 11.5 Summary of the SEC-006 conclusion

A correlated inbound email is attached on identifier evidence alone. Message-IDs and tokens are capabilities, not identities: everyone who received a message in the thread can see them, and the token is printed in the visible footer of every reply. The safe rule recommended in the review is that a message attaches only when the evidence is strong and the sender is the ticket's requester or an existing participant. What happens to other mail is a product choice (D8).

---

# 12. Security / Privacy Findings

Specialist: Security / Privacy Engineer. Thirteen findings (one High, three Medium, eight Low, one Polish). Blockers: SEC-001, SEC-002, SEC-003. Decisions: D13 (SEC-002), D14 (SEC-012), D15 (SEC-007), D8 and D9 (SEC-006, SEC-004). Related Messaging findings (MSG-002, MSG-004, MSG-005, MSG-007) carry the larger security consequences in the mail area.

## 12.1 Findings

| ID | Severity | Finding | Blocker | Decision |
|---|---|---|---|---|
| SEC-001 | High | A Platform Admin without membership reads sender addresses and subjects of any department's failed, uncertain and ignored mail (confirmed at runtime; the same caller gets 404 on the dashboard). | B-1 | No (D14 for the boundary) |
| SEC-002 | Medium | The attachment policy is a short extension blocklist with no scanner behind it (D-08). | B-11 | D13 |
| SEC-003 | Medium | No rate limiting or abuse controls; large uploads are buffered in memory. | B-10 | No |
| SEC-004 | Medium | Merging tickets of different requesters shares one conversation with both people (duplicate of BE-002). | No | D9 |
| SEC-005 | Low | Automation execution history is not covered by the department scope filter. | No | No |
| SEC-006 | Low | Correlated inbound email is accepted from any sender (duplicate of MSG-002, escalated there). | (B-2) | D8 |
| SEC-007 | Low | The Entra mail claim is treated as a verified address when linking and refreshing requester contact data. | No | D15 |
| SEC-008 | Low | Any Department Admin can search the whole organisation's directory by name or email. | No | No |
| SEC-009 | Low | The SignalR hub has no WebSocket origin allow-list (low confidence; relies on a recalled framework default). | No | No |
| SEC-010 | Low | The sanitiser keeps `data:` links in inbound HTML and signatures. | No | No |
| SEC-011 | Low | The application page carries no clickjacking, content-type or CSP headers; the host does not serve it. | No | No |
| SEC-012 | Low | A Platform Admin's reach over non-content department data (mail retry, Knowledge drafts, form audience lists) is implicit and undocumented. | No | D14 |
| SEC-013 | Polish | Knowledge markdown accepts a `/\host` value as an in-app link. | No | No |

## 12.2 Areas reviewed with no issue found

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

## 12.3 Could not be verified

- Runtime behaviour of `article-links`, `queue-types` and `queue-preferences` (the dev API may predate commit `081f660`); judged from code.
- SEC-005 at runtime (the DEMO department has no automation rules).
- SEC-009: the framework's default WebSocket origin behaviour was recalled, not exercised.
- Live Entra claim behaviour (SEC-007) and anything involving a real Graph tenant (D-16 stays open).
- Object-store access policy in a real deployment (only the dev compose file exists, SRE-001).
- Agent-side upload endpoints and the full set of attachment size and scan flows beyond the validator and download path.
- No fuzzing of guessed IDs across every endpoint; the existing isolation test matrix was not run.
- Markdown and rich-text components beyond the files named, and every admin screen's rendering.

## 12.4 Privacy notes

No compliance or certification claim is made. Privacy-relevant findings: raw mail bodies retained without a purge (MSG-011, D-09); log privacy resting on convention with the documented redaction denylist not implemented (SRE-012); the audit stream as both history source and retention collision point (DATA-003); subjects shown to a non-member Platform Admin (SEC-001).

---

# 13. Backend / Reliability Findings

Specialists: Backend / Domain Engineer (16 findings), QA / Reliability Engineer (8), Lead Architect (9, with four root-cause entries). No finding in this group is a standalone production blocker. BE-009 and BE-005 are supporting backend findings related to blockers recorded elsewhere (B-3 / MSG-001 and B-9 / SRE-005 respectively), and the root-cause entries explain blockers rather than being blockers themselves.

## 13.1 Backend / Domain (BE)

| ID | Severity | Finding | Decision | Workstream |
|---|---|---|---|---|
| BE-001 | Medium | Ticket participants have no cap and cannot be removed. | D8 | WS-2 |
| BE-002 | Medium | Merging tickets of different requesters orphans the source requester (master entry for SEC-004). | D9 | WS-2 |
| BE-003 | Medium | Posting a public message has no idempotency key; the portal reply is not atomic with its attachments. | No | WS-10 |
| BE-004 | Medium | Automation and SLA follow-up after commit have no stored intent and are lost on a restart. | D16 | WS-9 |
| BE-005 | Medium | One persistently failing notification outbox item can block the queue; attempts and errors are not kept. | No | WS-9 |
| BE-006 | Medium | The SLA policy is chosen once at creation, before automation routes or classifies the ticket. | D17 | WS-12 |
| BE-007 | Medium | The first-response clock keeps running after resolve or close without a public reply. | D18 | WS-12 |
| BE-008 | Medium | Sorting the queue by SLA hides every ticket that has no deadline. | No | WS-12 |
| BE-009 | Low | The delivery-state report has no transition rules. | No | WS-3 |
| BE-010 | Low | Bulk edits apply to whatever state a ticket is in now. | D19 | WS-10 |
| BE-011 | Low | Invalid references answer 400 in some places and 409 in others. | No | WS-16 |
| BE-012 | Low | Requester identity resolution is capped at 100 cases with no paging or search. | No | WS-13 |
| BE-013 | Low | Saving queue preferences for the first time twice at once returns a server error. | No | WS-10 |
| BE-014 | Low | Lifecycle timestamps are overwritten, so the first resolution time is not kept. | No | WS-12 |
| BE-015 | Low | A queued notification is classified from the ticket's state at drain time, not event time. | No | WS-9 |
| BE-016 | Polish | A validation message lists the wrong allowed sources. | No | WS-16 |

## 13.2 QA / Reliability (QA)

| ID | Severity | Finding | Workstream |
|---|---|---|---|
| QA-001 | Medium | The realtime client does not reconcile portal data after a long outage. | WS-10 |
| QA-002 | Medium | Stuck-Sending outbound recovery is untested. | WS-11 |
| QA-003 | Medium | The portal test helper links requesters by raw SQL, so a regression in the real link path could go unnoticed. | WS-11 |
| QA-004 | Medium | Concurrency and duplicate coverage stops at ticket and organisation writes (related: BE-003, BE-010). | WS-10 |
| QA-005 | Low | Negative realtime leak tests rely on fixed sleeps. | WS-11 |
| QA-006 | Low | Recurring-job registration is never verified. | WS-11 |
| QA-007 | Low | Structural guard coverage is Ticketing only; nothing asserts what an administrative endpoint may return to a non-member Platform Admin. | WS-1 |
| QA-008 | Polish | Merged-source bulk edit untested; a fixed wait in an end-to-end test. | WS-11 |

## 13.3 Architecture (ARC)

ARC-001 to ARC-004 are root-cause entries (see section 7: RC-3, RC-4, RC-2, RC-1). ARC-005 (open work restated in four other files), ARC-006 (decisions since ADR-015 recorded in long bullets, not ADRs), ARC-007 (cross-schema coupling and migration order unwritten and untested), ARC-008 (a cross-module contract carries a security precondition only in prose) and ARC-009 (one very large service, hand-written client contract with no drift check) are Low. The Lead Architect's conclusion is that the module structure should not be restructured; the issues are missing shared rules, not a wrong shape.

## 13.4 Operations (SRE)

Fourteen SRE findings: four High (SRE-001, 002, 003, 005), seven Medium (SRE-004, 006 to 011), three Low (SRE-012 to 014). Five of the High and Medium findings are production blockers (section 4). Remediation is in WS-5, WS-6 and WS-7.

---

# 14. Reporting / Data Readiness

Specialist: Data / Reporting Engineer. Nine findings (three Medium, six Low), no blockers, four decisions (D7, D25, D26, D27). This review examined whether Historical Reports can be built truthfully; it did not build any.

## 14.1 Conclusion

Existing data can support truthful future reports without invented snapshots, for ticket volume, backlog, ageing, source, team, type, first response, resolution, reopen, SLA attainment (with coverage) and delivery outcomes. Messages are immutable and timestamped; every status, priority, team, assignee, type and merge change is written to an append-only audit stream with previous and next values; every SLA transition is an event. Backlog and ageing are reconstructable exactly by replaying events. **Nothing here requires a new data store, a warehouse, or stored snapshots.** A daily snapshot must not be invented from current state.

## 14.2 Findings

| ID | Severity | Finding | Decision | Workstream |
|---|---|---|---|---|
| DATA-001 | Medium | Sidebar queue counts use different definitions from the queue list and dashboard; SLA badges count resolved and closed tickets. | D25 | WS-14 |
| DATA-002 | Medium | "Awaiting first response" is silently an SLA figure and reads zero when no policy applies. | D26 | WS-14 |
| DATA-003 | Medium | The append-only audit stream is both the history source and the place retention would have to change. | D7 | WS-14 |
| DATA-004 | Low | The three ticket-creation audit events have different shapes. | No | WS-14 |
| DATA-005 | Low | A merged ticket keeps its status and is easy to count by mistake. | D27 | WS-14 |
| DATA-006 | Low | Lifecycle columns hold the current value only; history lives in events. | No | WS-14 |
| DATA-007 | Low | Two ideas of "about to breach", and an age figure mixing waiting on us with waiting on the requester. | No | WS-12 |
| DATA-008 | Low | Some facts are not captured at all (mailbox health history, Knowledge views, why no SLA applied). | No (decision on whether wanted) | WS-14 |
| DATA-009 | Low | One person can be several requesters, so distinct-requester counts are approximate. | No | WS-14 |

## 14.3 What must be settled before Historical Reports

1. Retention and audit separation (DATA-003, D-09, D7).
2. First-response clock after resolution (BE-007) and SLA selection (BE-006); decide, then freeze, because they change the meaning of stored events.
3. Merged duplicates in counts (DATA-005).
4. One definition of "counted ticket", "open work" and "awaiting first response" shared by sidebar, list, dashboard and reports (DATA-001, DATA-002, ARC-005).
5. Explicit uncertain and bounced delivery states (ARC-001) before delivery-outcome reports.
6. One creation-event payload (DATA-004).
7. Whether any uncaptured history is wanted (DATA-008), because it cannot be added retroactively.

## 14.4 Reporting rules to preserve

Operational Dashboard answers "what needs attention now"; Historical Reports answer "what changed over time". Period boundaries are computed in Europe/London at query time. Reports aggregate in the database, not by paging lists (keyset paging on mutable keys can move rows). No report offers an actor dimension; there are no agent rankings, leaderboards or individual scoring.

---

# 15. UX / Accessibility / Polish Findings

Specialist: Frontend / UX Engineer (19 findings, sampled), plus the Documentation / Content Designer (11 findings, grep-based). No blockers. Decisions: D28 (UX-005), D29 (UX-018 context). The review was static; no screen was walked in a browser.

## 15.1 Frontend / UX

| ID | Severity | Finding | Workstream |
|---|---|---|---|
| UX-001 | Medium | Workspace keyboard shortcuts stay live behind open dialogs; a key press can tick a row behind a modal and feed a bulk action. | WS-15 |
| UX-002 | Medium | The deep link from a mention notification to its message is unreliable because the bottom scroll wins. | WS-15 |
| UX-003 | Medium | After a failed publish the Knowledge editor keeps a stale version and every retry conflicts until it is reopened. | WS-10 |
| UX-004 | Medium | Row action buttons in admin tables share identical accessible names. | WS-15 |
| UX-005 | Medium | Mailbox Deactivate is one click with no warning that replies on tickets using that mailbox will not be sent (D28). | WS-15 |
| UX-006 | Low | Reorder arrows lose focus and one reorder dialog can send mismatched orders. | WS-15 |
| UX-007 | Low | Focus is not restored after choosing an item from the ticket More menu. | WS-15 |
| UX-008 | Low | Esc or Close silently discards typed work in some dialogs. | WS-15 |
| UX-009 | Low | The bulk bar keeps a hidden Team or Assignee choice after the selection spans departments; the server rejects it per ticket. | WS-15 |
| UX-010 | Low | Portal My requests tabs use tab roles without the keyboard model. | WS-15 |
| UX-011 | Low | Saved-view Delete has no confirmation and Archive saves the draft without saying so. | WS-15 |
| UX-012 | Low | Retry sending checks the permission against any department rather than the ticket's (UX only; the server authorises). | WS-15 |
| UX-013 | Low | The mention picker's roles do not match its behaviour and results are not announced. | WS-15 |
| UX-014 | Low | The portal file picker silently keeps only five files and there is no feedback after reply or submit. | WS-15 |
| UX-015 | Low | Mailbox edit can overwrite a concurrent rename and the wide table has no overflow handling. | WS-15 |
| UX-016 | Low | Portal drafts and the submission id persist across route changes. | WS-10 |
| UX-017 | Polish | The dashboard subtitle exposes roadmap wording ("reports come later"). | WS-15 |
| UX-018 | Polish | Dead realtime foreign-change code remains and grows state for the session. | WS-15 |
| UX-019 | Polish | A group of small items: breadcrumb links, unnamed table headers, un-debounced search, live regions, notification panel position, loading state, a spacing typo. | WS-15 |

## 15.2 Documentation, copy and terminology

| ID | Severity | Finding | Workstream |
|---|---|---|---|
| DOC-001 | Medium | Two API and email documents say the department default mailbox is a fallback sender, contradicting the locked no-fallback rule. | WS-16 |
| DOC-002 | Medium | Documents claim a web/worker role mode and generated API types that do not exist. | WS-16 |
| DOC-003 | Low | Stale phase and module statements; tags still listed; refinements missing from the plan. | WS-16 |
| DOC-004 | Low | Documents recommend a full suite at milestones, contradicting the project testing rules. | WS-16 |
| DOC-005 | Low | Demo tooling and an operations runbook are missing from the documents. | WS-16 |
| DOC-006 | Low | The automation history screen shows raw codes. | WS-16 |
| DOC-007 | Polish | Roles shown as DepartmentAdmin / TeamLead in the Members list. | WS-16 |
| DOC-008 | Low | Weak confirmation wording; the merge dialog does not make clear the effect. | WS-16 |
| DOC-009 | Polish | Server validation messages repeat the field name. | WS-16 |
| DOC-010 | Polish | The mailbox-assignment question is missing from the open-decisions list; roadmap wording stale (D22). | WS-16 |
| DOC-011 | Polish | Mixed "authorization" / "authorised" spelling in documents. | WS-16 |

Other small items recorded elsewhere: SEC-013 (`/\host` markdown link), MSG-012 (opaque correlation token shown to requesters), BE-016 (wrong allowed-sources validation message), QA-008 (fixed wait in an end-to-end test).

---

# 16. Strengths

The source report states these should not be reworked while fixing the findings above.

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

---

# 17. Final Readiness Assessment

This is a summary of the source report's evidence. It is not a compliance statement, certification or production-readiness claim.

## 17.1 What is strong

The module structure and its enforcement by tests; the department scope filter on tickets, messages and requesters with non-disclosing 404s; Internal Note protection on every path reviewed; the concurrency and idempotency toolkit; transactional outbox patterns for notifications and outbound mail; honest delivery states and the no-fallback sender rule; business-time SLA arithmetic; request forms with explicit audiences; a queue-first workspace and a dashboard with no per-agent ranking; an audit, message and SLA event history that can support truthful reports without snapshots.

## 17.2 What is unsafe today (needs fixing before production)

- A Platform Admin without membership can read mail subjects and sender addresses of any department (SEC-001, B-1; related SEC-005, SEC-012).
- A stranger holding a thread identifier can reopen a ticket, appear to the requester and receive later replies (MSG-002, B-2; related MSG-003, BE-001).
- A bounced reply cannot be retried and is marked Sent (MSG-001, B-3).
- Any Department Admin can register any mailbox, with scoping unverified (MSG-004, B-4).
- No rate limiting or abuse controls (SEC-003, B-10) and a thin attachment policy with no scanner (SEC-002, B-11).

## 17.3 What is incomplete (not yet built)

Production deployment artefact (B-5); persistent Data Protection keys (B-7); backup and restore with a recorded drill (B-8); metrics and a meaningful readiness check (B-9, SRE-004); proxy configuration confirmation (B-6); runbooks for dependency outages (SRE-013); logging redaction (SRE-012); production start-up guards (SRE-010); a malware scanner (D-08); retention tooling (D-09); a stored-intent design for automation and SLA follow-up (BE-004).

## 17.4 What needs a product decision

Thirty decisions in section 6. Those that gate blocker work: D8 (ticket-party rule), D9 (merge across requesters), D10 (mailbox registration authority), D22 (mailbox-less tickets). Those that gate production-readiness work: D1 (hosting), D2 (key store), D3 (backup/restore), D13 (attachment policy). The rest gate SLA, workflow, reporting and polish work. No specialist decided any of them.

## 17.5 What needs tenant evidence

Everything about real Microsoft Graph and Exchange behaviour: sender identity (E-21, E-22), sent-copy and identifier behaviour (E-25, E-12, E-10), reply headers (E-23, E-24, E-26), conversation id derivation (E-15 to E-17), application access scoping (E-2, E-3), subscriptions (E-30 to E-35), loop and bounce header samples (E-37, E-38), Message-ID behaviour (E-13, E-14). Related findings: MSG-001 (real path), MSG-003, MSG-004, MSG-006, MSG-007, MSG-008, MSG-010. Simulator and unit tests are not live-tenant evidence.

## 17.6 What blocks production

The eleven blockers in section 4 (B-1 to B-11), the open gate D-16, and the open gates in section 5 (D-08, D-09, D-13, backup and restore evidence, abuse controls, hosting, observability, proxy configuration, mailbox scoping, migration policy). Evidence not yet produced (full regression, broad browser pass, load and query testing, restore drill) also remains.

## 17.7 What can wait

Everything classified Low or Polish that is not a blocker or gate: UX and accessibility items (WS-15), documentation and copy debt (WS-16, apart from documents that contradict locked rules, which are cheap and can be done at any time), reporting tidy-ups (DATA-004, DATA-006, DATA-009), the four ITSM enhancements, and optional items such as ITSM-007. Historical Reports should not start until the section 14.3 items are settled.

## 17.8 Final statement

The product is coherent and its core design is sound; it is not ready for production. The path is: make the blocker-gating decisions, fix the four P0 workstreams, run the D-16 tenant validation, complete the P1 production work with a recorded restore drill, then work through durability, SLA, workflow, reporting definitions and polish. The review was static and sampled in places, and none of its conclusions about Microsoft 365 behaviour have been verified against a real tenant.
