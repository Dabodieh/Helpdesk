# ADR-010: Shared mailboxes via application identity; cloud-agnostic hosting

Status: Accepted (2026-10-05). Refines ADR-004; resolves D-07, D-10.

## Context
Departments need independent sender identities without anyone signing into mailboxes.

## Decision
- Department mailboxes are Exchange Online **shared mailboxes** accessed through Microsoft Graph with the helpdesk application's own Entra identity. No mailbox credentials/sessions are used or stored. Production access is to be scoped to helpdesk mailboxes only (Exchange RBAC for Applications). A department has many mailboxes; a mailbox belongs to one department; tickets retain their mailbox; replies never fall back to a global sender (composite FK on `(mailbox_id, department_id)`). Agents need no Exchange delegation permissions.
- Hosting target: Linux, Docker/Compose, PostgreSQL, S3-compatible storage, any reverse proxy. Microsoft integrations must not become hosting dependencies.

## Alternatives considered
Per-mailbox user accounts with passwords/OAuth delegation (credential sprawl, MFA friction); tenant-wide application permissions (blast radius); Azure-only hosting (lock-in).

## Consequences
+ Smaller credential surface, clear isolation. - Requires Exchange admin setup (operator runbook); some Graph behaviours on shared mailboxes must be validated in a real tenant (see EMAIL_ARCHITECTURE.md spike section).
