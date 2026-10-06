# Phase 6 threat model: requester portal and knowledge

Scope: `/api/portal/*`, request forms, knowledge module, requester realtime. See ADR-015.

| Threat | Control | Test |
|---|---|---|
| Requester reads another requester's ticket | Every portal query scoped by the caller's `requester_links`; missing or foreign object returns 404 with no detail | PortalTests (cross-requester), Playwright security flow |
| Impostor claims an address | Link only from verified Entra email, only while the requester is unclaimed; later access by link id; second user gets 409 on submit and an empty list | PortalTests |
| Internal note or agent data leaks to requester | Separate DTOs; internal messages excluded in the query; `last_public_activity_at` so notes do not move timestamps, ordering or counts; internal attachments return 404 | PortalTests (note, timestamp, attachment) |
| Participant/CC reads ticket | Primary requester only in v1; no portal path for other participants | PortalTests |
| Forged protected values on submit | Department, status, requester, source, SLA, team derived on the server; unknown/hidden fields rejected | FormsTests |
| Form edit rewrites history | Snapshot per ticket value; field key/type immutable; deactivate not delete | FormsTests |
| Unsafe attachment from portal | Same pipeline as agents; `RequireCleanScan` unchanged | PortalTests |
| Stored XSS via Markdown | Own renderer, no raw HTML, http(s)/mailto/in-app links only | markdown.test.tsx, Playwright knowledge flow |
| Draft/archived knowledge exposure | Search and read restricted to Published plus audience; FTS vector built from published fields only | KnowledgeTests, Playwright |
| Realtime leakage | Requester group `r:{id}` derived from links; payload is only reference, kind, timestamp | PortalTests (realtime) |
| Search injection | tsquery built from sanitised alphanumeric tokens; cursor length-capped | KnowledgeTests |
| Cross-department admin access | `department.forms.manage` / `department.knowledge.manage` plus department filter; 404 otherwise | FormsTests, KnowledgeTests, architecture tests |

Residual: abuse/rate limiting of portal submit and search (D-16 gate), requester link retention (D-09), malware scanner dependency (D-13).
