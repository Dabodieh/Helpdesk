# Helpdesk Graphite

An independent, browser-ready design study. Created 8 October 2026. Awaiting visual approval; not approved for production integration.

Open `F:\Software Development\Helpdesk\docs\design\redesign\codex-prototype\index.html` directly in Chrome or Edge. No server, installation, internet connection or build is required. All runtime assets are local.

## Review the concept

| Screen | Interactive page | Light capture | Dark capture |
|---|---|---|---|
| Main Administration | [administration.html](administration.html) | [1440 × 900](screenshots/administration-light-1440x900.png) | [1440 × 900](screenshots/administration-dark-1440x900.png) |
| Department overview | [department.html](department.html) | [1440 × 900](screenshots/department-light-1440x900.png) | [1440 × 900](screenshots/department-dark-1440x900.png) |
| Mailbox management | [mailboxes.html](mailboxes.html) | [1440 × 900](screenshots/mailboxes-light-1440x900.png) | [1440 × 900](screenshots/mailboxes-dark-1440x900.png) |
| Agent workspace | [workspace.html](workspace.html) | [1440 × 900](screenshots/workspace-light-1440x900.png) | [1440 × 900](screenshots/workspace-dark-1440x900.png) |

[The gallery](gallery.html) includes all 16 screen/theme/viewport combinations and two additional mailbox drawer captures. Filter by theme and viewport; click any image for its original resolution. The landing page uses real screenshots of these screens, not fabricated product previews. Its theme toggle is also a visual comparison control.

Suggested walkthrough: Administration → IT Services → Mailboxes → Add mailbox → Agent workspace. Try the theme switch, Ctrl+K, mailbox validation, ticket selection, queue search, and the Public reply/Internal Note toggle.

## Product identity

**Graphite** uses a warm mineral neutral base and a vermilion accent. The restrained, dark application rail anchors both themes. The interior surfaces change together; dark mode uses deliberately tuned charcoal-green neutrals rather than an inverted light palette. Accent colour is reserved for selection and primary actions. Semantic green, amber and red have separate roles for success, attention and failure.

Manrope gives the product a precise, slightly human character. Primary navigation, messages, table identities and property values use approximately 14px type. Headers sit at 22–29px. Supporting timestamps, IDs and annotations are smaller, so operational text has a clear hierarchy. A small three-bar mark gives Helpdesk a recognisable identity without decorative illustration.

The design spends its space on work. Quiet separators, a consistent small-radius scale and aligned controls carry most of the visual structure. There are no decorative charts, invented performance metrics, gradients or large dashboard tiles.

## What changes from the production interface

The production frontend was inspected for function and constraints: AppShell, DepartmentPage, DepartmentOverview, MailboxesSection, WorkspacePage, Composer and index.css, together with the architecture/MVP and project guidance. The existing production visual baseline uses a system font, blue accent, dense 28px controls and a general-purpose navigation rail.

Graphite introduces:

- A 72px application rail plus contextual navigation. Global destinations, Department configuration and queue navigation occupy distinct, predictable places.
- An organisation table focused on identity and access, with sign-in, timezone and domain information above it. The sample actor is a platform administrator with explicit IT Services membership; other Departments are marked metadata-only.
- A Department overview that prioritises service readiness. Service delivery occupies the main area; people and self service are compact related groups. A restrained context column explains the Department's defaults.
- A mailbox table that separates identity, health, subscription and sync. Attention messages have their own review area. Editing happens in a contextual drawer with a live sender preview, clear fields, an explicit default setting and deactivation consequences.
- A five-region ticket workspace: global rail, queues, ticket list, conversation and properties. The conversation remains the widest region. Public messages are quiet; Internal Notes are unmistakably tinted and labelled. The composer stays available while the conversation scrolls.
- At laptop height, the initial requester message is collapsed in a native disclosure so the Internal Note and latest public reply remain visible. It can be expanded by mouse or keyboard. At desktop height it starts expanded. This is a visual concept proposal, not a change to production behaviour.

No HTML, CSS, navigation or component structure from the prior Claude prototype was read or copied. Its directory was not modified. This comparison is against the inspected production source, not a claimed assessment of the prior prototype.

## Why this could compete commercially

The proposed value is a coherent working experience: stable navigation, readable message hierarchy, explicit service context, predictable editing patterns and practical failure states. These details reduce orientation cost and make complex settings easier to understand. The identity remains recognisable across administration and ticket work rather than changing visual language between modules.

Commercial competitiveness is a design intent, not a usability-study result or a production-readiness claim. No claims are made about outperforming another vendor. The concept is deliberately independent of competitors' layouts.

## Extending the system

| Remaining surface | Reuse |
|---|---|
| Members / Teams | Department shell, searchable table, role badges, explicit edit drawer |
| SLA / Business hours | Readiness rows, labelled forms, target summaries, native dialogs |
| Automation | Ordered table, grouped rule form, distinct dry-run and history panels |
| Request forms | Same fields/drawer patterns, explicit audience summary, restricted initial state |
| Knowledge | List/detail layout, publication states, scoped audience information |
| Macros / canned responses | Searchable list, draft preview, clear explicit apply action |
| Requester portal | Shared type, colour, form and status tokens; simpler requester navigation; no internal controls |
| Operational dashboard | Attention rows and actionable work lists, using the established Department scope |
| Historical reporting | Shared typography/table/filter language in a separate reporting surface; no agent rankings |

Shared design tokens live in `styles.css`; shared shell, fixture data and local interactions live in `app.js`. Each screen has its own HTML entry point. This is a static prototype, not a proposed production component architecture.

## Interaction boundaries

Working demonstrations:

- Screen navigation and selected states; keyboard jump-to search.
- Theme persistence (localStorage when available).
- Department filtering and an empty state with recovery.
- Native mailbox drawer, keyboard focus containment, Escape/cancel, sender preview, required/email/duplicate validation, add/edit/default controls and an inactive-mailbox warning.
- Ticket/queue selection, sorting and search; property selection; resolve/reopen/follow.
- Separate local drafts per ticket and per public/Internal Note mode; local demo messages and status after adding a message.

Mailbox edits and ticket changes are kept in memory on the current page and reset on reload. Theme is the only persisted setting. New sample mailboxes are marked unverified/unconnected; saving never pretends to establish a Graph connection. No draft, message or form data is sent anywhere.

Other navigation entries open explicitly labelled sample previews. Attachment upload, rich-text tools, invitation flows, actual administration changes and mail-service checks are outside these four completed screens. The prototype contains no fetch/XHR, production API or external runtime asset requests.

Product constraints retained in the visual proposal: Department is the hard security boundary; Teams route work; Platform Admin is not ticket-content access; mailbox ownership fixes From/Reply-To; default changes affect new tickets only; uncertain sends do not offer blind retry; email is not proof of requester ownership; Internal Notes are for authorised staff only. These are explanatory UI states, not an authorization implementation.

No database/model, API, schema/migration, authentication, permission, privacy, audit, production route or production behaviour was changed. No deployment, commit or integration was performed. D-16 and all other production gates remain unchanged.

## Verification evidence

- Direct `file://` opening verified in local headless Chrome through the existing Playwright installation.
- Captured and visually inspected four screens at 1440 × 900 and 1366 × 768 in light and dark: 16 combinations, plus two mailbox drawer captures.
- No horizontal document/content overflow, missing font or JavaScript page errors in the 16 capture states. Long settings pages and secondary rails intentionally scroll vertically.
- Focused interaction checks passed: filtering, navigation, theme persistence, keyboard search, add/edit validation, focus restoration, fixed mailbox address, unique default, deactivation warning, uncertain-send review, ticket selection, isolated drafts, local messages, property controls, resolve/reopen and queue recovery.
- The existing axe-core installation reported zero WCAG 2/2.1 A/AA violations across the eight screen/theme combinations after the composer tab grouping was corrected. This is a targeted automated check, not a full accessibility certification.
- No external HTTP requests were observed in the interaction run.
- No full test suite, production build, backend test or live-tenant validation was run.

Machine-readable evidence: `visual-review.json`, `interaction-review.json`. Reproduction tools: `capture.cjs`, `verify.cjs`; they use the repository's already-installed Playwright/axe-core and an installed Chrome. They are not needed to open the prototype. The Browser plugin was attempted first, but startup referenced a missing browser-service module; the existing local tooling provided the fallback without installing packages.

The visual pass corrected wrapped settings labels, excess vertical density, an awkwardly clipped conversation and the composer accessibility grouping. Main table copy distinguishes metadata-only access, and sender states remain honest about unconnected sample mailboxes.

## Assets and research

No version-sensitive framework or security implementation was changed. Research was limited to confirming the sources/licences for local visual assets:

- [Manrope upstream](https://github.com/google/fonts/tree/main/ofl/manrope), distributed under the [SIL Open Font License](https://raw.githubusercontent.com/google/fonts/main/ofl/manrope/OFL.txt). Font and licence are in `assets/`.
- [Tabler Icons upstream](https://github.com/tabler/tabler-icons), distributed under the [MIT licence](https://raw.githubusercontent.com/tabler/tabler-icons/main/LICENSE). A selected set of outline SVG paths is bundled in `assets/icons.js`, with its licence alongside.

These are local assets, not new application dependencies. No production package or lockfile was changed.

## Local changelog

2026-10-08: Created Graphite as an independent five-entry-page static concept, added shared themes/shell and local interactions, completed targeted visual/accessibility checks, and recorded screenshots. Added a filterable screenshot gallery. Production roadmap and delivery status remain unchanged.

Next step: user visual approval or specific visual feedback. Do not begin integration from this artifact.
