# ADR-015: Requester portal, request forms and knowledge module

Status: accepted (Phase 6). Production gates D-16, D-08, D-09, D-13 remain open.

## Decisions

1. **Requester identity is an explicit link.** `requester_links` ties a department-scoped `Requester` to an Entra `User`
   (`requester_id` unique; `(user_id, department_id)` unique). A link is created only from the verified email in the
   Entra token (`ICurrentUser.Email`) and only while the requester is unclaimed. Later access is by link id, never by
   re-matching the address, so an impostor sharing an address cannot claim or read.
2. **Requester authorisation is a separate boundary.** `/api/portal/*` endpoints are authenticated-only and scope every
   query by the caller's links. Agent permissions (`IAuthorizer`) are never used for portal reads. Inaccessible
   objects return 404.
3. **Primary requester only (v1).** Participants/CC recipients get no portal access. Backlog item.
4. **Purpose-built DTOs.** Portal DTOs are separate types; internal notes, assignee identity, SLA data, routing and
   audit are never serialised to requesters.
5. **Visible activity timestamp.** `tickets.last_public_activity_at` (first message, public messages, status changes)
   drives portal ordering and timestamps, so internal notes leak nothing through ordering, counts or realtime.
6. **Portal replies use the ticket domain path** used by inbound email (reopen per D-15, SLA, `RequesterMessageAdded`,
   agent realtime). Email and portal share one conversation. Message `source = portal`, ticket `source` in
   agent/email/portal.
7. **Request forms** are per department, published to all employees, with typed custom fields only (no conditional
   logic, scripts, HTML or formulas). Field key and type are immutable; options keep stable values; fields are
   deactivated, never deleted. Submitted values are snapshotted (label, type, display value, visibility) in
   `ticket_field_values`, so later edits never change history. Protected values (department, status, requester,
   source) are server-derived.
8. **Attachments on submit** are added as a follow-up portal reply through the existing attachment pipeline;
   `RequireCleanScan` is unchanged.
9. **Knowledge is its own module** (migration order 60, depends only on SharedKernel, Audit, Organisation). One-level
   categories; Draft/Published/Archived with separate draft and published slots; PostgreSQL full-text search
   (generated `tsvector` over published fields, GIN index); audiences `Everyone` | `Requesters` resolved through
   `IRequesterAudience` (contract in Knowledge, implemented by Ticketing). Markdown is rendered by a safe renderer
   that never emits raw HTML.
10. **Realtime to requesters** uses group `r:{requesterId}`, carrying only `requestChanged {reference, kind, at}`.
    Hub access derives from `requester_links`.
11. **Permissions:** `department.forms.manage`, `department.knowledge.manage` (DepartmentAdmin).

## Consequences

- New module and migrations (Ticketing `Phase6Portal`, Knowledge `Initial`, Audit `KnowledgeCategory`).
- Portal pages are a separate route tree and shell (`/portal/*`); non-agents land there.
- Deferred: see BACKLOG (Phase 6 items).
