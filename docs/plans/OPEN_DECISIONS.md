# Decisions

## Open
| ID | Decision | Notes / default | Owner |
|---|---|---|---|
| D-04 | PostgreSQL row-level security as an additional isolation layer | Scoped authorization + department-keyed constraints are mandatory regardless. Phase 1 isolation matrix now exists (no leak found); evaluate RLS; revisit before Phase 2 ticket tables. | Principal |
| D-08 | Concrete malware-scanning engine (e.g. ClamAV, commercial API, cloud scanner) | Scanning is **required before any production release that accepts user attachments**. Engine choice not blocking until Phase 3. Abstraction: `IAttachmentScanner` (see SECURITY.md). | Principal / PO |
| D-09 | Data retention policy (periods, export, anonymisation, legal hold) | Organisational/compliance policy, **unresolved**. No automatic deletion, no age-based hard-delete. Schema must keep later retention/export/anonymisation possible. | PO / compliance |
| D-11 | Platform-admin self-granting department membership | Currently allowed as an ordinary, audited membership change (flagged `selfGrant` in the audit event). Consider dual control or notification before production. | PO |
| D-12 | Future audited emergency ("break-glass") ticket access | Not implemented. Constraints recorded in PERMISSIONS.md. | PO |
| D-13 | Persist the ASP.NET Data Protection key ring (PostgreSQL or volume) | Today a container replacement invalidates all sessions and OIDC correlation cookies. Must be settled before any shared/production deployment. | Principal |
| D-14 | User (de)activation API; do inactive users count toward last-admin rules | `is_active` is enforced per request but no endpoint changes it (Phase 1 contract has none). | Principal |
| D-15 | Behaviour when a late reply correlates to a closed ticket (reopen vs new linked ticket) | Raised by the Graph spike (ADR-011). Mail only reports the correlation; Ticketing decides. Needed before Phase 3 threading is finalised. | PO / Principal |
| D-16 | Close the ADR-011 production validation gate (ADR is Accepted as design direction; production readiness conditional on real-tenant validation) | Requires a test tenant + shared mailboxes + Exchange admin (runbook `docs/guides/GRAPH_TENANT_VALIDATION.md`). Gate stays OPEN until the runbook has run successfully against an approved test mailbox. Blocks Phase 3 production email readiness, not Phase 2. | Principal / PO |
| D-17 | Object-store image/provider for local dev and production (S3-compatible) | `minio/minio` is no longer pullable from Docker Hub; compose service moved behind the `storage` profile. Choose (pinned MinIO build, alternative S3-compatible store, or cloud bucket) before Phase 3 attachments. | Principal |

## Resolved
| ID | Decision | Resolution |
|---|---|---|
| D-01 | Entra tenancy | **Single Entra tenant.** No multi-tenant SaaS auth; no `TenantId` columns in the business domain. Auth infrastructure kept separable so multi-tenant could be designed later. |
| D-02 | Platform admin ticket access | **No automatic access.** Platform administration and ticket-data access are separate. Ticket content requires a department membership with the right permission. |
| D-03 | Teams as security boundary | **No (v1).** Departments are the security boundary; teams provide assignment, routing, queues, organisation, workload, reporting. Model must not preclude later team restrictions. |
| D-05 | Requester identity | Per department. |
| D-06 | Ticket numbering | `KEY-n` per department. |
| D-07 | Department mailboxes | **Exchange Online shared mailboxes**, accessed by the application's Entra identity through Microsoft Graph. No mailbox passwords/sessions/interactive sign-in ever. A department may have several; a mailbox belongs to exactly one department in v1. |
| D-10 | Hosting target | Linux + Docker + Compose + PostgreSQL + S3-compatible storage, reverse-proxy compatible, **cloud agnostic**. Entra/Graph are integrations, not hosting requirements. |
