# ADR-029: Requester identity links and request-form audiences

Status: Accepted (recorded retroactively in WS-16; amends ADR-015 which was edited inline; decisions D15 and D22; finding ARC-006)

## Decision
1. The authenticated account (immutable Entra identity bound to an internal user) is the stable owner. A `requester_links` row says "this department-local requester is this user". Email is mutable contact and routing data, never proof of ownership.
2. A link is created only by the user's own portal submission for a requester row that submission creates, or by an administrator's explicit audited decision (`requester-identities`). An existing requester row for the same address is never taken over by the address alone, with history or without. Email-based historical claiming does not exist.
3. Request forms have an audience: `AllEmployees` or `Selected` (explicit users). A new form starts restricted to selected people with nobody selected. The audience is applied on the server for the catalogue, metadata, fields and options, validation, direct access and submission; anything outside it behaves as if the form did not exist. Being able to submit a form grants no department access. Help topics never bypass the form's audience.
4. A form is available only while its department has a usable default mailbox (ADR-026).
5. Portal realtime access derives from the links.

## Consequences
- A person whose address already has an unlinked requester row needs an administrator's link before the portal works for them.
