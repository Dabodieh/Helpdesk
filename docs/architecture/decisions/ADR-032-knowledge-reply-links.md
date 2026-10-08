# ADR-032: Knowledge reply-link contract

Status: Accepted (recorded retroactively in WS-16; precondition made explicit in the same workstream; finding ARC-006, ARC-008)

## Decision
`IReplyLinkArticles.SearchAsync(departmentId, linkedUserId, query)` (Knowledge, public contract) returns the published articles a department's linked, authenticated requester could open in the portal, for the public-reply link picker. Only the published title, category and portal path are returned (no summary or body); drafts, archived articles and other departments' articles never. Ticketing, the caller, establishes the precondition (the ticket's requester is linked to an active account of that department) and passes that account's id; an empty id yields nothing. The reply then carries a link to the portal article, never the article text.

## Consequences
- A future caller must supply the linked user to call the contract, so the precondition is visible in the signature rather than only in prose.
