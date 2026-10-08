# Runbook: Microsoft Graph shared-mailbox validation (real test tenant)

Purpose: run the experiments E-1..E-38 listed in `docs/architecture/EMAIL_ARCHITECTURE.md` ("Graph spike findings") against a **test** Microsoft 365 tenant, record observations, and then confirm or amend ADR-011 (accepted as design direction; this run closes its production validation gate) and close or re-rate R-04.

Status: **not yet run. D-16 is OPEN.** Real-tenant validation attempted for Refinement 3 (2026-10-07): **BLOCKED / NOT RUN**. The development environment has no Graph configuration (no `Mail__Graph__*` settings, no approved test tenant, shared mailbox or safe recipient), and none may be invented. Everything below the provider seam is covered by simulator tests only; simulator results are not Graph evidence.

Minimum operator evidence still required to close D-16 (one controlled message per scenario, record ids/booleans only, never bodies or secrets): (1) public reply: From = ticket shared mailbox, Reply-To = same mailbox, agent address not exposed, sent copy location and owner in Sent Items (E-10, E-12, E-21..E-26); (2) one ticket, reply, change team/assignee, reply again: same From; (3) threading: reply to an inbound message, then a requester reply: In-Reply-To/References and Graph vs Internet message ids as stored in `mail.messages` (E-23); (4) Graph identifier behaviour: does `internetMessageId` of the draft equal that of the Sent Items copy, is the immutable id of the draft the sent copy id (what `FindSentCopyAsync` relies on); (5) one acknowledgement if enabled; (6) one controlled invalid-recipient NDR: does it arrive within the test window, and does it quote our `[hd:token]` footer and/or carry In-Reply-To/References of the sent copy (the two pieces of evidence `NdrCorrelation` uses); (7) whether Graph accepts an `Auto-Submitted` header on a draft (it is believed to allow only `x-` internet headers, so acknowledgements do NOT carry it; they are protected by inbound suppression and the sender rate limit instead).

Original status:  Written from Microsoft documentation only. Nothing in this runbook was executed by the authors; request/response shapes may need small corrections on first contact.

Rules for the run
- Use a dedicated test tenant (or a clearly separated test OU). Never run against production mailboxes.
- Use two shared mailboxes, e.g. `helpdesk-a@<tenant>` (department A) and `helpdesk-b@<tenant>` (department B), plus one outside the scope, `outside@<tenant>`, as a negative control. Two external sender accounts (e.g. a Gmail and an Outlook.com address) as requesters.
- Never commit secrets. Keep the client certificate/secret in your secret store; examples use `$TOKEN` placeholders. Do not paste message bodies or tokens into tickets or the repo; record only ids, header names, booleans and timings.
- Record every result in a copy of the table at the end of this file (observed / matches expectation / notes).

## 0. Quick path: one supervised session (read this first)
The experiments below are the detail. This is the minimum that closes D-16, in order. Stop and record at the first unexpected result; do not "fix forward" in the tenant.

**Entry criteria (all must be true, none is invented by the agent):** a non-production tenant or an approved separate test mailbox set; two shared mailboxes plus the outside-scope control; an application registration restricted to those mailboxes (section 1); one external test address you control; a named operator; a copy of the results table (section 3). Start with a staging deployment pointed at that tenant, never the production database.

| Area | Do (one controlled message each) | Evidence to record (ids, booleans, headers only; never bodies or secrets) | Pass when |
|------|----------------------------------|-------------------------------------------------------------------------|-----------|
| Access and scope | E-1..E-4: read mailbox A and B, be refused on the outside mailbox | HTTP status per mailbox | A and B succeed; outside is denied (403/404), not silently empty |
| Receiving | E-5..E-11: external address mails A; observe webhook and delta; move the message Inbox, Archive, Inbox | notification latency, immutable id before/after move, one ticket created | exactly one ticket; same immutable id after moves; no duplicate on replay |
| Sending | E-12, E-21..E-26: agent public reply on that ticket; then change team and assignee and reply again | From and Reply-To of each sent copy, Sent Items location and owner, agent address absent | From = ticket mailbox both times; Reply-To = same mailbox; agent personal address never appears |
| Threading | E-15, E-19, E-23: requester replies to the reply; reply again after a subject edit | In-Reply-To/References of the arrival, ticket it attached to, `conversationId` | reply attaches to the same ticket; edited subject does not split it unexpectedly |
| NDRs | E-27..E-29: send to one deliberately invalid recipient | does the NDR arrive, our token/footer quoted, resulting message state | NDR is not a requester ticket or reply; the outbound message becomes failed/bounced; a late NDR does not flip a delivered message |
| Acknowledgement | E-23 region, if enabled for the department | one acknowledgement from the ticket mailbox, no agent signature, no loop | one acknowledgement only; none for auto/bounce/own-address mail |
| Recovery | (a) block the webhook for 15 minutes, send mail, restore; (b) let or force a subscription to expire (E-31..E-35); (c) revoke then restore the app consent on mailbox B; (d) make the stored delta token invalid if the tenant allows | mailbox health state at each step (Healthy / Attention / Failed), time to catch up, tickets created | (a) mail arrives via the 5 minute delta reconcile with no loss or duplicate; (b) maintenance job recreates exactly one subscription; (c) health shows the problem without exposing secrets, and recovers after consent returns; (d) resync from start creates no duplicate tickets |
| Uncertain send | simulate a send timeout if safely possible | state Uncertain, the automatic re-check after about 10 minutes, Sent Items check | no blind retry; never a second copy sent |

**After the session:** copy the filled table into the results section, mark each of E-1..E-38 confirmed/amended/not run, update ADR-011 and risk R-04, set D-16 in `OPEN_DECISIONS.md` and the readiness report. Anything not run stays open and is stated as open. Simulator results are not evidence.

**Not authorised to start without the criteria above.** As of 2026-10-08 no Graph configuration, tenant, shared mailbox or safe recipient exists in the development environment, so none of this has been run.

## 1. Prerequisites and least-privilege setup (operator steps)

1. **Entra app registration** (single tenant). Credential: certificate preferred. Under *API permissions* do **not** grant admin consent for any `Mail.*` application permission (unscoped Entra grants are unioned with the scoped Exchange grants and would void the scoping; Microsoft docs: https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac). Record `AppId` (client id) and the **service principal object id** from *Enterprise applications* (not the app registration object id).
2. **Exchange Online PowerShell** (Organization Management / Exchange Administrator):
   ```powershell
   Connect-ExchangeOnline
   # pointer to the Entra service principal
   New-ServicePrincipal -AppId <AppId> -ObjectId <ServicePrincipalObjectId> -DisplayName "Helpdesk Graph"
   # scope = helpdesk shared mailboxes only (example uses a custom attribute you set on exactly these mailboxes)
   Set-Mailbox helpdesk-a@<tenant> -CustomAttribute1 HelpdeskApp
   Set-Mailbox helpdesk-b@<tenant> -CustomAttribute1 HelpdeskApp
   New-ManagementScope -Name "HelpdeskMailboxes" -RecipientRestrictionFilter "CustomAttribute1 -eq 'HelpdeskApp'"
   # least privilege roles: read/write mail (no send) + send
   New-ManagementRoleAssignment -App <ServicePrincipalObjectId> -Role "Application Mail.ReadWrite" -CustomResourceScope "HelpdeskMailboxes"
   New-ManagementRoleAssignment -App <ServicePrincipalObjectId> -Role "Application Mail.Send"      -CustomResourceScope "HelpdeskMailboxes"
   # verify (bypasses the cache)
   Test-ServicePrincipalAuthorization -Identity <ServicePrincipalObjectId> -Resource helpdesk-a@<tenant>   # InScope = True
   Test-ServicePrincipalAuthorization -Identity <ServicePrincipalObjectId> -Resource outside@<tenant>      # InScope = False
   ```
   Alternative scope: an administrative unit (`-RecipientAdministrativeUnitScope`). Group-based scoping is supported only for direct members. Grants take 30 min to 2 h to take effect; the test cmdlet bypasses the cache.
3. **Graph permissions needed, and no more:** Exchange application roles `Application Mail.ReadWrite` (read mail, headers, drafts, subscriptions, delta) and `Application Mail.Send` (send). Not needed: `Mail.ReadBasic.All` (omits body/headers), `*.Shared`, `MailboxSettings.*`, calendar/contact roles, SendAs/SendOnBehalf grants (to be confirmed by E-21/E-22; if Exchange demands SendAs, record it and raise it as an ADR-010 consequence rather than granting broadly).
4. **Webhook endpoint:** a public HTTPS endpoint (TLS 1.2+) reachable from Microsoft Graph, e.g. a dev tunnel or a test deployment of the Host behind your reverse proxy. For the spike a trivial echo service that implements the validation handshake and logs only `subscriptionId`, `changeType`, `resourceData.id`, clientState-match boolean is enough. Cloud agnostic: no Azure service is required.
5. **Token (client credentials):**
   ```http
   POST https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token
   Content-Type: application/x-www-form-urlencoded

   client_id=<AppId>&scope=https%3A%2F%2Fgraph.microsoft.com%2F.default&grant_type=client_credentials
   &client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer&client_assertion=<signed JWT>
   ```
   Use `Authorization: Bearer $TOKEN` below. Look up each mailbox's object id once: `GET /users/helpdesk-a@<tenant>?$select=id,mail` needs directory read; if the app has none, get the object ids from the Entra portal instead. Use the **object id** (`$A`, `$B`) in all paths below.

All requests below use base `https://graph.microsoft.com/v1.0`. Add `Prefer: IdType="ImmutableId"` on every request unless an experiment says otherwise.

## 2. Experiments

### A. Access and scoping (E-1..E-4)
- **E-1** `GET /users/$A/mailFolders/inbox/messages?$top=5&$select=id,internetMessageId,conversationId,subject,receivedDateTime` -> 200, shared mailbox without licence or sign-in. 
- **E-2** Same request for `$OUTSIDE` -> expect 403 (`ErrorAccessDenied`). Re-test after removing the role assignment to measure propagation (E-4).
- **E-3** Create a subscription (E-30), a draft (E-21) and send it under the scoped grants only.
- **E-4** Record minutes between `New-ManagementRoleAssignment` and first success/denial.

### B. Delta and immutable ids (E-5..E-11)
Send 3 mails from the external accounts to mailbox A.
- **E-7** `GET /users/$A/mailFolders/inbox/messages/delta?$select=id,internetMessageId,conversationId,subject,receivedDateTime,isRead` with `Prefer: odata.maxpagesize=50, IdType="ImmutableId"`. Follow `@odata.nextLink` until `@odata.deltaLink`; store it. Repeat with the deltaLink after sending one more mail: only the new message appears (plus `@removed`/read-state entries if you touched mail).
- **E-8** Keep a deltaLink for a long idle period (e.g. 7+ days); record the response (expected possibly `410 Gone`/`SyncStateNotFound`; the Mail module must then do a full resync).
- **E-9** Verify ids in `nextLink` pages are immutable-format when the header was set on the first call only.
- **E-10** Pick message M (immutable id `I1`). `POST /users/$A/messages/{I1}/move` `{"destinationId":"archive"}` then back to `inbox`. `GET /users/$A/messages/{I1}` after each: expect the same `id` (documented), while a control request **without** the header returns different ids per folder (**E-11**). Optionally move into the user "Archive mailbox" if the tenant has one: documented to change the id.
- **E-6** Move an existing message into Inbox from another folder and observe notifications/delta.

### C. Message fields (E-12..E-20)
- **E-13** `GET /users/$A/messages/{id}?$select=internetMessageId,internetMessageHeaders,conversationId,conversationIndex,from,sender,replyTo` for messages from Outlook, OWA, Gmail, mobile and an auto-reply: note missing/duplicated `Message-ID`, case, format.
- **E-14** Send via a mail gateway/forwarding rule if available; compare `internetMessageId` before/after.
- **E-15** Reply from the external account while changing the subject; compare `conversationId` and `In-Reply-To`.
- **E-16** CC both mailboxes A and B on one external mail; compare `conversationId` and `internetMessageId` between A's and B's copy (expected: same Message-ID, possibly different conversationId).
- **E-19/E-20** From the headers fetch confirm `In-Reply-To`/`References` presence for replies from each client; count replies lacking them.

### D. Reply flow, identity, Sent Items (E-12, E-17, E-21..E-29)
1. Draft: `POST /users/$A/messages/{I1}/createReply` with headers `Prefer: IdType="ImmutableId"`. Body none (or `{"comment":"..."}`). Expect 201 + draft message `isDraft:true`. Record `id` (D), `internetMessageId`, `conversationId`, `from`, `sender`. (**E-21**: `from.emailAddress.address` is the shared mailbox.)
2. Headers on the draft: `GET /users/$A/messages/{D}?$select=internetMessageHeaders,internetMessageId,from,sender,conversationId`. (**E-23**: `In-Reply-To`/`References` present? If absent, run **E-26**.)
3. Content: `PATCH /users/$A/messages/{D}` `{"body":{"contentType":"html","content":"<p>test</p><div style=\"display:none\">[hd:<random-token>]</div>"},"toRecipients":[{"emailAddress":{"address":"<requester>"}}]}`.
4. Token header (**E-28**): `PATCH /users/$A/messages/{D}` `{"internetMessageHeaders":[{"name":"x-helpdesk-token","value":"<random-token>"}]}`; if refused (headers are documented as set on creation only), create the draft with `POST /users/$A/messages` including the header and compare. Also try `singleValueExtendedProperties` (**E-29**): `{"singleValueExtendedProperties":[{"id":"String {00020329-0000-0000-C000-000000000046} Name HelpdeskTicket","value":"IT-42"}]}` (the GUID is the public-strings namespace; confirm in the extended-properties docs before use).
5. Send: `POST /users/$A/messages/{D}/send` -> expect `202`. 
6. Sent Items (**E-25**): poll `GET /users/$A/messages/{D}` (same immutable id) with a short back-off; record seconds until 200 and whether `parentFolderId` is SentItems, and whether `internetMessageId` equals the draft's (**E-12/E-24**).
7. Receive side: open the delivered message at the requester; compare the raw `Message-ID`, `In-Reply-To`, `References`, visible From, absence of "on behalf of" (**E-22**), and that the shared mailbox is the sender.
8. **E-17** Compare sent copy `conversationId` with the original.
9. **E-26 (R-04 fallback)** Create the reply draft in MIME form: `POST /users/$A/messages/{I1}/createReply` with `Content-Type: text/plain` and a base64 body of a MIME message containing explicit `In-Reply-To: <orig-id>` and `References: <orig-id>`; send; inspect delivered headers. Record whether the headers are preserved or rewritten.
10. **E-27** `POST /users/$A/sendMail` with `{"message":{...},"saveToSentItems":true|false}`: record Sent Items behaviour for both values; check the sendMail reference for the exact request shape.
11. Requester replies (various clients) -> go to section C/E and confirm correlation: Exchange-created `In-Reply-To` equals the recorded outbound `internetMessageId`.

### E. Subscriptions and webhooks (E-5, E-30..E-35)
- **E-30** `POST /subscriptions`:
  ```json
  {
    "changeType": "created,updated,deleted",
    "notificationUrl": "https://<host>/api/mail/notifications",
    "lifecycleNotificationUrl": "https://<host>/api/mail/lifecycle",
    "resource": "users/<A-object-id>/mailFolders('inbox')/messages",
    "expirationDateTime": "<now + 3 days, UTC>",
    "clientState": "<random 32+ chars, per subscription>"
  }
  ```
  with `Prefer: IdType="ImmutableId"`. Graph first POSTs `?validationToken=...`: respond `200 text/plain` with the decoded token within 10 s. Expect 201. Re-POST the same body: expect `409`. Try an expiry > 7 days (expect 400/clamped) and < 45 min (expect 45 min).
- **E-5/E-31/E-32** Send 100+ mails (script or a few bursts). Log per notification: receive time, `changeType`, `resourceData.id` (is it the immutable id?), clientState match. Count: duplicates, out-of-order created/updated, time from send to notification.
- **E-33** Renewal: `PATCH /subscriptions/{id}` `{"expirationDateTime":"..."}`. Observe `reauthorizationRequired` lifecycle events as expiry approaches; respond `202` then PATCH. Do not call `/reauthorize` and PATCH within 10 min.
- **E-34** Missed notifications: stop the endpoint for > 4 h (or return 5xx) and compare notifications received vs delta; confirm delta reconciliation recovers every message exactly once through the idempotency keys. Return 202 slowly (> 3 s) to observe the retry/throttling behaviour.
- **E-35** Keep a subscription alive 8+ days with a daily renewal job; confirm no gap (compare with delta).
- To test `subscriptionRemoved`: delete the subscription in Graph (or revoke the role assignment) and observe the lifecycle event, then recreate and reconcile.
- To list state: `GET /subscriptions`.

### F. Correlation and isolation end-to-end (E-36..E-38)
- **E-36** Requester writes to A, CC B. Agent replies from A (Reply flow). Requester replies-all. Expected: A copy attaches to the A ticket via `In-Reply-To`; B copy (referencing A's outbound id) does **not** attach: it creates a new ticket in B's department and logs a cross-department match. Re-run with the requester changing the subject, and with a forward by a third person. Threading policy check: a mail that carries only the `[KEY-n]` subject reference of an existing ticket (no `In-Reply-To`/`References`, no outbound token, new conversation), including one sent by a known participant, must **not** attach; it becomes a new ticket / manual-correlation item with the ticket recorded as a suggestion. The subject reference is a hint only.
- **E-37** Make a mailbox auto-forward/auto-reply to the other: confirm the own-address rule stops a loop and that `Auto-Submitted`/OOF replies are suppressed.
- **E-38** Collect real header samples (OOF, DSN/bounce, mailing list) and confirm the `MailLoopGuard` header rules in `tests/Helpdesk.GraphSpike.Tests` match them; add them as fixtures.

## 3. Results table (copy and fill)

| ID | Expected | Observed | OK? | Notes / action |
|---|---|---|---|---|
| E-1 | 200, no licence/sign-in | | | |
| E-2 | 403 outside scope | | | |
| E-3 | subscribe/draft/send work with scoped roles only | | | |
| E-4 | propagation delay (min) | | | |
| E-5 | latency avg < 1 min, max 3 min | | | |
| E-6 | created fires on move into Inbox | | | |
| E-7 | delta first round + incremental | | | |
| E-8 | delta token expiry behaviour | | | |
| E-9 | immutable ids in nextLink pages | | | |
| E-10 | id stable across folder moves | | | |
| E-11 | id differs without header | | | |
| E-12 | draft internetMessageId == delivered Message-ID | | | |
| E-13 | Message-ID absent/dup frequency | | | |
| E-14 | gateway case/format changes | | | |
| E-15 | subject change -> new conversationId? | | | |
| E-16 | conversationId per-mailbox | | | |
| E-17 | outbound keeps conversationId | | | |
| E-18 | closed-ticket policy (D-15) | | | |
| E-19 | headers present for replies per client | | | |
| E-20 | absence frequency | | | |
| E-21 | from = shared mailbox under app-only | | | |
| E-22 | no "on behalf of"; SendAs needed? | | | |
| E-23 | draft carries In-Reply-To/References | | | |
| E-24 | recorded id == recipient's Message-ID | | | |
| E-25 | Sent Items copy via draft immutable id; delay | | | |
| E-26 | MIME createReply headers preserved | | | |
| E-27 | sendMail saveToSentItems behaviour | | | |
| E-28 | x- header survives to Sent Items | | | |
| E-29 | extended property alternative | | | |
| E-30 | handshake, 201, 409 on duplicate, expiry limits | | | |
| E-31 | duplicates/order observed | | | |
| E-32 | created+updated pairs | | | |
| E-33 | lifecycle reauth + PATCH renew | | | |
| E-34 | delta recovers missed notifications | | | |
| E-35 | 8+ day renewal without gap | | | |
| E-36 | cross-mailbox reply stays isolated | | | |
| E-37 | loop stopped by own-address rule | | | |
| E-38 | real header samples match guard rules | | | |

Exit criteria: E-10, E-12, E-21..E-26 and E-36 observed and consistent with ADR-011 -> close the production validation gate of ADR-011 (status is already "Accepted as design direction"), mark R-04 mitigated (or re-rate), update EMAIL_ARCHITECTURE.md (move findings from [REAL] to validated, citing the run) and resolve D-16. Any contradiction -> amend ADR-011 before Phase 3 mail work starts.
