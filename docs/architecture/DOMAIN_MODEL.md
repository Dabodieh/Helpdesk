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

## Mail
- **Mailbox**: department-owned; provider (`MicrosoftGraph`), address, display name, signature reference, Graph subscription state, delta token, `is_active`; unique address globally (one address cannot serve two departments).
- **MailMessage** (stored email): direction, mailbox, department, provider message id, internet message id, in-reply-to, references, from/to/cc/bcc, subject, html/text body, received/sent time, raw reference in object storage, processing state. Unique `(mailbox_id, provider_message_id)` and a dedup key on `(mailbox_id, internet_message_id, direction)`.
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

## Open points
See `docs/plans/OPEN_DECISIONS.md`.
