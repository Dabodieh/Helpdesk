# ADR-038: Knowledge reads user summaries through Identity.Contracts

Status: accepted (records a dependency that has existed since 2026-10-07, commit `081f660`)

## Context

Knowledge article maintenance (ADR-032 reply links, article owner and review date) shows the owner's display name in the administration view. Owners must be active users who hold `department.knowledge.manage` in the article's Department. The Knowledge module has no user table and must not read Identity's.

The architecture tests expected Knowledge to reference only Audit and Organisation. `Project_references_follow_the_module_dag` failed once the Knowledge project began using `Helpdesk.Modules.Identity.Contracts`.

## Decision

- Knowledge **may** reference the Identity project, and only the `Helpdesk.Modules.Identity.Contracts` namespace. The single use today is `IUserDirectory.GetManyAsync` in `KnowledgeAdminService`, to resolve display names and active state for eligible owners.
- Eligibility stays with Organisation (`GetUserIdsWithPermissionAsync`, Department-scoped). Identity supplies names only, never permissions.
- This is the same contract-only pattern already used by Organisation and Ticketing (`IUserDirectory`). Dependency direction remains acyclic: Identity references Audit only.
- No Identity internals, tables, `DbContext` or persistence types may be used. `Module_does_not_depend_on_another_modules_internals` continues to enforce this.
- Any further Identity.Contracts member Knowledge starts using is a review point; new Identity dependencies for other modules need their own ADR.

## Consequences

- `Project_references_follow_the_module_dag` expects Knowledge -> Audit, Identity, Organisation.
- The users shown are display names of Department staff the caller is already authorised to administer; no cross-Department user data is read, because the requested ids come from the article's own Department eligibility set.
- Alternative considered: Organisation returning owner names. Rejected: it would duplicate user summary data and push a user-directory concern into Organisation for one consumer.
