# Production integration validation plan (single `helpdesk` Compose project)

Scope: first real Entra sign-in and Microsoft Graph mail validation of Production mode (`dev.ps1 prod-*`) in the existing `helpdesk` project, database `helpdesk_prod`. No second environment is created. Development and Production share the container name `helpdesk-backend`, so they run one at a time.

Stop at any step that fails; do not continue to a later stage. Steps marked **APPROVAL** need your explicit go-ahead in chat at that moment.

## 0. Preconditions (checked 2026-10-14, read-only)

- `.env`: tenant/client IDs match the sign-in app (`<sign-in-app-id>`) and mail app (`<mail-app-id>`); Entra secret present; Graph and DP certificates open with their password files and the Graph `.cer` matches `graph.pfx`.
- `helpdesk_prod` migrated: all 7 modules at the source migration count (4/2/3/3/26/1/2).
- Image `helpdesk-host:local` was built after the latest commits and uncommitted web changes.
- Graph propagation window has elapsed. RBAC assignments were created 2026-10-14 16:44Z; the first mailbox check is allowed from 18:44Z. Expected read-only results: A 200, B 200, outside control 403, personal mailbox 403. Do not start Production before A and B return 200.
- `graph.pfx` password rotated 2026-10-14 (same certificate, key and thumbprint; Entra registration unchanged; certificate sign-in verified). The previous password was exposed in a transcript: the offline backup `E:\HelpdeskCertBackup\helpdesk-production-certs-20261014.zip.enc` still holds the old PFX and must be replaced (re-run `backup-production-certs.ps1`), then delete `HelpdeskSecrets\productionetired\`.
- Sign-in app secret was issued with a 30-day lifetime (provisioning script): renew before expiry.
- Backups of `HelpdeskSecrets\production` exist offline (guide section 5).

## 1. Private Production startup

1. Stop Development backend and web only (keeps PostgreSQL up):
   `docker compose -p helpdesk --profile development stop web host`
   `dev.ps1` decides the running mode from Compose project/service labels and container state; `prod-start` and `tunnel-start` refuse while `host` or `web` is running, and `start`/`restart`/`update` refuse while `app` or `cloudflared` is running. Nothing is stopped or removed automatically.
2. `.\scripts\windows\dev.ps1 prod-check` (names only; must print "settings present").
3. `.\scripts\windows\dev.ps1 prod-start`. Expect `helpdesk-backend` healthy, `objectstore-prod` running, nothing published.
4. Verify on the Docker network (no browser, no tunnel):
   - `docker exec helpdesk-backend bash -c "exec 3<>/dev/tcp/127.0.0.1/8080; printf 'GET /api/auth/providers HTTP/1.0\r\nHost: helpdesk.amb-it.co.uk\r\n\r\n' >&3; cat <&3"` → `{"entra":true}`
   - same with `/api/auth/dev-login?subject=x` → 404
   - same with `/health/ready` → 200
   - `dev.ps1 logs app`: no startup validation errors; Graph/storage reachable messages only.

## 2. Entra authentication and Platform Administrator (needs the tunnel)

The app publishes no port, so a browser can only reach it through Cloudflare.

1. Confirm in the Entra portal (read-only) that the sign-in app has redirect URI `https://helpdesk.amb-it.co.uk/api/auth/signin-oidc` and nothing else broad.
2. Confirm the Cloudflare tunnel route is `http://app:8080` and Access policy is allow-list only.
3. **APPROVAL**: `.\scripts\windows\dev.ps1 tunnel-start -ConfirmPublic`.
4. Browse to `https://helpdesk.amb-it.co.uk` (pass Access), sign in with the bootstrap admin account.
5. Expected: lands signed in; account is Platform Administrator; `/api/auth/dev-login` 404; cookie `helpdesk.session` is Secure + HttpOnly.
6. Failure path: sign in with a non-allowed account → `/?authError=...&ref=...`, no stack detail shown.
7. Logout: confirm the session ends and a protected page returns 401/sign-in.

## 3. Graph mailbox connectivity

Use the mailbox addresses you supply (mailbox A, mailbox B, outside control mailbox C).

1. As Platform Administrator, open Mail settings and run the connectivity check for A, then B: both must succeed.
2. Run the check for C: must be rejected (RBAC scope excludes it). A success here is a **stop** finding.
3. Check the sign-in/Graph log lines contain no tokens or message bodies.

## 4. Department mailbox, outbound and inbound

All steps send real mail from the tenant; **APPROVAL** before step 3.

1. Create (or reuse) a test Department; assign mailbox A as its default mailbox.
2. Create a ticket manually; confirm the ticket stores mailbox A.
3. **APPROVAL**: send a public reply to your own test address. Expect From and Reply-To = A, agent signature appended once, status "accepted" (not "delivered" until seen). Verify receipt in your inbox.
4. Reply from your test address to the thread: expect the reply attaches to the same ticket (threading), no new ticket.
5. Send a fresh email from the test address to A: expect a new ticket in the test Department.
6. Send to B (if configured to a different Department): ticket lands in B's Department only; no cross-Department visibility.
7. Confirm no NDR or auto-reply created a ticket/reply and an automatic acknowledgement (if enabled) is not counted as a human response.

## 5. Safe shutdown and Development restoration

1. `.\scripts\windows\dev.ps1 tunnel-stop`
2. `.\scripts\windows\dev.ps1 prod-stop`
3. `.\scripts\windows\dev.ps1 start` (Development at `localhost:5173`).
4. Confirm `docker ps` shows no `cloudflared` and no `helpdesk-backend` in Production; `helpdesk` DB unchanged.
5. Take an encrypted backup of `helpdesk_prod` if anything was written.

No volumes are removed at any point.

## 6. Entra sign-in client secret rotation (30-day secret)

The sign-in app secret was created with a 30-day lifetime. Overlapping rotation, no outage (two secrets may exist at once):

1. At least 7 days before expiry (check with `verify-entra-signin-app.ps1`, which lists secret expiry), add a NEW secret to the sign-in app registration (Entra admin; ideally 6 to 12 months, not 30 days). Do not delete the old one.
2. Put the new value in the git-ignored `.env` as `ENTRA_CLIENT_SECRET` (never in chat, never tracked).
3. Restart the Production `app` only (`dev.ps1 prod-stop`, then `prod-start` during a maintenance window); the secret is read at start.
4. Verify one sign-in works, then delete the OLD secret in Entra.
5. Record the new expiry date in this guide and set a calendar reminder 14 days before it.

Longer term: the Production sign-in secret should have a lifetime matched to the operations calendar; certificates are not supported for OIDC by the application today (the Graph mail app already uses a certificate).

## 7. Status for the first private Production sign-in test (2026-10-14)

Verified: Entra registration (single tenant, redirect URI exact), sign-in secret authenticates, certificate backup 11/11, Graph certificate authenticates, mode guard tests, health check host header, `helpdesk_prod` migrated.

Outstanding before `prod-start`:
1. ~~Graph mailbox check~~ DONE 2026-10-14 18:50Z: A 200, B 200, outside 403, personal 403.
2. Decision on deleting the superseded certificate backups and `retired\` (after the new backup is stored off this PC with its passphrase).
3. Sign-in secret expires 2026-11-13: rotate per section 6 before then.
4. For a browser sign-in: explicit approval to run `tunnel-start -ConfirmPublic` (no published port otherwise).

## 8. Activation runbook (first controlled Production sign-in; NOT executed, needs explicit approval)

Pre-flight facts (2026-10-14): `prod-check` clean; image `helpdesk-host:local` current; `helpdesk_prod` migrated, 0 users; Access allows only <operator-account>; tunnel `Helpdesk` ingress is the 404 catch-all; the standalone container `cloudflared` (exited, not part of Compose) must stay unused.

Known points before activating:
- The tunnel token was printed once in a transcript (`docker compose config` expands `env_file`). Rotate the tunnel token in Cloudflare and replace `.tunnel.env` first. Never run `docker compose config` with the tunnel profile again; use `config -q`.
- Graph change notifications (`/api/mail/graph/*`, anonymous by design, authenticated by client-state) cannot pass Cloudflare Access. Sign-in testing is unaffected; inbound mail over webhooks needs a decision (narrow path bypass for those two POST paths, or another delivery path) before the inbound-ticket test.
- `cloudflare/cloudflared:latest` is unpinned.

Sequence (each step stops on failure):
1. `docker compose -p helpdesk --profile development stop web host` (Development down; postgres stays). Downtime of Development: until rollback or switch back, about 5 minutes for startup.
2. `.\scripts\windows\dev.ps1 prod-check`, then `.\scripts\windows\dev.ps1 prod-start` (waits for health; refuses if host/web still run).
3. Private checks via `docker exec helpdesk-backend` (section 1 step 4): `/health/ready` 200, `/api/auth/providers` `{"entra":true}`, `/api/auth/dev-login` 404, `dev.ps1 logs app` clean.
4. In Cloudflare, set the tunnel's public hostname route `helpdesk.amb-it.co.uk` to `http://app:8080` (and nothing else).
5. **Approval**, then `.\scripts\windows\dev.ps1 tunnel-start -ConfirmPublic`.
6. Browser: Access one-time PIN for <operator-account>, then Entra sign-in; expect Platform Administrator. Unauthenticated `curl` to `/`, `/api/me` must still 302 to Access.
7. Success: stay on Production for the integration steps (sections 3 and 4).

Rollback (any failure, in this order):
1. `.\scripts\windows\dev.ps1 tunnel-stop`
2. Cloudflare route back to the `http_status:404` catch-all.
3. `.\scripts\windows\dev.ps1 prod-stop`
4. `.\scripts\windows\dev.ps1 start` (Development at localhost:5173; nothing was written to `helpdesk`).
`helpdesk_prod` is left as is; no volumes or databases are removed.

## 9. First controlled sign-in result (2026-10-14, 19:00Z)

- Tunnel token rotated (new secret verified by hash; old token invalid); route `helpdesk.amb-it.co.uk` -> `http://app:8080`; four connections registered.
- Unauthenticated `/`, `/api/me`, `/api/auth/dev-login`, `/hubs/*` from outside: 302 to Cloudflare Access.
- Cloudflare Access (one-time PIN, <operator-account>) then Entra sign-in: succeeded. One earlier callback without `state` failed safely (`message.State is null or empty`, redirect to `/?authError=failed`); the retry succeeded.
- `helpdesk_prod`: one active user, platform administrator, Entra identity subject equals `BOOTSTRAP_PLATFORM_ADMIN_SUBJECT`. `/api/me` 200, `/api/csrf` 200, realtime `/hubs/tickets` upgraded (101).
- `dev.ps1`: `Wait-Healthy` strict-mode bug fixed (prod-start and tunnel-start errored after starting). `/hangfire` answers 500 to an unauthenticated request (fails closed; not investigated).
- Development stopped with its database untouched (398 users, 14 tickets, 6 departments). Production and the tunnel are RUNNING until you roll back.

## 10. Graph webhook Access exception (2026-10-14, approved)

Cause of "Inbound subscription failed": Graph's validation POST to `https://helpdesk.amb-it.co.uk/api/mail/graph/notifications?validationToken=...` was answered with a 302 to Cloudflare Access, so `POST /subscriptions` returned 400 (`graph_400`). The origin handler itself was correct.

Cloudflare (account <cloudflare-account-id>):

| Item | Value |
|---|---|
| Main application (unchanged) | `SAMH Helpdesk` `<access-app-id>`, whole hostname, Allow policy `<access-allow-policy-id>` (exactly <operator-account>) |
| Webhook application (new) | `Helpdesk Graph webhooks` `<webhook-app-id>`, self-hosted, destinations `helpdesk.amb-it.co.uk/api/mail/graph/notifications` and `.../lifecycle` only, not in the app launcher |
| Webhook policy | `<webhook-policy-id>`, decision bypass, include everyone (applies to those two paths only) |

Verified from outside without an Access session: both endpoints answer a POST with HTTP 200 `text/plain` echoing the token; GET returns 404 (POST-only); a notification with an unknown subscription/wrong clientState is rejected and logged ("rejected (unknown subscription or client state mismatch)", 202, nothing processed); `/`, `/api/me`, `/admin`, `/hangfire`, `/api/auth/*`, `/api/mail/graph`, `/api/mail/graph/other`, `.../notifications2`, `.../notificationsx` and a `..` traversal all 302 to Access. Trailing slash and case variants of the two paths reach the same webhook handler (Access matches paths case-insensitively); they expose nothing else.

Rollback: delete application `<webhook-app-id>` (webhooks then return to Access).

Subscription for helpdesk-test-a: state `Failed`, no subscription yet. The next automatic attempt is the `mail-subscriptions` job (every 6 h, 00:00Z); delta sync (every 5 min) works without webhooks.

## 11. Manual subscription retry (2026-10-14)

- Added `POST /api/departments/{id}/mailboxes/{mailboxId}/subscription/retry` (permission `department.mailboxes.manage`, department-scoped, CSRF, audited as `mailbox.subscription_retried`) and a "Retry subscription" button on the Department Mailboxes page, shown when an active mailbox has no Active subscription. It calls the same `SubscriptionService.EnsureAsync` as the automatic jobs; a healthy subscription is left alone, and a held subscription id (Active or Failed) is renewed before a new one is created, so a retry cannot leave a duplicate. Graph exposes no list call in our provider, so a subscription whose id we never stored cannot be reconciled (its notifications would be rejected as unknown).
- Also fixed: renewing a subscription held in state Failed now returns the state to Active.
- Tests: `MailAdminApiTests.Retrying_a_failed_subscription_...` plus the Mail namespace (202 passed).
- Deployed to Production by recreating only `app` (rollback image `helpdesk-host:pre-subretry`; Cloudflare and the database untouched).

## 12. Development configuration copied to Production (2026-10-14, 19:50Z)

Backup first: `%USERPROFILE%\HelpdeskBackups\prod\helpdesk-prod-20261014T194859Z.dump.enc` (+ `.sha256`), AES-256-CBC with the existing DPAPI-held passphrase; decrypt round-trip hash matched and `pg_restore --list` read 469 entries.

Applied with `scripts/ops/config-sync-apply.sql` (one transaction, dry-run rolled back first, rerun created nothing):

| Entity | Result |
|---|---|
| Departments | 5 created (ACP, CONN, FAC, FUND, HR; Development ids reused, no clashes); 1 matched by key and left unchanged (IT keeps its Production id and name "IT") |
| Ticket types | 79 created (ACP 27, CONN 16, FAC 0, FUND 10, HR 3, IT 23); 0 skipped |
| Business hours | 1 created (IT) |
| Audit | one `department.created` per new department and one `config.synchronised`, source `config-sync` |

Not copied (by design): users and identities, tickets and messages, mailboxes/subscriptions, personal canned responses (14, owned by Development users), saved views (5, user-owned), teams (2, not part of the current model). Development had no custom fields, SLA policies, automation rules, macros, request forms or knowledge, so there was nothing to copy.

Preservation after the run: 1 user, 1 identity, 1 ticket, 2 messages, 1 membership and the mailbox helpdesk-test-a (Active subscription) unchanged; IT department id unchanged; public hostname still answers 302 to Access. The new departments have no members yet: a Department Admin membership must be added before anyone can work in them.
