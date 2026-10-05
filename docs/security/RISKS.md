# Risk Register

| ID | Risk | Impact | Mitigation | Owner |
|---|---|---|---|---|
| R-01 | Cross-department data leakage via missed authorization check | Critical | Single `IAuthorizer`, scoped query filters, 404 policy, isolation matrix tests, optional RLS (D-04) | QA/Sec |
| R-02 | Duplicate/lost inbound mail (webhook duplicates, expiry, outages) | High | Delta query as source of truth, unique provider id constraint, reconciliation job, failed-mail visibility | Messaging |
| R-03 | Graph subscription expiry / throttling (429) | High | Renewal + lifecycle handling, backoff honouring Retry-After, per-mailbox concurrency limit | Messaging |
| R-04 | Graph cannot set `In-Reply-To`/`References` on outbound; broken threading | High | createReply drafts, X-headers + guarded subject token; prove in Phase 3 spike before building on it | Messaging |
| R-05 | Ticket hijack/spoofing through subject token or forged sender | High | Token honoured only for known participants; SPF/DKIM/DMARC results (Authentication-Results) considered; no auto-privilege from email | Messaging/QA |
| R-06 | Malicious HTML/attachments | High | Sanitisation, sandboxed rendering, CSP, validation, AV hook | Backend/QA |
| R-07 | Graph credential compromise reaching all mailboxes | Critical | Application access policy scoped to department mailboxes, cert credentials, rotation | Messaging |
| R-08 | Privilege escalation through membership/role management | High | Escalation rules in PERMISSIONS.md + tests | Backend/QA |
| R-09 | PostgreSQL FTS insufficient for scale/quality | Medium | Defer; measure; abstract behind Search module | Backend |
| R-10 | Hangfire + in-process jobs impair web latency | Medium | `ROLE=worker` split, same image; revisit on evidence | Principal |
| R-11 | Small team; scope creep | Medium | Backlog discipline, vertical slices, ADRs | Principal |
| R-12 | .NET 10 / PostgreSQL 18 / Npgsql / Hangfire version compatibility surprises | Low | Pin versions, verify in Phase 0 CI | Backend |
