# ADR-035: Microsoft 365 administration page and guided mailbox setup

Status: accepted (read-only checks and guided setup implemented; automatic mailbox creation not implemented)

## Context

Administrators needed one place to see how Helpdesk uses Microsoft 365 and a guided way to connect shared mailboxes. Sign-in (Entra OIDC), mail processing (Graph application identity) and mailbox creation (Exchange management) are different identities with different privileges and must stay separate (ADR-010, ADR-011). No tenant is available in development, so nothing here can be validated against Microsoft 365 (D-16).

## Decision

- `Administration > Microsoft 365` is platform-administrator only (`platform.departments.manage`, the same gate as registering a mailbox address). It reports identifiers (tenant id, client ids), whether a credential exists and its kind, never a secret, token or raw payload.
- Server endpoints (`/api/admin/microsoft-365`): status, `check-connection` (asks only for a token), `check-mailbox` (validates the address, looks for a duplicate, probes access: one read request, registers nothing) and `new-mailbox-plan` (validates a request and returns an Exchange Online PowerShell script for a tenant administrator). Checks are audited (`m365.*`). Nothing in them creates, changes or deletes anything in Microsoft 365.
- Registering an existing mailbox uses the existing endpoint (`POST /api/departments/{id}/mailboxes`): platform administrator, access probe first, then register, subscribe and first sync. The wizard calls it only on its last step.
- Creating a mailbox is **manual**: the script uses `New-Mailbox -Shared`. Display names are restricted to letters, digits and a few separators so the script cannot carry shell syntax. `Mail:ApprovedDomains` (optional list) restricts the domain of a planned mailbox; empty means unrestricted by Helpdesk.
- Automatic creation is **not built**. It needs Exchange Online management (the `Exchange.ManageAsApp` application permission plus an Exchange role such as Recipient Management, or equivalent), held by an identity separate from the mail application, and an approval. See open decision D-18.
- Wizard progress is kept in the browser session only. Every step except the last is a read.

## Consequences

- Platform administrators can see mailbox health across departments (address, department, health, sync, delivery counts). No ticket content is involved.
- Helpdesk never deletes a Microsoft 365 mailbox, including when setup is abandoned.
- Local Docker without a public HTTPS address: the Graph provider currently requires `Mail:NotificationBaseUrl` to be a public https URL at startup, because subscriptions are created when a mailbox is registered (D-19).
