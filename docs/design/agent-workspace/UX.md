# Agent workspace: UX notes (Phase 2)

Principle: a long-lived service desk used all day. The workspace (queue + ticket) is the product; administration is a secondary area. Dense, calm, keyboard-first, minimal chrome. Original design, no product copied.

## Visual language
- Plain CSS with tokens in `index.css` (`--bg`, `--surface`, `--surface-2`, `--border`, `--text`, `--muted`, `--accent`, status/priority/internal tokens). Light by default, dark via `prefers-color-scheme`. Base font 14px system UI, line height 1.4; tabular numerals for references and ages; monospace only for references.
- Density: 28px controls, 32px queue rows, 1px hairlines instead of cards, 4px grid. Radius 4px. No shadows except popovers/dialogs.
- Focus: 2px accent ring with offset on every interactive element, never removed. Motion limited to 120ms colour transitions; disabled under `prefers-reduced-motion`.
- Meaning is never colour alone: status has dot + text, priority has bar glyph + text, public/internal differ by label, icon, border style and background.

## Navigation and shell
Narrow (184px) left rail: My Queue, Team Queue, Unassigned, All Tickets, Search (open-ticket count badges from `queue-counts`), "New ticket" button on top, Administration and the user menu (sign out) at the bottom. Administration only when `/api/me` shows an administrative permission. Under 900px the rail becomes a top bar.

## Split workspace
Queue pane left (min 360px, 40%), ticket pane right. URL is the state: `/queue/:queue/:ticket`, `/tickets/:ticket` (guid or reference, deep link works on reload). Under 900px list and ticket are separate views; the ticket view has a Back to queue control. Selection survives list refresh because it is the URL, not row state; the keyboard cursor is tracked by ticket id.

## Queue
Toolbar: search box, status chips (default Open + Pending), priority, department, team, assignee, sort. Columns: reference, subject, requester, status, priority, team, assignee, updated (relative age, absolute in the title). In a narrow pane team/assignee/requester collapse into a second line under the subject (container query). Keyset pagination with "Load more" (auto-loads near the bottom). Polls every 30s and on focus.

## Ticket header and metadata
Line 1: reference, department, editable subject (Edit button, Enter saves, Esc cancels). Line 2: status, priority, team, assignee as compact labelled selects (hidden or disabled by permission, UX only). Line 3: requester (name, email, open/total counts), created, updated, resolved/closed times.

## Conversation, composer, history
Ascending chronology, newest-first fetch with "Load earlier messages". Each entry: icon, kind label ("Public reply" / "Internal note"), author, author type, time, plain text body with preserved line breaks. Internal notes: lock icon, dashed border, hatched tinted background, label "Internal note, not visible to the requester". Agent public replies show "Recorded, not emailed (email delivery is not enabled yet)". Composer has an explicit Public reply / Internal note radio toggle; internal mode shows a banner and a differently styled box; optional "Set status after". Draft survives conflicts and ticket switches. History is a tab with human sentences ("Alex changed status from Open to Pending"); message events are omitted because the conversation shows them.

## Keyboard map
`j`/`k` or arrows move the queue cursor, `Enter` opens, `r` public reply, `n` internal note, `/` search, `c` new ticket, `?` help, `Esc` leaves composer / closes dialog, `Ctrl+Enter` posts from the composer. Suppressed while typing or with Ctrl/Alt/Meta. No destructive shortcuts.

## States
Empty queue: one line plus hint to change filters. Empty workspace pane: "Select a ticket" with shortcut hints. Loading: skeleton rows with fixed height (no layout shift) and `role=status`. Errors: inline alert with Retry; 404 never distinguishes missing from forbidden. Conflict: banner "Someone else changed this ticket" with Reload, draft kept.
