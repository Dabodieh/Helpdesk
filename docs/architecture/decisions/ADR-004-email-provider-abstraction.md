# ADR-004: Email provider abstraction (IMailProvider) with Microsoft Graph first

Status: Accepted (2026-10-05)

## Context
Email is first class; other providers later; Graph has specific constraints (delta, webhooks, header limits).

## Decision
Mail module owns IMailProvider; only MicrosoftGraphMailProvider implemented. Inbound: webhook nudge + delta query as source of truth + unique provider-message-id idempotency. Outbound: outbox + job. Threading by headers then conversation id, subject token only for known participants. See EMAIL_ARCHITECTURE.md.

## Alternatives considered
Direct Graph calls from ticketing (lock-in); IMAP polling first (weaker for M365 scale and auth).

## Consequences
+ Provider swap without domain change. - Interface shape must be validated by the Phase 3 spike; R-04 outbound-header risk.
