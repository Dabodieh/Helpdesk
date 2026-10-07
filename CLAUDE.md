# Helpdesk — Project Rules

Global CLAUDE.md remains authoritative for generic behaviour, safety, Git, validation discipline and the absolute no-assumption rule.

This file defines Helpdesk-specific architecture, security boundaries, locked product decisions and repository-specific constraints. Read `docs/architecture/ARCHITECTURE.md` and `docs/product/MVP.md` before substantial work.

## Agent usage

Do not spawn subagents, specialist agents, parallel agents or delegated workstreams. This project uses a single-agent workflow to control usage and keep one coherent context. The primary session owns architecture, implementation, testing, security review, documentation and integration (final architectural authority). Do not invoke definitions under `.claude/agents/` unless the user explicitly requests a subagent for a specific task. Prefer sequential implementation with targeted tests. This overrides any older documentation recommending multi-agent development.

The repo is the source of truth. Significant changes to shared contracts, auth, permissions, DB architecture, module boundaries, common APIs, global dependencies or deployment need an ADR.

## Project purpose

Helpdesk is a UK-focused internal service desk / ITSM platform.

Keep it focused on:
- internal ticketing
- Department-scoped service management
- requester self-service
- email support
- SLA / automation
- knowledge
- collaboration
- reporting / operational visibility

Do not turn it into:
- CRM
- sales software
- call-centre software
- marketing automation
- social support
- employee surveillance
- gamification
- generic workflow software

## Locked architecture

- React 19
- TypeScript
- Vite
- React Router
- TanStack Query
- Zod
- C# / .NET 10 LTS
- ASP.NET Core
- EF Core
- PostgreSQL 18
- SignalR
- Hangfire
- Microsoft Entra ID / OIDC
- Microsoft Graph for Exchange Online shared mailboxes
- SeaweedFS for local S3-compatible object storage
- PostgreSQL-backed search
- Serilog
- OpenTelemetry
- Docker / Compose / Linux
- modular monolith

Do not introduce microservices, new persistence engines, external search infrastructure, new frontend frameworks, new message brokers or major infrastructure dependencies unless explicitly approved.

Extend existing mechanisms first.

## Security boundary

Department is the HARD security boundary.

Teams are routing/assignment groups only, not security boundaries.

Every Department-scoped operation is authorised server-side via `IAuthorizer`. Cross-department leakage is Critical.

A user with access only to one Department must not see or infer another Department's:
- tickets
- counts
- requester history
- queues
- types
- fields
- mailboxes
- SLA
- automation
- configuration

Platform Admin does not automatically gain Department ticket-content access.

Server-side authorization is authoritative.
Frontend hiding is UX only.

Preserve existing non-disclosure behaviour:
- inaccessible objects should usually behave like missing objects (404)
- no leaks through counts, search, errors, validation or realtime events

No secrets in the repo. Never log message bodies or tokens.

## Identity model

Keep these concepts separate:

- Entra/internal account = stable authenticated employee identity
- Department Requester = Department-local requester row used by tickets
- requester link = stable ownership association
- email = mutable contact/routing data
- Department membership = internal staff/admin access
- Request Form audience = permission to discover/submit a form

Do not collapse these into a global CRM/contact model.

Email is not proof of ownership.

Do not restore email-based historical claiming.

Ambiguous legacy/requester ownership must require explicit authorised admin resolution.

## Request Form audiences

Current model:
- All Employees
- Selected People

New forms default restricted until deliberately configured.

Audience rules must apply server-side to:
- catalogue discovery
- metadata
- fields/options
- validation
- direct access
- submission

Being allowed to submit a Department form grants no Department staff access.

Future Help Topics must not bypass the underlying Request Form audience.

## Email identity

Department comes from the receiving mailbox. Ingestion is idempotent.

Locked behaviour:

- ticket mailbox owns From
- ticket mailbox owns Reply-To
- agent personal email is never From
- team/assignee/status/type changes never change mailbox
- manually-created tickets store a valid Department mailbox
- do not silently switch mailbox if the stored mailbox is unavailable

Mailbox-less tickets — no fallback. A ticket must have its own valid stored Department mailbox before any public reply can be sent:
- ticket has a valid active stored mailbox → send from that mailbox
- ticket has no stored mailbox → BLOCK sending
- ticket has an inactive/unavailable stored mailbox → BLOCK sending
- NEVER fall back to the Department default mailbox for an existing ticket
- NEVER silently switch sender identity

This applies to legacy, mailbox-less, manually-created and any other ticket missing its stored mailbox. The Department default mailbox may be used only at ticket CREATION time, where the creation rules deliberately choose and store it; afterwards sender identity comes only from the mailbox stored on the ticket. A legacy/mailbox-less ticket that needs a reply requires an authorised user to explicitly assign a valid active mailbox first; do not build an automatic repair or migration, and ask before designing the assignment workflow.

## Signatures

- per agent
- per Department
- resolved only at actual public send
- appended once
- never on Internal Notes
- canned responses/macros remain signature-free
- historical sent HTML is immutable

Do not add:
- Department footer
- organisation footer
- mailbox footer/signature
- agent-selected From identity

unless explicitly requested.

## Acknowledgements

- configured per Department
- may apply separately to email and portal
- sent from ticket mailbox
- no agent signature
- must not create loops
- existing auto/bounce/bulk/own-address suppression remains
- automatic acknowledgement does not count as human first response

## Ticketing model

Preserve existing:
- statuses: Open, Pending, Resolved, Closed
- priorities: Low, Normal, High, Urgent
- queue-first agent workspace
- public replies
- Internal Notes
- requester history
- followers
- macros
- canned responses
- merge
- related tickets
- SLA
- automation
- saved views
- search
- request forms
- ticket types
- configurable ticket fields
- knowledge
- realtime updates

Do not rebuild existing capabilities unnecessarily.

## Internal Notes

Internal Notes are security-sensitive and must never leak to requesters through:
- APIs
- portal
- search
- snippets
- notifications
- realtime
- requester history
- merged views
- future reports/exports

## Merge rules

- same-Department only
- canonical ticket remains authoritative
- merged source is read-only
- preserve message provenance
- do not rewrite historical messages
- no cross-Department merge/link visibility

## Macros

Macros are manual agent actions, not automation rules.

A public-reply macro prepares a draft only.
It must never send automatically.

Agent signature is added only when the draft is actually sent.

## Automation

Preserve:
- rule order
- stop-processing
- dry run
- execution history
- loop protection

Improve existing behaviour before adding broad new automation concepts.

## UI direction

The product should feel like a mature working helpdesk.

Design direction:
- Freshdesk/Freshservice operational density
- HaloITSM-style organisation
- restrained professional hierarchy
- preserve our queue-first workflow

Avoid:
- generic SaaS dashboards
- excessive cards
- huge whitespace
- oversized typography
- decorative UI
- CRUD/admin-console appearance

Use:
- compact density
- clear hierarchy
- spacing
- restrained separators
- consistent states
- obvious actions

Ticket conversation should dominate the workspace.
Properties should remain a restrained secondary rail.
Internal Notes must be unmistakable.

## Department administration UX

Preserve:
- grouped Department shell
- Department context header
- grouped vertical navigation
- real Overview/setup page
- explicit Edit Department interaction

Do not return to an unscalable horizontal tab strip.

## Reporting rules

Operational Dashboard = what needs attention now.

Historical Reports = what changed over time.

Do not combine them conceptually.

Never add:
- agent rankings
- leaderboards
- individual performance scoring
- presence-duration reporting
- surveillance metrics

Automatic acknowledgements do not satisfy human response metrics.

Use Europe/London for UK-facing reporting unless an existing business-calendar rule overrides it.

## Collaboration roadmap constraints

Planned order:
1. durable notifications
2. Internal Note mentions
3. presence/collision warnings

Notifications must be durable/persisted.
SignalR should prompt authorised retrieval.

Mentions:
- Internal Notes only initially
- individuals only initially
- Department-safe picker
- no automatic following

Presence:
- advisory only
- viewing/composing only
- no keystrokes
- no draft streaming
- no employee monitoring
- does not replace concurrency controls

## Explicit exclusions

Do not add unless the user explicitly reverses the decision:

- Tags
- Forwarding
- Time tracking
- Agent scoring
- Leaderboards
- Gamification
- Companies CRM
- Contracts
- Sales CRM
- Social-media channels
- Telephony/call-centre features
- Marketing automation
- Department/global email footers
- personal-email From addresses
- Team security boundaries
- implicit Platform Admin ticket access
- cross-Department merge
- generic form scripting
- chatbot journey builders
- additional mail providers
- external search infrastructure
- replacement queue engine

## Production gates

Do not claim production readiness while required gates remain open.

Important gates include:
- real Graph tenant validation
- malware scanning
- persistent Data Protection keys
- approved retention/data lifecycle policy
- backup/restore evidence
- abuse controls
- admin accountability where required

Simulator/unit tests are not live-tenant evidence.

## Microsoft Graph

Do not assume simulator behaviour proves Graph behaviour.

Graph `sendMail` acceptance does not mean delivered.

Keep states distinct where applicable:
- queued
- submitted/accepted
- confirmed failure
- uncertain outcome

Do not blindly retry uncertain sends.

NDRs must not become requester tickets or replies.

Do not expose Graph secrets/tokens/raw payloads in ordinary UI/logging.

## Privacy/security target

Design for:
- UK GDPR / privacy by design
- data minimisation
- Cyber Essentials / Cyber Essentials Plus readiness
- OWASP ASVS Level 2 verification target

Do not claim legal compliance or certification merely because controls exist.

Do not implement destructive retention until organisational policy is approved.

## Repository structure

Preserve established repository layout: code in `src/`, tests in `tests/` (existing locations), scripts in `scripts/`, docs in `docs/`.

Use:
- `docs/architecture/`
- `docs/design/`
- `docs/guides/`
- `docs/plans/`
- `docs/reports/`
- `scripts/`

Do not create loose plans/reports or stray files in the repository root.

Scope: ideas go to `docs/plans/BACKLOG.md`; unresolved questions to `docs/plans/OPEN_DECISIONS.md`.

## Project-specific testing

Global targeted-testing rules are mandatory. Usage efficiency is a project requirement: fewer commands, smaller outputs, sequential work, fewer rereads.

Do not automatically run:
- full .NET suite
- architecture suite
- full Vitest
- full Playwright
- Department isolation matrix
- Docker builds
- migration round-trips
- scale/load tests

Run only tests directly affected by the change. Never rerun a passing test in the same task, and documentation changes never trigger retesting.

Specific rules:
- authorization change → focused authorization tests (e.g. internal-note change: note REST and note realtime authorization tests, not every isolation test)
- mail change → affected mail tests
- frontend change → affected frontend tests; no browser flows for backend-only changes
- visual change → one focused browser review if useful
- schema change → affected migration only, up/down/up
- Docker image builds only if the Dockerfile or runtime dependencies changed, the user asks, or a container-specific problem is under investigation
- scale testing only if a performance-critical query changed, a concern is demonstrated, or the user asks
- builds: frontend build only for frontend work when needed; backend project build only for backend work when needed

Do not run the Department isolation matrix merely because a Department-scoped feature changed. Run it only if shared authorization behaviour changes or the user explicitly requests it.

On failure: inspect, fix, rerun ONLY the failing test/class/file, then the smallest directly affected group once. A passing Playwright flow is sufficient; if one flakes, fix the wait/assertion and rerun that single test once.

If a full regression seems genuinely necessary, stop and say: `This change is broad enough that I recommend a full regression run. It will use additional time/usage. Shall I run it?`

Keep runner output quiet/minimal (filtered for .NET); verbose only when investigating a failure.

Commands: Backend `dotnet test tests/<Project> --filter ...`. Frontend `npm --prefix src/web test -- <path>`.

## Visual validation

Do not perform broad browser review after every frontend change.

Check only representative changed states.

If a defect is found:
- fix it
- re-check only the affected state

Do not repeat the entire visual review for confidence.

## Known debt

Do not fix these opportunistically unless the active task requires them:

- inactive follower cleanup
- macro permission separation
- required Ticket Field lifecycle stages
- E2E/demo data separation
- audited requester relinking/merge
- requester contact refresh at sign-in
- Graph production hardening
- production malware scanner
- persistent Data Protection keys
- retention/data lifecycle tooling
- backup/restore evidence

Backlog items are not permission to broaden the task.

## Implementation discipline

Before changing behaviour:

1. inspect existing implementation
2. inspect relevant project docs/ADRs
3. preserve locked decisions
4. extend existing mechanisms first
5. if ANY reasonable ambiguity remains, STOP and ask

Never invent a project rule to work around an implementation difficulty.

Never broaden scope because another design would be easier to code.

Never replace a working subsystem unless explicitly requested.

## Commands

- Deps: `docker compose up -d` (PostgreSQL; object store opt-in via `--profile storage`)
- API: `dotnet run --project src/Helpdesk.Host`
- Web: `npm --prefix src/web run dev`

## Completion

For substantial work, report concisely:
- what changed
- important project-specific decision
- targeted validation run
- validation result
- schema/migration impact
- commit SHA if requested
- genuine remaining limitation/blocker

Do not automatically start the next roadmap item.

STOP after the requested task/refinement is complete.
