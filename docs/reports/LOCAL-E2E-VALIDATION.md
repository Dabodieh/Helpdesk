# Local E2E validation — 2026-10-13

**Final result: 13 passed, 0 failed, 9 skipped (22 tests), one full run after the fixes.** The 9 skips are the opt-in capture specs (`VISUAL=1`, `SCREENSHOTS=1`); they were left as they are. First run, before any fix: 1 passed, 12 failed, 9 skipped.

## Environment (reusable: `scripts/e2e/e2e.ps1 up | run | status | down [-DropData]`)

| Part | Configuration |
|---|---|
| Database | Container `helpdesk-e2e-postgres` (postgres:18.6), own volume `helpdesk_e2e_pgdata`, `127.0.0.1:5434`, database `helpdesk_e2e`. Throwaway password generated once into `%LOCALAPPDATA%\Helpdesk-E2E\pg.env` (outside the repo). |
| Backend | `dotnet run` of the committed source, `127.0.0.1:5081`, Development, dev sign-in, `Mail:Provider=Fake`, `Storage:Provider=InMemory`; migrations applied to `helpdesk_e2e` only. |
| Frontend | Vite `127.0.0.1:5174`, proxy target from env `HELPDESK_API_PROXY` (default unchanged, `http://localhost:5080`). |
| Playwright | `E2E_BASE_URL=http://127.0.0.1:5174`, `E2E_BROWSER_CHANNEL=msedge`. |

Isolation was verified before the first run: loopback-only listeners, backend connection string from the environment (45 tables migrated into `helpdesk_e2e`), in-memory storage and fake mail (no Microsoft 365 or real mail), `hd-host` and the dev PostgreSQL untouched, `helpdesk` database and `helpdesk_pgdata` untouched, Cloudflare stopped, Playwright targeting 5174 only.

## Root causes and fixes (specs only; no application behaviour changed)

| Area | Root cause | Verdict | Change |
|---|---|---|---|
| Landing and ticket URL (`agent-workflow`) | Agents land on the Dashboard (`11f0f6b`); My Queue is under Tickets; ticket page is `/tickets/<ref>` with `[` / `]` for neighbours. | Outdated test | Assertions follow the current navigation and URLs. |
| Properties rail (agent-workflow, productivity, service) | Rail is docked open only from 1400 px wide; the default 1280 px viewport hides it behind the Properties button. Responsive behaviour is intentional. | Outdated test | `test.use({ viewport: 1440x900 })` in those specs. |
| Email loop mailbox text | Mailbox sender-identity row is shown only to users with `department.mailboxes.manage` (`TicketMailbox`). | Outdated test | Asserts a plain agent does not see the address (non-disclosure kept). |
| Email loop signature | Signatures are per agent and per Department; mailboxes carry none (project rule). | Outdated test | Agent sets own signature via `/signature/me`; mailbox created without one. |
| Portal specs (3 tests) | (a) Portal serves forms only for a Department with an active default mailbox (PR10-D22) — seed had none. (b) Tickets an agent creates for an email address are not owned by whoever later signs in with it; an admin must link the requester (email is not proof of ownership). | Test setup / outdated test | `configure()` registers a mailbox; new `linkRequester()` uses `requester-identities/{id}/link`. |
| Ticket details | "Details" is now a region (`aria-label`), not a `<summary>`. | Outdated test | Selector updated. |
| Administration tabs | "Ticketing" tab is now "Ticket settings"; "Knowledge" also matches "Acknowledgements". | Outdated test | Names updated, `exact: true`. |
| Strict-mode text matches | Row edit buttons repeat the name in an `sr-only` span ("Edit Network routing"), so `getByText` matches twice. Intentional accessible naming. | Outdated test | Locators target the visible `strong`/anchored cell names. |
| Queue filters | Priority and Service level filters sit inside a collapsed "Filters" details group; "Views" exists only on the queue, not the ticket page. | Outdated test | Open the group; check Views before opening a ticket. |
| SLA | Service level moved from the ticket header strip to the properties rail (`SlaPanel`, "SLA details"). | Outdated test | Selectors updated. |
| Phone overflow (2 px on portal home) | Failed once in the first run (Department without a mailbox); **did not reproduce** after the mailbox fix or in the full run. | Not reproduced | No change. Possibly latent in the "no form available" state; untested. |

No genuine application defect was confirmed. No test was skipped, deleted or weakened; the one inverted assertion (mailbox address) now guards the intended non-disclosure.

## Observations

- `SlaStrip` in `src/web/src/components/Sla.tsx` is exported but no longer rendered (replaced by `SlaPanel`): dead code, not removed.
- The agent-workflow test still asserts "Recorded, not emailed (mailbox not available)" for a mailbox-less seed ticket, consistent with the locked "no fallback mailbox" rule.

## Not covered by E2E

- Historical reporting/analytics, Microsoft 365 administration page, invitations/People page, Entra setup.
- Browser-level Internal Note leakage (covered by backend tests only).
- The 9 opt-in visual/screenshot specs produce captures only and were not run; no baselines.
- Phone layout is checked on the portal only (and only with a mailbox configured).

## Files changed (uncommitted)

`src/web/vite.config.ts` (proxy env override), `scripts/e2e/e2e.ps1`, `src/web/e2e/agent-workflow.spec.ts`, `email-loop.spec.ts`, `portal.spec.ts`, `productivity.spec.ts`, `service.spec.ts`, this report.

The isolated stack is left running on loopback; stop with `scripts\e2e\e2e.ps1 down`.
