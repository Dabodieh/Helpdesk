# Domain Model (initial)

Direction, not a final schema. Each module refines its slice before implementing it. Naming: PostgreSQL `snake_case`, one schema per module, UUIDv7 keys, `timestamptz` UTC, optimistic concurrency (`xmin`/row version) on mutable aggregates.

## The tenancy rule
`Department` is the isolation boundary. Every department-owned row carries a non-null `department_id` (denormalised onto child rows such as messages and attachments so scoping and indexing never require a join). A row never changes department except through the explicit "move ticket" operation, which is audited and requires permission in source and target departments.

## Organisation
- **User**: person known to the system (from Entra: `tenant_id`+`object_id` unique, email, display name, `is_active`, `is_platform_admin`). Not a requester by definition; an agent may also raise tickets.
- **Department**: name, key (short prefix for ticket numbers), `is_active`, branding/config (later).
- **Team**: belongs to exactly one department; name; used for assignment and queues.
- **DepartmentMembership**: (user, department, role). A user has at most one role per department; no row = no access.
- **TeamMembership**: (user, team); user must hold a membership in the team's department (enforced by composite FK/constraint + service check).

## Permissions
- **Role** (system roles in v1: DepartmentAdmin, TeamLead, Agent, Viewer) maps to a fixed set of **Permission** codes defined in code (`tickets.read`, `tickets.reply`, ...). Custom per-department roles are backlog; the schema stores role as a stable code so it can be generalised later. See PERMISSIONS.md.

## Mail (Phase 3 tables; model fixed now)
- **Mailbox**: `id`, `department_id` (NOT NULL, FK), `display_name`, `email_address` (globally unique, case-insensitive; one address can never serve two departments), `provider` (`MicrosoftGraph`), `provider_mailbox_id` (external identifier, e.g. Entra/Exchange object id), `is_enabled`, `inbound_enabled`, `outbound_enabled`, `is_default_for_department` (at most one per department, partial unique index), provider status metadata (subscription id/expiry, delta token, last sync, last error; may live in a child table), `created_at`, `updated_at`.
  - **Shared mailboxes only (D-07).** A mailbox is an Exchange Online *shared* mailbox reached by the application's own Entra identity via Graph. No mailbox username, password or session is ever stored or required. Helpdesk agents need no Exchange FullAccess/SendAs/SendOnBehalf: helpdesk permissions alone govern what an agent may do.
  - A department may have many mailboxes; each mailbox has its own outbound identity (address, display name, signature). Replies **never** fall back to a global/system sender.
  - A **ticket records the mailbox** it arrived on (`ticket.mailbox_id`) and every email-backed message records its mailbox. A reply is sent from the ticket's mailbox (or another outbound-enabled mailbox of the **same** department, chosen explicitly). A composite FK `(mailbox_id, department_id)` → `mailboxes(id, department_id)` on tickets and mail messages makes using another department's mailbox impossible at the database level.
- **MailMessage** (stored email): direction, mailbox, department, provider message id (+ immutable id), internet message id, in-reply-to, references, provider conversation id, from/to/cc/bcc, subject, html/text body, received/sent time, raw reference in object storage, processing state. Unique `(mailbox_id, provider_message_id)` and a dedup key on `(mailbox_id, internet_message_id, direction)`. `conversationId` is evidence for threading, **never** the ticket's identity.
- **Signature**, **EmailTemplate**: department-owned (Phase 3/4).

## Tickets
- **Requester**: external or internal person who raises tickets. Department-scoped identity record (`department_id`, email unique per department) so one department's requester history is invisible to another. Optional link to a `User`.
- **Ticket**: department_id, number (`{DEPT_KEY}-{seq}`), subject, status, priority, requester, assignee (user), team, source (email/web/agent), due date, mailbox (originating), created/updated/resolved/closed times, row version.
- **TicketParticipant**: (ticket, email/requester or user, kind: CC | Follower).
- **Conversation/Message**: ticket entries: kind (PublicReply | InternalNote | Inbound | System), author (agent/requester), body (sanitised HTML + text), link to `MailMessage` when email-backed. Notes never leave the system.
- **Attachment**: metadata only (name, content type, size, sha256, storage key, scan state); binary in object storage; department_id denormalised.
- **TicketTag**, **CannedResponse**, **SavedView**, **CustomField***: Phase 4+, department-owned.
- **TicketLink** (merge/relate/parent-child): later; links never span departments unless explicitly permitted.

## SLA / automation (Phase 5)
BusinessHours, SlaPolicy, SlaInstance(ticket), AutomationRule (+ execution log): department-owned.

## Audit
- **AuditEvent** (append-only): id, occurred_at, actor (user id or system/integration), action code, object type+id, department_id (nullable for platform events), previous/new values (jsonb, redacted), source (web/api/email/automation/job), correlation id. No update/delete grants for the application DB role.

## Ticket status model (initial)
`New → Open → Pending (awaiting requester) → Resolved → Closed`; reopening on requester reply to Resolved. Statuses are fixed enums in v1; per-department custom statuses are backlog.

## Ticket numbering
Per-department monotonically increasing integer from a `departments`-keyed counter row updated in the ticket-creation transaction (`UPDATE ... RETURNING`), displayed as `KEY-1234`. Gaps are acceptable on rollback-free design; uniqueness enforced `(department_id, number)`.

## Phase 1 organisation constraints (implemented)
- `departments`: `key` unique (case-insensitive, immutable after creation), `name` unique (case-insensitive); departments and teams are **deactivated, never hard-deleted** (no soft-delete framework; an `is_active` flag). Optimistic concurrency via PostgreSQL `xmin`.
- `teams`: unique `(department_id, lower(name))`; unique `(id, department_id)` to serve as a composite FK target.
- `department_memberships`: PK `(user_id, department_id)`; role is constrained text; FK to `departments`; user reference to `identity.users`.
- `team_memberships`: PK `(team_id, user_id)`, carries `department_id`; composite FK `(team_id, department_id)` → `teams(id, department_id)` and `(user_id, department_id)` → `department_memberships(user_id, department_id)` (`ON DELETE CASCADE`), so a team membership can neither cross departments nor outlive the department membership.
- Identity: `identity.users` (internal id; display name, email, `is_active`, `is_platform_admin`) and `identity.external_identities` (`provider`, `issuer_tenant`, `subject` unique; subject = Entra `oid`). Email/UPN/display name are mutable attributes, never keys.
- `audit.audit_events`: append-only (see ARCHITECTURE/SECURITY). Columns: `id`, `occurred_at`, `category` (`organisation`|`identity`), `action`, `actor_user_id` + `actor_display_name` (snapshot, so names survive renames), `object_type`, `object_id`, `department_id` (nullable), `previous`/`next` (jsonb, minimal/redacted), `source`, `correlation_id`. No foreign keys (audit outlives its subjects). A database trigger rejects UPDATE, DELETE and TRUNCATE for every role; separate DB roles/grants are a deployment hardening still to do.
- Cross-schema integrity: `organisation.department_memberships.user_id` -> `identity.users(id)` (`ON DELETE RESTRICT`) is created in migration SQL. Unique indexes on `lower(key)`, `lower(name)` and `(department_id, lower(name))` are also migration SQL; a trigger makes `departments.key` immutable. Memberships store `role` as constrained text (`DepartmentAdmin|TeamLead|Agent|Viewer`).

## Retention (D-09, unresolved)
Retention is an organisational/compliance decision with no period chosen. Requirements on the schema: no age-based hard-deletes; business entities keep stable ids and creation timestamps so retention rules, export and anonymisation can be added later (e.g. requester/user PII isolated in few columns/tables); the audit log references objects by id and stores redacted values; object storage keys are derivable from rows so binaries can be purged/exported with their metadata.

## Open points
See `docs/plans/OPEN_DECISIONS.md`.
