# ADR-022: Abuse controls and the interim attachment policy

Status: Accepted (post-Refinement-10 remediation, WS-8; decisions D11 and D13; findings SEC-003, MSG-005, SEC-002)

## Decision
1. **Per-user rate limits** with the built-in ASP.NET Core limiter (no dependency): portal request creation, portal replies, and ticket search (counted only when a search term is present), fixed window per signed-in user, `429` with `Retry-After` and problem code `rate_limited`. Defaults (`RateLimits` section) are PROVISIONAL starting values (10 creations and 30 replies per 10 minutes, 60 searches per minute) to be tuned to real traffic.
2. **Body size.** The default request body limit is 2 MiB (`AbuseControls.DefaultMaxRequestBodyBytes`); the portal reply (the only upload endpoint) sets an explicit 29 MiB (one 25 MiB attachment plus the rest), and the Graph webhook keeps its own 256 KiB.
3. **Acknowledgements (D11).** `IMailOutbox.QueueAutoReplyAsync` returns null when the mailbox has already sent `Mail:AcknowledgementLimitPerWindow` (default 60, provisional) acknowledgements in `AcknowledgementWindowMinutes` (10). The ticket is still created and the skipped acknowledgement is audited (`ticket.auto_reply_failed`, reason `mailbox_limit`). The default template does not contain `{{ticket.subject}}` (verified; no change needed). SPF/DKIM/DMARC is not used (depends on D-16 evidence).
4. **Attachments (D13, interim).** Until a scanner exists the blocked list also covers macro-enabled Office files and add-ins, disk and container images, HTML/SVG, shortcuts and package/launcher types. Production refuses to start unless `Attachments:RequireCleanScan` is true (ADR-020); with no scanner every stored attachment therefore stays "awaiting malware scan" and is not downloadable, which the UI says. Once a scanner exists (D-08, engine not chosen) clean macro-enabled documents may be allowed.

## Consequences
- Choosing and integrating the scanner engine is still open and blocks release (D-08).
