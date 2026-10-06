# Phase 6: Requester self-service

Status: built and verified at the phase boundary. Not production ready: gates D-16, D-08, D-09, D-13 remain open.
Decisions: ADR-015. API: `docs/api/PHASE6_API.md`. Threat model: `docs/security/PHASE6_THREAT_MODEL.md`.

## Architecture
- Portal is a separate route tree and shell (`/portal`, `submit/:formId?`, `requests`, `requests/:reference`, `knowledge`, `knowledge/:slug`). Employees without agent permissions land there.
- Ticketing owns identity links, request forms, custom fields and portal services (`src/Helpdesk.Modules.Ticketing/Portal`).
- New Knowledge module (migration order 60) depends only on SharedKernel, Audit and Organisation; the audience check crosses to Ticketing through `IRequesterAudience` (Contracts only). Architecture tests cover the new module (73/73).
- Migrations: Ticketing `Phase6Portal`, Knowledge `Initial`, Audit `KnowledgeCategory`.

## Identity association
`requester_links` links a department requester to an Entra user. Claim only from the verified token email and only while unclaimed; thereafter access is by link id. Same-address impostors get 409 on submit and an empty list.

## Authorisation model
Separate from agent authorisation. Every `/api/portal/*` query is scoped by the caller's links; foreign or unknown references return 404. Primary requester only in v1 (participants/CC have no portal access). Admin endpoints use `department.forms.manage` and `department.knowledge.manage` (DepartmentAdmin).

## Forms and custom fields
Per-department forms, typed fields (short/long text, single/multi select, checkbox, date, number), stable immutable key and type, stable option values, deactivate not delete, requester/agent visibility. Values are snapshotted per ticket, so later form edits never change an existing ticket. Department, status, requester, source, SLA and routing are server-derived. Submissions are idempotent. No conditional logic, scripts, HTML or formulas.

## Portal behaviour
Home, Submit a request (suggested articles, per-field errors with `aria-invalid`), My requests (open/closed/all, cursor pagination, ordered by visible activity), request detail with public conversation and reply, Knowledge. Requester status wording: Open becomes In progress, Pending becomes Waiting, Resolved, Closed. Attachments on submit are added as a follow-up reply after creation (limitation, see below); all uploads use the existing pipeline and `RequireCleanScan` is unchanged.

## Email/portal coexistence
Portal replies use the same domain path as inbound email: reopen per D-15, SLA, `RequesterMessageAdded` automation, agent realtime. One conversation per ticket; message `source` is `portal`, ticket `source` is agent/email/portal.

## Privacy guarantees
Purpose-built DTOs; internal notes and their attachments never reach requesters; `last_public_activity_at` ensures notes do not change timestamps, ordering, counts or realtime. Tested by adding a note and diffing everything a requester can see.

## Knowledge base
One-level categories; Draft/Published/Archived with separate draft and published content (edits stay private until publish); audiences Everyone/Requesters; PostgreSQL full-text search over published fields (generated tsvector, GIN index) with a sanitised prefix query; helpful/not-helpful feedback (one latest answer per person, counted for admins); agent navigation entry; safe Markdown renderer (no raw HTML, safe links only). Admin changes are audited.

## Realtime security
Group `r:{requesterId}` derived from links, updated when a link is created. Payload is only `{reference, kind: reply|status|attachment, at}`; internal activity is never forwarded.

## Verification (phase boundary, run once)
| Check | Result |
|---|---|
| .NET full suite (Debug) | 926/926 (43 GraphSpike, 73 architecture, 810 host) |
| Release build of Host | 0 warnings, 0 errors |
| Vitest | 76/76 (new: markdown safety; landing test updated to the portal redirect) |
| Lint (oxlint), web build | clean |
| Migrations | all seven modules down to 0 and up again on a scratch PostgreSQL 18 database |
| Playwright (full set, Chrome) | portal 5/5 stable over 4 repeated runs, other specs pass |
| Integration | inbound email to ticket to automation, replay-safe; email+portal shared conversation |

Playwright portal flows: form submission (validation errors, routing, SLA, custom values, hidden field never offered), conversation (public only, reply, agent answer arrives live), knowledge (search, draft invisible, article XSS text, feedback, admin dialogs), security (foreign request reveals nothing).

Accessibility (axe, WCAG 2 A/AA, serious/critical): portal home, form chooser, submit form with errors, My requests, request detail, knowledge search, knowledge article, request-form dialog, form-field dialog, article editor, and five portal pages at 375px. No serious findings.

Responsive: at 375px home, submit, My requests, request detail and article have no horizontal overflow.

Performance (one focused check, PostgreSQL, 30,000 published articles, rolled back): prefix FTS matching all rows with top-21 ordering 8 ms; unfiltered browse 0.03 ms via the partial index. My requests uses `ix_tickets_requester_public_activity`; it was not measured at scale.

## Known limitations
- Attachments on submit are a follow-up reply after creation.
- Primary requester only; no participant/CC portal access; no admin unlink.
- Forms visible to all signed-in employees (no per-form audience).
- No conditional fields, article approval workflow or version history.
- Light/dark portal screenshot review not done; portal component tests beyond markdown are covered by Playwright rather than vitest.
- My requests not measured at large scale.
- Four ticket-creation call sites share logic by copy; refactor logged in BACKLOG.

## Remaining production gates
D-16 (rate limiting/abuse controls for portal submit and search), D-08, D-09 (retention including requester links and feedback), D-13 (malware scanner). Not production ready.

## Phase 7 options (not started)
Service catalogue/approvals, requester email notifications and CSAT, participant access, knowledge approval and article suggestions for agents, reporting, production hardening against the open gates.
