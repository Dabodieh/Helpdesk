# Decisions

## Open
| ID | Decision | Notes / default | Owner |
|---|---|---|---|
| D-04 | PostgreSQL row-level security as an additional isolation layer | Not adopted in Phase 2. Isolation layers in place: fail-closed EF scope filter, composite foreign keys, per-action `IAuthorizer` checks, and the Phase 1 + Phase 2 isolation matrices (no leak found). Re-evaluate before production hardening or if a new bypass path appears. | Principal |
| D-08 | Concrete malware-scanning engine (e.g. ClamAV, commercial API, cloud scanner) | Scanning is **required before any production release that accepts user attachments**. Engine choice not blocking until Phase 3. Abstraction: `IAttachmentScanner` (see SECURITY.md).  Phase 3: the scan seam and the `Attachments:RequireCleanScan` download gate exist; no engine yet. | Principal / PO |
| D-09 | Data retention policy (periods, export, anonymisation, legal hold) | Organisational/compliance policy, **unresolved**. No automatic deletion, no age-based hard-delete. Schema must keep later retention/export/anonymisation possible. | PO / compliance |
| D-11 | Platform-admin self-granting department membership | Currently allowed as an ordinary, audited membership change (flagged `selfGrant` in the audit event). Consider dual control or notification before production. | PO |
| D-12 | Future audited emergency ("break-glass") ticket access | Not implemented. Constraints recorded in PERMISSIONS.md. | PO |
| D-13 | Persist the ASP.NET Data Protection key ring (PostgreSQL or volume) | Today a container replacement invalidates all sessions and OIDC correlation cookies. Must be settled before any shared/production deployment. | Principal |
| D-14 | User (de)activation API; do inactive users count toward last-admin rules | `is_active` is enforced per request but no endpoint changes it (Phase 1 contract has none). | Principal |
| D-16 | Close the ADR-011 production validation gate (ADR is Accepted as design direction; production readiness conditional on real-tenant validation) | Requires a test tenant + shared mailboxes + Exchange admin (runbook `docs/guides/GRAPH_TENANT_VALIDATION.md`). Gate stays OPEN until the runbook has run successfully against an approved test mailbox. Blocks Phase 3 production email readiness, not Phase 2. | Principal / PO |

## Resolved
| ID | Decision | Resolution |
|---|---|---|
| D-15 | Late reply on a Closed ticket | **Reopen the same ticket** (status Open, same department, reference, team and assignee, audited, system actor). Pending/Resolved also return to Open. No follow-up ticket by default; a per-department setting may change this later. Implemented as `ITicketIngress` (Phase 2); mail ingestion arrives in Phase 3. |
| D-17 | Object store for local dev | **SeaweedFS** (S3-compatible) behind the compose `storage` profile; the app depends only on a generic S3 endpoint. Production provider stays deployment-configurable (Azure Blob via a provider implementation, AWS S3, Cloudflare R2, other). |
| D-01 | Entra tenancy | **Single Entra tenant.** No multi-tenant SaaS auth; no `TenantId` columns in the business domain. Auth infrastructure kept separable so multi-tenant could be designed later. |
| D-02 | Platform admin ticket access | **No automatic access.** Platform administration and ticket-data access are separate. Ticket content requires a department membership with the right permission. |
| D-03 | Teams as security boundary | **No (v1).** Departments are the security boundary; teams provide assignment, routing, queues, organisation, workload, reporting. Model must not preclude later team restrictions. |
| D-05 | Requester identity | Per department. |
| D-06 | Ticket numbering | `KEY-n` per department. |
| D-07 | Department mailboxes | **Exchange Online shared mailboxes**, accessed by the application's Entra identity through Microsoft Graph. No mailbox passwords/sessions/interactive sign-in ever. A department may have several; a mailbox belongs to exactly one department in v1. |
| D-10 | Hosting target | Linux + Docker + Compose + PostgreSQL + S3-compatible storage, reverse-proxy compatible, **cloud agnostic**. Entra/Graph are integrations, not hosting requirements. |
