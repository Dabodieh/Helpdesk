# Post-Refinement-10 decision record

Decisions resolved from `docs/reports/POST-REFINEMENT-10-MASTER-REPORT.md` (section 6). Concise record only; the report holds the evidence and options.

## D8. Inbound correlated mail from a non-requester, non-participant sender

- **Status:** Resolved 2026-10-07.
- **Related:** MSG-002 (blocker B-2), SEC-006, MSG-003, BE-001, MSG-012, ARC-003. Workstream WS-2.

**Final decision**
1. A message with a valid thread identifier whose sender is neither the requester nor an existing participant must not touch the existing ticket. A **new ticket** is created and keeps a safe reference to the suggested/original ticket for the agent (same precedent as the subject hint). No separate held-mail workflow.
2. `conversationId` is kept, but only as correlation evidence when the sender is already the requester or an existing participant. It never establishes permission to join a ticket. D-16 experiments E-15 to E-17 stay open.
3. Participants are capped at **20 active Cc participants per ticket**, excluding the requester.
4. Agents may remove participants from Cc.

**Implementation rules**
- For the non-participant case, do not: add the message to the original conversation; reopen the original ticket; expose the message to the original requester; create participants on the original ticket from its sender/To/Cc.
- Sender/participant membership is the trust boundary; identifiers (Message-ID, token, conversationId) only locate the ticket once that condition holds.
- Cap: never silently create participants beyond 20. The requester and existing participants can still reply. Further To/Cc addresses are not added once the cap is reached, and a clear agent-visible warning is shown when addresses were not added because of the cap.
- Removal: an agent with permission to send a public reply may remove an ordinary participant. Removal affects future replies only and must not alter historical messages or audit history. The primary requester can never be removed through this action. Any special treatment of a requester introduced by a cross-requester merge belongs to D9 and is not assumed here.
- Fixed minimum rules: never create participants from To/Cc of a message sent by a non-participant; show the actual Cc recipients by name in the composer.

## D9. Merging tickets of different requesters

- **Status:** Resolved 2026-10-07.
- **Related:** SEC-004, BE-002, ARC-003. Workstream WS-2 (merge part). Interacts with D8.

**Final decision:** Option C. Cross-requester merge is allowed, and the source requester is preserved as a **protected participant** on the canonical ticket.

**Implementation rules**
- The canonical ticket remains the working ticket.
- The source requester becomes a participant on the canonical ticket, can see the canonical request in the portal, and continues to receive future public replies.
- The historical source ticket remains part of the merge history/audit.
- The merge dialog must clearly warn that another requester will be added to the canonical conversation.
- D8 interaction: a merge-introduced requester is not an ordinary removable Cc participant.
  - primary requester: never removable;
  - merge-introduced requester: never removable through the ordinary "remove from Cc" action;
  - ordinary Cc participants: removable by agents as defined in D8;
  - merge-introduced requesters do not count against the D8 cap of 20 ordinary active Cc participants.
- No separate "detach merged requester" workflow is part of this decision.

## D10. Mailbox registration authority

- **Status:** Resolved 2026-10-07.
- **Related:** MSG-004 (blocker B-4), D-16 (E-2, E-3). Workstream WS-4.

**Final decision:** Option A + C. Only a Platform Admin may register a mailbox, and a mandatory Graph scope probe runs at creation. No approved-address list.

**Implementation rules**
- Registration authority: only a Platform Admin may add/register a new mailbox. Department Admins cannot introduce a new mailbox address. The restriction applies to registration only; it does not remove existing Department Admin permissions for managing an already-registered mailbox unless another decision says so.
- Creation-time validation: before registration succeeds, probe the mailbox through Graph using the application's actual credentials. If the mailbox does not exist or the application cannot access it (403/404 or equivalent), registration fails and no mailbox configuration is created.
- A Platform Admin cannot bypass a failed Graph probe.
- D-16 remains required. This decision does not prove Exchange-side scoping; E-2/E-3 must still show the application cannot access mailboxes outside its approved scope.

## D22. Tickets with no stored mailbox

- **Status:** Fully resolved 2026-10-07 (direction plus all four design questions). Closed in `OPEN_DECISIONS.md` (PR10-D22).
- **Related:** ITSM-003, DOC-010. Workstream WS-13.

**Decided: A + B (direction)**
- A, portal prevention: a requester cannot submit a Request Form unless the Department has a usable active default mailbox. The form is unavailable/non-submittable until then. No silent mailbox-less ticket, and no later fallback to whatever mailbox becomes the Department default (the mailbox is selected and stored at ticket creation).
- B, explicit assignment: existing legacy/mailbox-less tickets get a deliberate repair action. The Department default is never attached automatically when a mailbox is later configured, and no mailbox is silently chosen when an agent sends a reply. Once explicitly assigned, that mailbox is the ticket's stored mailbox and the existing no-fallback/immutability rules apply.
- Setup ordering: document mailbox first, Request Forms second. Department setup must clearly warn that requester submission is unavailable until a usable default mailbox exists.

**Design questions (asked one at a time, all resolved)**
1. Who may assign a mailbox to an existing mailbox-less ticket? (resolved, see below)
2. Which Department mailboxes are eligible? (resolved, see below)
3. Is assignment permanent or may it later be changed? (resolved, see below)
4. What confirmation/audit behaviour is required? (resolved, see below)

**D22 design question 1 resolved 2026-10-07: who may assign**
- A mailbox may be assigned to an existing mailbox-less ticket only by a user who is a member of the ticket's Department, already has normal access to that ticket, and holds the existing Department-scoped permission `department.mailboxes.manage` (administrative; mapped to DepartmentAdmin and PlatformAdmin in `docs/architecture/PERMISSIONS.md`).
- Implementation rule: `department.mailboxes.manage` is administrative and passes for a Platform Admin without membership, so it is not sufficient alone. The assignment must also require normal ticket access by Department membership (resource authorisation through the scoped ticket, 404 non-disclosure otherwise). A Platform Admin without membership cannot assign.
- `tickets.reply` alone is not sufficient; choosing the ticket's permanent sender identity is an administrative action, not part of sending a reply.
- No new permission is introduced; a suitable Department-scoped capability already exists.
- Question 1 status: resolved.

**D22 design question 2 resolved 2026-10-07: eligible mailboxes**
- Any usable active mailbox in the ticket's Department may be explicitly assigned. Eligible mailboxes must belong to the same Department as the ticket, be active, and be currently considered usable for outbound sending under the existing mailbox availability checks.
- The authorised user explicitly chooses which eligible mailbox to assign. The Department default may be offered like any other eligible mailbox but is never silently selected or automatically applied.
- Mailboxes from another Department, inactive mailboxes and unavailable/unusable mailboxes are not selectable.
- Once selected, the mailbox is stored on the ticket and becomes its sender identity under the normal no-fallback rules.
- Question 2 status: resolved.

**D22 design question 3 resolved 2026-10-07: changing an assigned mailbox**
- An explicitly assigned mailbox may be changed by the same authorised users (question 1 rule) only while no public reply has been submitted or potentially submitted using that mailbox. After external sending has begun, the ticket mailbox is permanently fixed.
- Changeable: draft/local-only reply; pre-send validation failure where nothing was submitted.
- Locked: sent/accepted; uncertain send (may have been delivered); confirmed bounce after submission (sender identity already used externally).
- A queued/in-progress outbound send must not race a mailbox change: the mailbox cannot be changed while such a send is actively pending.
- Changing the mailbox never rewrites historical messages or audit history.
- Once locked, team, assignee, status, type, default-mailbox changes or later mailbox configuration never switch the ticket's sender identity.
- Question 3 status: resolved.

**D22 design question 4 resolved 2026-10-07: confirmation and audit**
- Confirmation: a confirmation dialog is required before every assignment or correction. It shows the ticket reference, the selected mailbox address/display name, that this mailbox becomes the ticket's sender identity, that it can only be corrected before any public reply has been submitted or potentially submitted, and that once external sending has begun the mailbox is permanently fixed.
- Audit: every first assignment and permitted correction writes an audit event with ticket reference/ID, actor, timestamp, previous mailbox ID/address (none for a first assignment), new mailbox ID/address, and action type (assigned or corrected). No message bodies, tokens or other message content.
- Reason: absent. No free-text reason is required or offered; the action and its before/after values provide the accountability and avoid unnecessary personal data.
- D22 fully resolved. Question status: all four resolved.

## D1. Production web serving and reverse proxy

- **Status:** Resolved 2026-10-07.
- **Related:** SRE-001 (blocker B-5), SEC-011, SRE-006 (intended end-state). Workstream WS-5.

**Final decision:** Option A. The ASP.NET Core host serves the SPA, API and SignalR as one deployable; Nginx terminates TLS and proxies HTTP and WebSockets.

**Implementation rules**
- The host serves the built Vite static assets, `index.html`, the SPA fallback route, the API and SignalR. Production has one Helpdesk application deployable; no separate frontend web-server deployment.
- The frontend build is produced during the production image/build process and copied into the host's static-file directory. Development stays Vite-based and unchanged.
- Nginx is the supported production reverse proxy: terminates TLS, proxies HTTP to the host, proxies WebSocket/SignalR upgrades, forwards only explicitly trusted proxy headers, and provides the external HTTPS endpoint.
- The application must not depend on Nginx-specific behaviour beyond ordinary reverse-proxy/TLS/WebSocket functionality (cloud-agnostic architecture preserved).
- Security headers: the ASP.NET host owns the application-page response headers. Configure production headers including Content-Security-Policy (with frame protection / `frame-ancestors`), `X-Content-Type-Options: nosniff` and Referrer-Policy. Nginx may add transport-level hardening, but there is one documented owner for application response headers; no conflicting policies in both layers.
- Forwarded headers: trust them only from the configured Nginx proxy. No unrestricted/default trust. This is the intended end-state for SRE-006 once the framework behaviour is verified.

---

# Batch 2: questionnaire answers (resolved 2026-10-07)

Source of truth for each decision's options: `docs/reports/POST-REFINEMENT-10-MASTER-REPORT.md` section 6. Workstreams are in section 8 of that report.

## Production / hosting / recovery

### D2. Data Protection key store (SRE-002 / B-7, D-13; WS-6)
- **Decision:** A, PostgreSQL.
- **Rule:** set a fixed Data Protection application name so every instance shares the same key ring.

### D3. Backup and recovery (SRE-003 / B-8, ARC-007; WS-6)
- **Decision:** RPO 1 hour; RTO 4 hours.
- **Rules:** backup target is encrypted off-host storage, separate from the Helpdesk host/infrastructure. PostgreSQL and attachment/object storage are backed up as one recoverable system. A restore drill is performed and recorded at least quarterly. Drill owner: the named Helpdesk platform/IT technical owner (the individual is **not yet named**; to be recorded by the owner).

### D4. Readiness check (SRE-004; WS-7)
- **Decision:** A + C.
- **Rules:** readiness fails if object storage is unavailable, or if required migrations/schema state are not valid. A temporary Hangfire/job backlog must NOT fail web readiness; job-processing health is exposed through monitoring/metrics instead.

### D5. Migration order and rollback (SRE-007, ARC-007; WS-6 documentation)
- **Decision:** A.
- **Rules:** one database, one role, explicitly documented module migration order. Production rollback is restore from a known-good backup, not arbitrary per-module down migrations.

### D6. Supported topology (SRE-008, DOC-002; WS-6 / WS-16)
- **Decision:** A. A single Helpdesk application instance is supported initially; correct the documentation.
- **Rule:** do not build the web/worker split or a realtime backplane until there is an actual scaling requirement.

### D7. Retention, reporting facts, pruning, log sink (DATA-003, MSG-011, SRE-011, D-09; WS-14, WS-7)
- **Decision (a), PROVISIONAL retention periods:** ticket/message/attachment content 3 years after ticket closure; ignored/bounce/raw inbound mail not attached to a ticket 90 days; operational logs 90 days; completed outbox records 30 days; user notifications 180 days; attachment orphans 7 days; personal-data-bearing audit detail aligned with the ticket/content period; content-free lifecycle/reporting facts 7 years.
- **Decision (b):** A. Keep a content-free lifecycle/reporting record separate from personal data so reporting survives later anonymisation/deletion.
- **Decision (c):** automatically prune each data class per the periods above. Never prune pending/failed operational work merely because it is old.
- **Decision (d):** structured logs to stdout plus a central OTLP/OpenTelemetry-compatible log sink. Console/container logs alone are not the production record.
- **Gate:** the periods must be checked against SAMH's approved organisational retention policy before production. D-09 therefore stays open (provisional), and per project rules no destructive retention/pruning is implemented until the periods are approved.

## Mail / messaging

### D11. Acknowledgement abuse control (MSG-005, SEC-003; WS-8)
- **Decision:** A + C.
- **Rules:** per-mailbox acknowledgement cap/window; remove attacker-controlled subject text from the default acknowledgement template. SPF/DKIM/DMARC status may be added as an extra signal only if D-16 proves reliable data is available through Graph; do not depend on it.

### D12. Create ticket from ignored mail (MSG-009; WS-13)
- **Decision:** A. Add an audited "create ticket from this message" action.
- **Rules:** allowed for normal ignore/rate/bulk/list/auto-submitted cases. Not allowed for confirmed NDR/bounce/loop messages that belong to mail-delivery processing.

## Security / permissions

### D13. Attachment policy (SEC-002 / B-11, SRE-010, D-08; WS-8)
- **Decision:** A now, B after the scanner exists.
- **Rules:** until scanning exists, block the review's risky categories including macro-enabled Office files, disk images, HTML/SVG, shortcuts and similar executable/script-launching types. Production fails startup if clean scanning is required but disabled. Once a scanner exists, legitimate macro-enabled Office documents may be allowed only after a clean scan.

### D14. Platform Admin boundary (SEC-012, SEC-001, SEC-005, ARC-002; WS-1)
- **Decision:** mixed boundary. A Platform Admin without Department membership may manage structural/platform configuration but not Department content or content-derived operations.
- **Rules:** require Department membership/ticket access for mail retry/resend and Knowledge draft content. A Platform Admin may keep form audience configuration, Department membership configuration and Department administrative audit/configuration views, provided those views contain no ticket/message/requester content. Document the boundary explicitly.

### D15. Trust in the Entra mail attribute (SEC-007; WS-13)
- **Decision:** B.
- **Rules:** an editable email/mail attribute is not proof of ownership. Immutable Entra identity is the ownership anchor; email is mutable contact/routing data only.

### D30. Structural guards (QA-007, ARC-002; WS-1)
- **Decision:** A.
- **Rules:** extend structural isolation guards to Knowledge, Mail and Attachments; also add the administrative-endpoint Platform Admin probe test.

## Reliability / concurrency

### D16. Durable after-commit work (BE-004, ARC-004, BE-005; WS-9)
- **Decision:** B.
- **Rules:** reuse the existing durable outbox pattern. Automation and SLA follow-up intent is written in the same transaction as the triggering change and drained idempotently. No generic event framework.

### D19. Bulk edit concurrency (BE-010, QA-004, UX-009; WS-10)
- **Decision:** B, per-ticket versions.
- **Rules:** if a ticket changed after selection, do not overwrite the intermediate decision; return that ticket as "changed since selected" while unaffected tickets proceed.

## SLA / lifecycle

### D17. SLA policy selection (BE-006, ITSM-007, DATA-002; WS-12)
- **Decision:** B. Re-evaluate the applicable SLA policy when Team, Type or Priority changes.
- **Clock rules:** never reset the SLA cycle start merely because the policy changes; preserve elapsed and paused business time; recalculate the target/deadline from the original cycle timing; if the new target is already exceeded, reflect that truth rather than granting a fresh clock.

### D18. First response on resolve/close without a reply (BE-007, DATA-001, ITSM-005; WS-12, WS-14)
- **Decision:** A.
- **Rule:** resolving/closing before any public agent response stops the first-response clock and records first response as Not Applicable (not Met, not Breached).

## Service-desk workflow

### D20. Needs us vs answered (ITSM-001, DATA-002; WS-13)
- **Decision:** C. A last-public-speaker / needs-response signal in the queue.
- **Rules:** no new status, and no automatic status change after every agent reply. Agents can tell requester-last-spoke from agent-last-spoke.

### D21. Agents changing team (ITSM-002; WS-13)
- **Decision:** A.
- **Rules:** Agents may change the Team for routing/escalation. Assignee-management permissions may remain with the existing higher role. Macros that perform permitted routing must be usable by Agents.

### D23. "Waiting" wording (ITSM-004; WS-13)
- **Decision:** B.
- **Rules:** keep the underlying status; no new requester-facing status. The portal explains that "Waiting" means the request is waiting on information or another dependency and does not necessarily mean the requester must act.

### D24. Resolving without a public reply (ITSM-005, BE-007; WS-13)
- **Decision:** A.
- **Rules:** prompt that the requester will not receive a message and offer to reply first. Deliberate silent resolution is allowed after the warning where appropriate.

## Reporting / data

### D25. Sidebar counts (DATA-001, BE-007, ARC-005; WS-14)
- **Decision:** B. Sidebar unresolved-work counts = Open + Pending, matching queue and dashboard.
- **Rule:** also fix the separate bug where SLA badges include resolved/closed tickets.

### D26. Awaiting first response (DATA-002, BE-006, ITSM-001; WS-14)
- **Decision:** A.
- **Rules:** means no public human agent message yet, independent of any SLA policy. SLA-specific first-response figures stay separately labelled as SLA metrics.

### D27. Merged duplicates in "created" (DATA-005, BE-002, ARC-005; WS-14)
- **Decision:** A.
- **Rules:** a ticket genuinely created in the period counts as created even if later merged. Merged source tickets are not counted as current backlog/independent work. Report merged volume separately where useful.

## UX / lower-priority

### D28. Deactivating a mailbox (UX-005; WS-15)
- **Decision:** A.
- **Rules:** clear consequence confirmation on deactivation. Do not forbid deactivating the current default mailbox; rely on explicit consequences and existing setup warnings/validity rules.

### D29. Dead foreign-change realtime code (UX-018; WS-15)
- **Decision:** A. Remove it; do not turn it into a new feature.

---

# Ratified implementation clarifications (approved after WS-3 to WS-16)

These choices were made during implementation where the resolved decision left the mechanism or an edge open. They are now approved.

### D21 clarification: the team-routing permission
- **Approved:** the permission `tickets.assign.team` (content kind) lets Agents and above change a ticket's Team for routing and escalation. Agents do NOT receive `tickets.assign.any`; choosing another person as assignee stays with `tickets.assign.any`. The separation stays (ADR-026, `PERMISSIONS.md`).

### D17 clarification: no matching replacement policy
- **Approved:** when Team, Type or Priority changes, the SLA policy is re-evaluated. If another policy matches, the ticket switches to it while keeping the original cycle timing rules (never restarted, spent business time kept, an exceeded target shown as exceeded). If NO policy matches, the currently active SLA for that cycle is KEPT. An existing SLA commitment is never removed merely because later classification or routing matches no replacement (ADR-025).

### D15 clarification: requester identity tightening
- **Approved:** an address or email equality alone must not claim or link an existing requester identity. Where stable identity cannot establish ownership, explicit Department-admin identity resolution is required. Claim-by-email behaviour is not to be restored.

### Initial rate-limit and acknowledgement defaults
- **Approved as INITIAL operational values (configurable, not permanent policy):** portal request creation 10 per 10 minutes; replies 30 per 10 minutes; searches 60 per minute; automatic acknowledgements 60 per 10 minutes per mailbox. They are starting defaults (`RateLimits` section, `Mail:AcknowledgementLimitPerWindow` and `AcknowledgementWindowMinutes`) to be tuned from observed production traffic (ADR-022).
