# Controlled live-tunnel check: client address (S-03) and Graph subscription renewal

Status: EXECUTED 2026-10-16 (17:44Z to 17:57Z) for steps 1 to 7 and 10; results in the master audit section 26. Steps 8 and 9 (subscription retry and mail proof) were deliberately NOT run: the Failed subscription was to be reported, not repaired. Original plan: Cloudflare, the tunnel, Access, Entra and Graph have not been touched. Every step that starts the tunnel or sends traffic through it needs your explicit go-ahead for the window.

## 1. Why, and what is already known
- **Client address (S-03).** The application is proven, with synthetic requests, to honour only the rightmost `X-Forwarded-For` entry from a trusted proxy and to rate-limit per forwarded client (`ClientAddressTrustTests`, plus an internal-network check of the deployed Production app). Not proven: what Cloudflare and cloudflared really send (does Cloudflare append the connecting address; does cloudflared pass it unchanged).
- **Graph subscription.** State `Failed` since the 12:00Z `mail-subscriptions` run on 2026-10-16. Cause chain from the code and log (renewal PATCH rejected, then `POST /subscriptions` returned `graph_400`, while the tunnel was stopped): Graph validates the notification URL, which cannot be reached with the tunnel down. Unconfirmed with Microsoft. Verified in the database: the application has **lost the id** of the Graph-side subscription (`subscription_id` is null), so it can no longer renew it; that old subscription ((id withheld)) expires on its own at 2026-10-17 19:27Z. A retry creates a new one. Inbound mail is unaffected (5-minute delta polling).
- **Existing exception.** An approved, path-limited Access bypass (`Helpdesk Graph webhooks`, only `/api/mail/graph/notifications` and `/lifecycle`) is recorded in `PRODUCTION_INTEGRATION_VALIDATION.md` section 10; it was verified from outside on 2026-10-14 but not today.

## 2. Decisions needed from you before any window
1. **When.** Recommended: a supervised 30 to 45 minute window finished before 2026-10-17 18:00Z (the next scheduled maintenance run is 18:00Z, and the old subscription lapses at 19:27Z). Earlier is better; a window today avoids an unattended run.
2. **Who does the browser steps.** Access one-time PIN and Entra sign-in need the single allowed operator account; I cannot sign in for you.
3. **Second vantage point** for the per-client proof (section 4, step 6): phone hotspot or another network. If unavailable, that step is skipped and the result is recorded as partial.
4. **Forged-header test with curl** (optional, step 7): needs the operator to run one command locally with the `CF_Authorization` cookie; I never see the cookie. If declined, the "Cloudflare appends" claim stays documented-but-unverified.
5. **After the window:** stop the tunnel again (default; Production stays private) or leave it running. If it stays stopped, the new subscription will lapse in about 3 days and the status will read Failed again; the real fix for a private deployment is the unbuilt polling-only mode (open decision D-19).
6. **Access and webhook rules:** you confirm in the Cloudflare dashboard, or authorise me to read them through the Cloudflare connector (it needs to be authorised in your connector settings; read-only), that the two applications still exist and are unchanged.

## 3. Preconditions (checked immediately before the window, all local)
- Working tree clean; `master` = `b505868` plus the local docs commits; Production runs `helpdesk-host:1.2.0-s02s03-b505868`, healthy.
- `dev.ps1`'s `health-check-production.ps1`: only the known subscription FAIL.
- A backup under 24 hours old with a hash-equal copy on `I:` (today's `...20261016T170409Z` qualifies until 17:04Z tomorrow; take a new one if the window is later).
- `.tunnel.env` exists (not read) and the tunnel image is the pinned `cloudflared:2026.7.3@sha256:e39ee8da...`; `dev.ps1 tunnel-start -ConfirmPublic` refuses unless the app is running.
- You are at the PC, ready for the PIN, and no other Cloudflare change is in flight.
- Abort criteria at any step: unexpected 5xx or 4xx from Access, any sign that traffic reaches the origin without Access, any application error in the log, any hint of a second person's session.

## 4. Sequence (T = start)
| Step | Action | Expected | Stop if |
|---|---|---|---|
| 0 (T-10) | Preconditions above. Note the time. | all pass | any fail |
| 1 | You approve in chat: "start the tunnel now". I run `dev.ps1 tunnel-start -ConfirmPublic`. | cloudflared registers connections; app unchanged | does not register in 2 minutes: `tunnel-stop` |
| 2 | From outside, no Access session: `GET /` and `GET /api/me` and `GET /api/auth/dev-login`. | 302 to the Access login for all | any 200 or origin response |
| 3 | Same, webhook paths: `POST /api/mail/graph/notifications?validationToken=probe` and `/lifecycle`. | 200 text/plain echoing the token (bypass works); `GET` on them 404 | 302 (bypass gone: renewal cannot work, stop and report); any other path reaching the origin |
| 4 | You sign in (Access PIN, then Entra). | `/api/me` shows the Platform Administrator; the app log shows the callback | failure other than a retried first callback |
| 5 | **Client-address proof, one vantage.** In the signed-in browser, same origin, run a loop of 70 `fetch('/api/auth/dev-login?subject=x')` calls (the route answers 404 in Production but is counted by the sign-in limiter, 60 a minute). | 61st response 429, the first 60 are 404. A shared bucket would be limited much earlier (other traffic) and a missing forwarded address would key every user on cloudflared's address | 429 well before 61 (suggests the key is not the client) |
| 6 | **Second vantage.** Switch the browser/PC to the phone hotspot (new public address), keep the Access session, immediately repeat 5 for 5 requests. | 404, not 429: a different client has its own bucket although both arrive through the same tunnel | 429 (shared bucket: the forwarded address is not being used) |
| 7 | **Forged header (optional).** In a local terminal, one `curl` with the `CF_Authorization` cookie from the browser held in an environment variable (never typed into chat or a file), sending `X-Forwarded-For: 198.51.100.9` on the request that follows step 5 within the same minute. | still 429: the spoofed left-hand entry did not change the client | 404 (the forged value was used as the client) |
| 8 | **Subscription.** In the signed-in app: Department Mailboxes, "Retry subscription" for the IT mailbox (permission `department.mailboxes.manage`; audited as `mailbox.subscription_retried`). I watch the log and database. | state Active, new `subscription_id`, expiry about 3 days out; log shows `POST /subscriptions` 201 | `graph_400` again: capture the exact Graph error (code/message from the log, never the token), do not retry in a loop; then stop |
| 9 | Notification proof. Send one test message to the mailbox from the operator's own mail account. | a ticket appears within the 5-minute poll, and the log shows the notification accepted if Graph delivers (a delivered notification is the real proof of the bypass path) | message lost (no ticket after 15 minutes) |
| 10 | Close: `dev.ps1 tunnel-stop` (or leave running if you decided so in 2.5). Re-run the unauthenticated probes of step 2 expecting a connection error/530. Run the health check. | tunnel exited; Production private; health check clean (the subscription now PASS) | |
| 11 | Record: exact outcome per step, times, no secrets, in the master audit (S-03 and the integration validation record); move S-03 to PASS or leave PARTIAL with the missing items. | | |

## 5. Rollback
- `dev.ps1 tunnel-stop` at any point. Nothing else needs undoing: no code, schema, Cloudflare or Entra changes are part of this check.
- If the subscription retry misbehaves: do not edit the database by hand. Inbound mail keeps flowing by polling; report the Graph error and decide next steps.

## 6. Risks and limits
- While the tunnel is up, the hostname is public behind Access (default deny, one allowed account) and the two webhook paths are public by design; all other paths were verified to be denied at the edge on 2026-10-14 and are re-probed in step 2.
- The step-5 loop locks the operator's own sign-in endpoint for under a minute.
- The result describes Cloudflare as seen on that day from two networks; it does not prove behaviour for a different Cloudflare plan or a future proxy.
- The browser steps cannot send a forged `X-Forwarded-For` (browsers forbid setting it), which is why step 7 is separate and optional.
- Cloudflare account settings are not changed by this plan. If the webhook bypass is missing, fixing it is a separate approved Cloudflare change.
