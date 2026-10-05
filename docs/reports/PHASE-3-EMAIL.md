# Phase 3 report: email slice

Status: **built and tested against a simulated mailbox; production email readiness is NOT achieved.** The Microsoft Graph integration has never been run against a real Microsoft 365 tenant (ADR-011 gate open). Phase 4 has not been started.

## What was built
- **Mail module** (`mail` schema): department mailboxes (Exchange shared mailboxes via the application identity, no mailbox credentials), per-mailbox sync state, inbound/outbound messages, webhook receipts. Admin API + UI (create/edit/activate/default, subscription and sync health, "Mail needing attention" with retry).
- **Inbound pipeline:** webhook/lifecycle endpoints (client-state authenticated), delta synchronisation (source of truth), subscription creation/renewal, idempotent persist, loop/noise guard, per-sender rate limit, department from the receiving mailbox, deterministic threading (In-Reply-To, References, outbound token, same-mailbox conversation id; a ticket reference in the subject is a hint only), hand-over to Ticketing, attachments, audit, retries with visible failure.
- **Outbound:** replies queued in the same transaction as the ticket message, sent from the ticket's (or department default) mailbox with signature, retry-safe, truthful delivery states (NotDelivered / Pending / Sent / Failed) with a retry action; CC participants copied.
- **Attachments module:** validation (size, blocked types incl. double extensions, signature check), S3-compatible object store (verified against local SeaweedFS) with an in-memory option, scan seam + download gate, department-scoped download as attachment only.
- **HTML:** allow-list sanitising; original kept immutable; sandboxed formatted view in the UI.
- **Providers:** `MicrosoftGraphMailProvider` (unvalidated) and an in-process simulator used by tests and local development (dev endpoints exist only with the simulator in Development/Testing).
- **Background jobs:** Hangfire (PostgreSQL storage) in the Host: reconciliation, dispatch, subscription maintenance; read-only platform-admin dashboard.
- **Also:** `assign.self` hardening (claim unassigned tickets only), structured error codes already in use, UX updates, seed script with mailbox and emails, two Playwright flows.
Design: ADR-013, `docs/api/PHASE3_API.md`, `docs/architecture/EMAIL_ARCHITECTURE.md`, `docs/security/PHASE3_THREAT_MODEL.md`.

## Test results (one broad pass at the phase boundary, .NET 10 container + PostgreSQL 18)
| Suite | Result |
|---|---|
| `Helpdesk.Host.Tests` | 712/712 after fixing one stale production-config test (includes ~120 new mail/attachment tests) |
| `Helpdesk.ArchitectureTests` | 61/61 (module DAG, boundaries, webhook allow-list, scope-bypass call sites) |
| `Helpdesk.GraphSpike.Tests` | 43/43 |
| Web lint / build / vitest | clean / builds / 62/62 |
| Playwright (real stack, simulator) | 2/2: agent workflow; email in -> reply out from the department mailbox -> requester answer threads onto the same ticket |
| Release solution build | 0 warnings, 0 errors |
| Migrations (PostgreSQL 18) | all six modules down to 0 and up again; 6 schemas |
| Docker host image | builds; started and healthy with Hangfire |
| S3 store vs real S3-compatible endpoint | round trip + 404 OK (SeaweedFS, run with env vars set) |

## Security guarantees tested
Department isolation of mail (composite FKs, discarded cross-department evidence, never another department's mailbox as sender); subject spoofing never attaches; forged/replayed webhooks harmless and indistinguishable; only the two webhook routes are CSRF-exempt; attachments safe-by-default and ticket-authorized; HTML sanitised; no content in audit/logs; dashboard gated. See the threat model.

## MVP acceptance scenario
Automated against the simulator (`MailLoopTests.Mvp_acceptance_scenario...`, Playwright `email-loop.spec.ts`). The **manual run against a real tenant is not done**.

## Requires real-tenant validation (not done)
The whole runbook (`docs/guides/GRAPH_TENANT_VALIDATION.md`): immutable ids, draft/sent `internetMessageId`, whether Exchange sets reply headers on `createReply` (the simulator assumes it does), From/sender with app-only access and SendAs needs, Sent Items lookup timing, delta behaviour and reset, subscription handshake/renewal/lifecycle, throttling, two-mailbox isolation. Also real Entra sign-in.

## Known limitations
No webhook rate limiting; no malware engine (downloads are gated by `RequireCleanScan`, default true in production); raw email bodies stored in the database (retention D-09); inline `cid:` images are listed as attachments, not rendered in place; no outbound attachments; replies carry no quoted history; a worker dying right after the provider accepted a send can in theory send twice if the sent copy is not yet visible; Hangfire shares the main database; Data Protection keys still not persisted (D-13).

## Outstanding decisions
D-16 (execute the runbook; production email gate), D-08 (scanner engine), D-09 (retention incl. stored bodies), D-13, D-11, D-12, D-14.

## Readiness for Phase 4
Ready to start (queues, search, canned responses, SignalR build on the current ticket and message model). Not ready for production email: needs the tenant validation, a scanner decision and the Data Protection key ring.
