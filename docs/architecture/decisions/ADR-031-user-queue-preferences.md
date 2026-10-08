# ADR-031: Per-user queue preferences

Status: Accepted (recorded retroactively in WS-16; finding ARC-006)

## Decision
`ticketing.user_queue_preferences` holds one row per account: the optional queue columns and their order and the default queue or saved view. It contains no department data and is read and written only for the signed-in user. Unknown or stale values fall back safely when read (an unavailable saved view, an unsupported column). The first save is an upsert (a concurrent first save is redone as an update).

## Consequences
- Preferences follow the person across departments and devices; they never affect what a ticket query is allowed to return (authorization stays server-side).
