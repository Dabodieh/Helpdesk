# Backlog (not in current milestone)

- Custom per-department roles and permission editing
- Team-restricted ticket visibility
- Per-department custom statuses, custom fields and forms
- PostgreSQL row-level security (if D-04 defers it)
- IMAP/SMTP and Google Workspace providers
- Ticket merge/link/parent-child
- Redis / external search (only on demonstrated need)
- Service catalogue, approvals, assets/CMDB, incident/problem/change, onboarding, procurement
- Intune/Entra and Teams integrations; AI-assisted support
- Multi-factor / additional auth providers
- Requester email DKIM/SPF-based trust scoring
- Audited, temporary emergency ticket access (D-12) - explicit, never implied by platform-admin
- Team-scoped management for TeamLead (own team) and optional team-level visibility restrictions
- Retention rules, export and anonymisation (needs D-09 policy first)
- Malware scanning engine selection and `IAttachmentScanner` (D-08; required before production attachment release)
- Dual control / notification for platform-admin self-granted memberships (D-11)
- Exchange RBAC-for-Applications operator runbook (Phase 3)
- Multi-tenant authentication design (only if ever required; not v1)
- Department-scope EF query filter (PERMISSIONS rule 4) when ticket tables arrive (Phase 2)
- Rate limiting (per-user API, per-IP anonymous/webhook)
- Dedicated DB roles/grants (app role vs migration role); deployment step for `migrate`
- Move `/api/me` permission-list logic into `IAuthorizer` so UI hints and enforcement share one source (Phase 2)
- Machine-readable problem `type`/`code` for 409 (version conflict vs uniqueness vs last-admin) and CSRF failures, so the UI stops matching message text
- `/api/me` should expose effective administrative permissions for platform admins (replaces the hardcoded list in the web app)
- Department audit view and platform-admin management screens in the web UI (API exists)
- Playwright end-to-end smoke for the admin UI; OpenAPI-generated TS types
- User lookup scope/privacy review (currently all active users)
- Web Dockerfile / reverse-proxy config; CI step that builds the host image
- Real Entra tenant sign-in verification (needs a dev tenant)
- Database-level append-only guard (trigger or revoked grants) for `ticketing.messages`, like `audit_events`
- ~~Ticket event when a membership removal unassigns tickets~~ (done: audit/activity entry per ticket with reason `department_membership_removed`, `unassigned` notification to those with `tickets.assign.any`, capped at 5 open tickets per removal). Still open: a grouped summary notification (the notification list is one entry per ticket), and automation triggers for these assignee changes. The web client must learn the `unassigned` notification type (`src/web/src/api/notifications.ts` enum and the text map in `NotificationBell.tsx`) BEFORE this backend reaches an environment with real users, or the notification list fails to parse for leads.
- Phase 3 mail: sanitise CR/LF in subjects and angle brackets in requester emails before header/template use
- Per-department late-reply policy (reopen vs follow-up ticket) and auto-close timers (Phase 5)
- Cross-department ticket move (reference alias rules defined in TICKETING.md)
- ~~SignalR push, resizable split~~ (done in Phase 4). Still open: optimistic UI for ticket edits.
- SignalR backplane (Redis or PostgreSQL) before running more than one host instance.
- Canned responses: per-team scope, usage statistics. (Placeholders and per-ticket rendering are done: `GET /api/tickets/{ticket}/canned-responses/{id}/render`, fixed list `requester.name`, `ticket.reference`, `ticket.subject`, `department.name`, `agent.name`; the composer should call it instead of inserting the raw body.)
- Ranked full-text search (tsvector, stemming, relevance) if substring search proves too coarse on real data.
- Per-account storage of split width, density and column preferences (currently per browser).
- Code-split the web bundle (about 540 kB).
- Screen-reader and high-contrast testing with real assistive technology.
- Bulk undo, bulk reply.
- Multi-select priority filter and saved views in the queue (Phase 4)
- Reverse-proxy security headers for the SPA (CSP, HSTS)
- Rate limiting for the webhook endpoints and the API (ASP.NET rate limiter)
- Malware scanning engine behind `IAttachmentScanner` (D-08); outbound attachments; `cid:` inline image rendering
- Reply quoting strategy, per-reply mailbox choice, "send test email" and connection check for a mailbox
- Hangfire ROLE=web|worker split and metrics; retention/purge of raw bodies and webhook receipts (D-09)
- Re-run the Graph runbook findings into the provider (message-id rewriting, reply headers, SendAs needs) once a tenant exists

- (Phase 5) Re-evaluate or re-apply an SLA policy when priority, team or mailbox changes; manual policy override on a ticket.
- (Phase 5) Retention and pruning of automation execution history; store only matched and failed evaluations if volume demands it.
- (Phase 5) Notification channel (email or chat) for SLA warnings and escalation; automation actions for tags and categories once they exist.
- (Phase 5) Dry run that follows chained triggers; a rule "impact preview" over recent tickets.
- (Phase 5) Inbound-mail-with-rules end-to-end test. (Failed-execution resume and the `sla` realtime event now have dedicated tests.)
- (Phase 5) Rate limits for configuration endpoints; per-department business-hours templates and public-holiday imports.

- (Phase 5 closeout) Optional: a separate "last note activity" timestamp visible only to note readers if agents want notes to reorder their queue.

- (Phase 6) Conditional form fields and form-level routing rules; per-form default team is the only routing hook today.
- (Phase 6) Participant/CC portal access (primary requester only in v1); admin unlink or relink of a requester identity.
- (Phase 6) Attachments in the submit request itself (today a follow-up reply right after creation); refactor the four ticket-creation call sites onto one path.
- (Phase 6) Knowledge approval workflow, article versions/history, per-department audiences beyond Everyone/Requesters, category nesting.
- (Phase 6) Retention of requester links and portal feedback; per-department portal branding; requester notifications by email on agent replies are the existing email path only.
- (Phase 6) Light/dark screenshot review of the portal; vitest component tests for portal pages beyond markdown safety (covered by Playwright flows today).
- (Phase 6) Scale check of My requests at 50k+ tickets per requester (index `ix_tickets_requester_public_activity` exists; only knowledge search was measured).

- (Refinement 1) Requester merge/deduplication (person identity is the normalised email across department requester rows today, so a merge would unify rows or add an explicit person link); requester/people directory and search; phone and site fields (not held today); requester history link from the portal-linked identity.
- (Refinement 2) Queue filter UI for ticket type (the API accepts `typeId`; agents need a read-only type list per department first); type and field columns or filters in saved views.
- (Refinement 2) Agent create-ticket dialog: choose a type and fill its fields (the API supports both); portal UI for requester-editable fields (API and `editableFields` exist).
- (Refinement 2) Searchable or filterable custom fields; automation conditions on field values; conditional fields; a required-details report for tickets missing newly required values.

- (Refinement 3) Un-merge (split) of a merged ticket; merging across departments; copying merged participants/CC to the canonical ticket (deliberately not done: replies would reach other requesters); retrying delivery of an imported public reply from the canonical ticket.
- (Refinement 3) Searching the messages of merged tickets from the canonical ticket; showing links of a merged source on its canonical ticket; link types beyond Related/Duplicate/Parent/Child (Problem/Incident management); a requester-facing view of related tickets.
- (Refinement 3) Browser flow (open duplicate, merge, check merged state) and a portal Vitest for the merged-request notice (covered by API tests today); a realtime event kind for merge so open workspaces refresh without polling.

- (Refinement 4) Follower notifications (in-app and email) using the follower table; "follow automatically" rules (assignee, requester reply, mentions); follower counts in queue rows; a realtime event kind for followers instead of the plain `updated` event.
- (Refinement 4) Clean up follower rows when a user is deactivated (only membership removal cascades today; the list already ignores inactive users); team-level followers; follow from the bulk bar.

- (Refinement 5) Macro usage statistics and "last run"; personal (per-agent) macros and macro folders; macro actions for followers, links, tags and mentions; a dry-run of a macro against a ticket showing the concrete before/after (the preview is the configured action list today); keyboard shortcut to run a macro; one browser flow (admin creates macro, agent runs it, reply stays an unsent draft) is covered by API and Vitest tests only.

- (Refinement 7) Per-mailbox acknowledgement overrides; HTML acknowledgement templates; a stable Entra-backed person identity for requesters; job title on the agent profile (and `{{agent.jobTitle}}`); drop the unused legacy `mailboxes.signature` column; an `Auto-Submitted: auto-replied` style header on acknowledgements once Graph custom-header support is validated (D-16); rate limit per requester for acknowledgements.

- (Refinement 8) Request form audience by department membership or a reusable employee group (only all employees and named users exist; the user picker reuses `/api/users/lookup`, so an administrator without `department.members.manage` can keep and remove but not add people); audience-aware Help Topics; one browser flow (restrict form, allowed employee submits, other cannot discover) is covered by API tests only.

- (Refinement 9) Audited administrator tool to link/relink an unlinked requester to an account (and list `requester.link_ambiguous` cases); today an employee whose address already has unlinked email history cannot submit a portal form until an administrator links it (no UI yet), and legacy email-created tickets do not appear in a portal user's list until linked. Requester merge/dedup. Refresh requester contact data at sign-in, not only at submission.

- (Refinement 10) Explicit, audited, reason-bearing relink of an already linked requester ("Change linked account"); requester merge/dedup for an account that already owns a requester in the department; surface `requester.link_ambiguous` events as a prioritised hint in the identity list; a lookup that does not need `department.members.manage` anywhere is still shared with the member picker (`/api/users/lookup`).

- (Refinement 11) Per-requester acknowledgement rate limit (deferred: the inbound sender rate limit already caps tickets from one sender, so a looping sender cannot trigger unbounded acknowledgements); `Auto-Submitted` on acknowledgements once Graph support is validated in a real tenant (believed unsupported for non-x headers); outbound message health could also count NDR-failed messages separately from provider-failed ones; a lifecycle-notification counter on mailbox health; real-tenant Sent Items and identifier evidence (D-16).

- **Legacy cross-requester merges (from WS-2, decision D9).** Tickets merged across different requesters before ADR-017 left the source requester outside the conversation (no portal access, no replies). They are deliberately not back-filled: granting access to an existing conversation could expose content they were never authorised to see. Needs a way to list them and a deliberate, authorised remediation per merge.

- **(Post-Refinement-10 remediation, deferred)**
  - MSG-007 forged bounce: require a bounce to come from the tenant's own transport once real headers exist (needs the D-16 E-37/E-38 samples). MSG-012 visible correlation token wording.
  - BE-011: one HTTP status for "reference not valid for this department" (400 field error vs 409 `invalid_state`); a published API behaviour, change deliberately with its clients and tests.
  - DATA-008: decide whether to capture mailbox outage history, article views and "why no SLA" (cannot be rebuilt later).
  - UX-019 remainder: a live region for new conversation messages; the notification panel's hard-coded position. DOC-009 wording of server 400 messages; DOC-011 spelling convention.
  - SRE-009 mail job locks (a stalled run now shows as job-lag metrics); SRE-011 sizing estimates and pruning (blocked on D-09).
  - A scanner engine (D-08) and a named restore-drill owner (D3) are external prerequisites, not backlog.

- (Production readiness) **UI requirement: hide attachment controls when attachments are disabled.** Production runs with `Attachments:Enabled=false` (no scanner yet, D-08), so the server records any upload or inbound file as blocked (`attachments_disabled`) and refuses downloads. The redesigned UI must not offer attach buttons, drop zones or download links in that state (agent composer, portal request and reply forms, ticket conversation attachment lists). The server does not yet tell the client whether attachments are enabled: a read-only flag in an existing authenticated config/profile response is needed before the UI can honour this (the endpoint choice is undecided). Until then the controls remain visible and uploads show as blocked. Do not edit `docs/design/redesign/`; carry this into the redesign hand-off.
