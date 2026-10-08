# ADR-034: Departments are the workgroups (Teams removed)

Status: accepted (stage 1 implemented; stage 2 pending a separate request)

## Context

A Department already is a service team (IT Support, HR, Finance, Facilities). A Team inside a Department duplicated it: tickets carried both a department and a team, routing and queues needed a team choice, and administrators had two organisational layers to understand and maintain. Teams were never a security boundary (Department is the hard boundary), so removing them does not touch isolation.

## Decision

The model is **Departments + Members + Tickets**.

- A ticket belongs directly to a Department and is assigned to one Department member or stays unassigned. The Department's unassigned queue is every unassigned ticket of that Department.
- A user may belong to several Departments. Mailboxes, SLAs, automation, business hours, permissions and configuration stay per Department. Department isolation is unchanged.
- Removed from active behaviour: team administration and endpoints, team assignment, the Team queue and filter, team conditions and actions in automation, SLA policy and macros, a request form's default team, team workload reporting.
- Permissions: `teams.manage` and `tickets.assign.team` are retired. Every other permission and role mapping is unchanged. The `TeamLead` role keeps its stored code, name and permissions (`assign.any`, department audit); renaming it is cosmetic and was not required. An Agent can still take and release their own tickets; choosing another person needs `tickets.assign.any`.
- Reports: the operational dashboard lists current backlog per assignee (alphabetical, unassigned last: a snapshot of where work sits, never a ranking or score). The historical volume report is counts only.

## Data and rollout (two stages)

Stage 1 (this change) removes every read and write of teams and changes **no schema**: `organisation.teams`, `organisation.team_memberships`, `ticketing.tickets.team_id`, `ticketing.request_forms.default_team_id` and every historical audit and lifecycle row remain exactly as they were. Existing tickets keep their assignee; their old team value is simply no longer used. History written before the change (`ticket.team_changed`, `team.*`, `team_membership.*`) still reads.

The data migration `RemoveTeamConcept` (Ticketing) runs once and:

- **refuses to run**, with a report listing each item, while configuration still names a team: automation rules (assign-team action, team condition, team-changed trigger), SLA policy team conditions, macros with a set-team action, request forms with a default team, saved views filtering by team. These cannot keep their meaning, so an administrator resolves them first. Nothing is rewritten silently.
- rewrites the "My Team" queue to "All" in saved views and default queues (a Department is the team, so the set is the same), and removes the retired Team column from saved column choices.

Stage 2 (only when requested, after the data has been verified) drops the four team tables/columns above in a separate migration.

## Consequences

- API contracts lose `team`, `teamId`, `noTeam`, the `team` queue and `queue-counts.team`; the dashboard `teams` list becomes `agents`; the volume report loses `teams`.
- Rollback of stage 1 is a revert of the code; the data is untouched apart from the queue rewrite (an exact equivalent) and the removed column choice.
