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
