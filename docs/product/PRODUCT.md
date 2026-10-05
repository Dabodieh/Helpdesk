# Product

## Vision
A multi-department Helpdesk / ITSM platform. One installation, many departments (IT, HR, Facilities, Finance, Access Care Planning, ...). Each department behaves like its own helpdesk: its own mailboxes, sender identities, signatures, canned responses, forms, fields, teams, agents, roles, queues, SLAs, business hours, automations, notifications, categories, tags and (where appropriate) requester-facing branding. Departments share one platform, one user directory and one codebase.

Inspired by the workflow strengths of Freshdesk, Freshservice and HaloITSM; not a clone and not a copy of their design.

## Principles
1. **Department isolation is the central invariant.** Cross-department data leakage is a critical defect.
2. **Email is first class.** A ticket received on a department mailbox stays in that department; replies leave from that department's identity. There is no global sender.
3. **The agent ticket workspace is the main product surface.** Optimise for agents processing high volumes.
4. **Small-team maintainable.** Modular monolith, few dependencies, explicit code.
5. **Correctness, security and data integrity over speed of generation.**

## Personas
| Persona | Needs |
|---|---|
| Requester | Email in/out, later a portal; sees only their own tickets |
| Agent | Fast queues, ticket workspace, replies, notes, canned responses; may work in several departments with different roles |
| Team lead | Queue oversight, assignment, reassignment within their department/team |
| Department admin | Configures own department only |
| Platform admin | Administers the whole installation (departments, identity, global settings) |

## In scope (long term)
Ticketing core, email (Microsoft 365 first), departments/teams/RBAC, audit, search, views, canned responses, tags, watchers, bulk actions, SLA/business hours, automation, requester portal, knowledge base, reporting.

## Out of scope for now (architecture must not prevent)
Service catalogue, approvals, assets/CMDB, incident/problem/change/release management, onboarding/offboarding, procurement, software inventory, Intune/Entra device integration, Teams integration, AI assistance. See `docs/plans/BACKLOG.md`.

## Non-goals
Microservices, Elasticsearch/OpenSearch, Redis (until a concrete need is demonstrated), a generic workflow engine in v1.
