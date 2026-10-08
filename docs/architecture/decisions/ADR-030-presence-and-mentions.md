# ADR-030: Presence hints and @mentions

Status: Accepted (recorded retroactively in WS-16; project collaboration constraints in `CLAUDE.md`; finding ARC-006)

## Decision
- **Mentions.** Internal notes only, individuals only, at most 10 per note. A mentionable colleague is an active user who, by department membership, holds `tickets.read` and `tickets.notes.read` in the ticket's department (the picker lists only those). A mention creates a durable notification through the outbox (ADR-028); it never follows, assigns or grants access.
- **Presence.** Advisory only: the hub's single client-callable method sets a state `viewing`, `reply` or `note` for a ticket the caller may read. Entries are ephemeral (one per connection, expire after 60 seconds without a heartbeat), carry display name and state, and are sent only to people who may read that ticket. No keystrokes, no draft content, no history, no duration reporting, no employee monitoring. It does not replace the concurrency controls (row versions).

## Consequences
- Presence data is never persisted or reported. A draft shows a gentle "new activity" warning but is never blocked.
