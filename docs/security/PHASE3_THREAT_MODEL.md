# Phase 3 threat model (email)

| Abuse case | Mitigation | Proof |
|---|---|---|
| Mail for department A lands in B / reply attaches across departments | Department only from the receiving mailbox; evidence into another department discarded; ticket.mailbox composite FK | `MailInboundTests` (cross-department, department isolation), `MailOutboundTests` (never another department's mailbox) |
| Subject spoofing (`Re: [IT-1]`) to read or poison a ticket | Subject reference is a hint only, never attaches; suggestion note only for the receiving department | `A_ticket_reference_in_the_subject_is_a_hint_only...` |
| Forged webhook | Client-state secret per subscription (hash), mailbox from our record, identical 202 | `MailWebhookTests` |
| Duplicate/replayed notifications, crashes mid-pipeline | Idempotency keys end to end, delta as source of truth | `MailLoopTests`, retry tests |
| Mail loops / auto-replies / bounces | Guard (own addresses of all departments, Auto-Submitted, Precedence, lists, DSN) + rate limit | `MailInboundTests` |
| Malicious attachment | Type/extension/signature/size checks, attachment-only download, nosniff, scan gate, department-scoped access | `AttachmentTests` |
| Stored XSS via HTML mail | Allow-list sanitiser; plain text default; sandboxed frame with CSP | `Html_bodies_are_sanitised...`, `mail.test.tsx` |
| Sending as the wrong identity / silent send failure | Sender = ticket or department mailbox only; delivery states truthful; failures visible and retryable | `MailOutboundTests` |
| Header injection via subject/address | Single-line subjects, address regex excludes whitespace, Graph JSON API | `MailRulesTests` |
| SSRF through delta links | Links only followed on the configured Graph host | `GraphProviderTests` |
| Dashboard exposure | Read-only, platform admin only | manual check (401/403/200) |

Residual: no webhook rate limiting; no malware engine; bodies stored unencrypted at application level; real Exchange/Graph behaviour unvalidated (ADR-011); a duplicate send is possible if a worker dies after the provider accepted a message and the sent copy is not yet visible.
