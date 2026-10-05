# Email Architecture

Module: **Mail** (+ Attachments). Ticketing depends on mail only through contracts; Mail never writes ticket tables directly.

## Provider abstraction (ADR-004)
```csharp
public interface IMailProvider
{
    ProviderKind Kind { get; }
    Task<MailSubscription> EnsureSubscriptionAsync(MailboxRef mailbox, CancellationToken ct);
    Task<MailDeltaPage> FetchChangesAsync(MailboxRef mailbox, string? deltaToken, CancellationToken ct);
    Task<RawMail> GetMessageAsync(MailboxRef mailbox, string providerMessageId, CancellationToken ct);
    Task<SendResult> SendAsync(MailboxRef mailbox, OutboundMail mail, CancellationToken ct);
}
```
`MicrosoftGraphMailProvider` is the only v1 implementation. Provider-specific ids and types never leak past the Mail module; the domain sees normalised `InboundEmail` / `OutboundMail`. Final shape is set during the Phase 3 spike.

## Inbound pipeline
1. **Webhook** (Graph change notification) → validate `clientState` secret (per subscription), validation-token handshake, reject malformed/oversized; respond 202 fast. Only the notification identifiers are persisted to a `mail.inbound_notification` table (outbox). No processing inline.
2. **Fetch**: a Hangfire job fetches messages for the mailbox using **delta query** (source of truth; webhooks are only a nudge, so lost/duplicate/out-of-order notifications are harmless). A periodic reconciliation delta per mailbox covers missed webhooks and subscription expiry.
3. **Persist raw**: store the message (headers, HTML, text, MIME ref to object storage) as `MailMessage` with unique `(mailbox_id, provider_message_id)` → **idempotency** boundary. Conflict = already processed, skip.
4. **Resolve department**: from the *mailbox* that received it (never from content).
5. **Thread** (see below) → append to existing ticket, or create a ticket + requester, in a single transaction with the `MailMessage` state update and audit event.
6. **Attachments**: validated (size/type/extension/magic bytes), hashed, stored in object storage, scan state tracked; inline images mapped via Content-ID.
7. **Loop/noise protection**: ignore own-mailbox senders, `Auto-Submitted`, `Precedence: bulk/list`, mailer-daemon bounces (bounces recorded against the outbound message instead of creating tickets); per-sender rate limiting.

Failures retry with backoff, then dead-letter into a visible "failed mail" admin list. Nothing is dropped silently.

## Threading (in priority order)
1. `In-Reply-To` / `References` matched against stored `internet_message_id` of messages in **the same department**.
2. Provider conversation id (Graph `conversationId`) within the same mailbox.
3. Ticket reference token in the subject (`[KEY-1234]`) **only if** the sender is a requester/participant of that ticket (prevents hijacking by guessing numbers), and the ticket is in the receiving mailbox's department.
4. Otherwise a new ticket. A subject match alone never merges tickets.
Cross-department threading never occurs: a match found in a different department is ignored (treated as new) and logged.

## Outbound
- A public reply is persisted as an outbound `MailMessage` (state `Queued`) in the same transaction as the ticket message (outbox), then sent by a job → provider `SendAsync` from the department mailbox identity (`From` = mailbox address/display name, department signature appended). Retry-safe: send is guarded by a unique outbound id and recorded provider message id; a job that crashed after sending reconciles by looking up the sent item rather than resending.
- Graph constraint: custom `internetMessageHeaders` may only be `x-*`, so `In-Reply-To`/`References` cannot be set directly. Strategy: reply with Graph `createReply`/`createReplyAll` draft on the original message (Exchange sets the threading headers), falling back to `sendMail` plus `X-` headers and the subject token. To be proven in the Phase 3 spike (risk R-04).
- Record `internetMessageId` of the sent message (read from the draft/sent item) so the requester's reply threads.
- Public replies only ever use the ticket department's mailbox; if the department has no active mailbox, sending fails visibly.
- Internal notes never produce email.

## Graph security and operations
- App registration with **application permissions** limited to specific mailboxes through an Exchange Online **Application Access Policy / RBAC for Applications**, so a compromised credential cannot read other mailboxes.
- Credentials: certificate preferred over client secret; stored in secret store/env, never in DB or logs. Rotation documented.
- Subscription lifecycle: renewal job before expiry, lifecycle notifications handled, re-create on failure; state visible in the admin UI.
- Webhook endpoint: unauthenticated by necessity, therefore validated by `clientState`, strict payload parsing, size limits, rate limit, and idempotent handling (replay-safe). No SSRF surface: the system only calls Graph endpoints from configuration.
- HTML bodies are sanitised on **render** and at ingest (allow-list); remote content/images blocked by default; stored original is immutable.

## Testing
Fake `IMailProvider` for integration tests (the MVP loop runs against it in CI); contract tests for Graph mapping using recorded payloads; a manual runbook for a real tenant.
