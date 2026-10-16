# Inbound mail: polling and webhook modes

Status 2026-10-16. Decision: for the private Production pilot, Microsoft Graph **delta polling** is the supported way inbound mail is found; webhook subscriptions are deferred until the host is always-on (D-19, now built).

## 1. The two modes (`Mail:InboundMode`)
| Mode | What happens | When to use |
|---|---|---|
| `Polling` | Every 5 minutes the delta synchronisation (`SyncAll`) asks Graph for changes and creates tickets and replies. **No subscription is created, renewed, retried or reported.** `Mail:NotificationBaseUrl` is not needed. | A private host, a pilot, anywhere Graph cannot reach the application. Default in `docker-compose.yml` (`MAIL_INBOUND_MODE`, default `Polling`). |
| `Webhook` (code default) | Graph change-notification subscriptions nudge the same 5-minute delta sync (which stays the source of truth). Needs a public https `Mail:NotificationBaseUrl`, the Access bypass for `/api/mail/graph/notifications` and `/lifecycle`, and an endpoint that is reachable at every renewal. | An always-on public deployment. |

Delta polling runs in both modes; processing, threading, duplicate protection and outbound mail are identical.

## 2. What polling mode changes
- `EnsureAsync` (mailbox registration or update, lifecycle events, the manual retry) and the 6-hourly `mail-subscriptions` job return immediately without any Graph call. The job still appears as succeeded in Hangfire; in polling mode that means "nothing to do".
- Health: the subscription is not part of the picture. A mailbox is Healthy when the mail check works; `sync_failing` (consecutive failures) and `sync_not_started` or stale checks still make it Attention or Failed. The web UI says "New mail: checked every 5 minutes (polling)"; the Microsoft 365 page says "Regular check (polling)"; "Retry subscription" is not offered and the endpoint answers 409.
- Stored data is never rewritten: an existing subscription id, state, expiry, client-state hash and the delta token are left exactly as they are, so switching back to `Webhook` resumes from the same record. A webhook that still arrives for a subscription Graph holds is still accepted (the client state decides).
- `scripts/windows/health-check-production.ps1` reads the mode from the running container: in polling mode it checks only the mail check (last sync age, consecutive failures, delta error) and prints "polling mode (intentional, no subscription)".

## 3. Two error fields (both modes)
`mail.mailbox_sync.last_sync_error` and `consecutive_failures` describe the **delta (mail check)** only and are cleared by the next successful check. `subscription_error` (new column, migration `AddSubscriptionError`) describes the last **subscription** create or renew failure and is cleared by the next success. A successful poll no longer erases the reason a subscription failed, and a subscription failure no longer hides a failing mail check. The subscription error now carries Graph's own `error.code` and a short message (for example `PermanentProviderException:graph_400: ExtensionError: ...`), which the earlier `graph_400` alone did not.

## 4. Subscription id handling (webhook mode)
A renewal that Graph refuses for any reason other than "not found" (404, 410) keeps the stored subscription id: the subscription may still exist at Graph, and discarding the id (the earlier behaviour) made it unrenewable and left a duplicate behind when a replacement was created. Only a subscription Graph no longer knows is replaced.

## 5. Switching to webhook mode later
1. Always-on public https hostname for the application; the two webhook paths reachable by Microsoft (Access bypass limited to those paths; note the bypass currently matches by prefix, see the S-03 record).
2. Set `MAIL_INBOUND_MODE=Webhook` and `Mail:NotificationBaseUrl` (`PUBLIC_HOST`) in `.env`, recreate the application only.
3. The next `mail-subscriptions` run (every 6 hours at 00, 06, 12, 18 UTC) or an administrator's "Retry subscription" creates or renews the subscription; check the mailbox shows Active and the log has no `subscription maintenance failed`.
4. Keep the endpoint reachable: a renewal while it is unreachable fails and turns the mailbox Failed (delta polling continues meanwhile).

## 6. Operating notes
- Latency: new mail is noticed within 5 minutes in polling mode (instant nudge in webhook mode).
- `helpdesk_mail_sync_oldest_age_seconds` (above 1800 means the mail check itself stopped) is the key signal in both modes.
- To confirm polling is working: the mailbox shows a recent "Last mail check", and `select last_sync_at, consecutive_failures, last_sync_error from mail.mailbox_sync` shows a time within 5 minutes and no failures.
