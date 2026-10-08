using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <summary>
    /// Stage 1 of removing Teams (a Department is the team). Data only, no schema change: nothing is dropped, so ticket.team_id, request_forms.default_team_id, the team tables and every
    /// historical audit row stay exactly as they are (and are simply no longer read or written).
    /// Configuration that NAMES a team cannot keep its meaning, so the migration REFUSES TO RUN, with a report, while any of it exists: automation rules (assign-team action, team
    /// condition, team-changed trigger), SLA policy team conditions, macros with a set-team action, request forms with a default team and saved views filtering by team.
    /// Only what has an exact equivalent is rewritten: the "My Team" queue (saved views and default queues) becomes "All" (a Department is the team), and the removed "team" column is
    /// dropped from column preferences.
    /// </summary>
    [DbContext(typeof(TicketingDbContext))]
    [Migration("20261009090000_RemoveTeamConcept")]
    internal partial class RemoveTeamConcept : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
DECLARE
    report text;
BEGIN
    SELECT string_agg(line, E'\n' ORDER BY line) INTO report FROM (
        SELECT format('automation rule %s (department %s) uses a team', id, department_id) AS line FROM ticketing.automation_rules
            WHERE trigger = 'TeamChanged' OR actions::text ~ '""type""\s*:\s*""assignTeam""' OR conditions::text ~ '""field""\s*:\s*""team""'
        UNION ALL
        SELECT format('SLA policy %s (department %s) has a team condition', id, department_id) FROM ticketing.sla_policies
            WHERE conditions::text ~ '""field""\s*:\s*""team""'
        UNION ALL
        SELECT format('macro %s (department %s) has a set-team action', id, department_id) FROM ticketing.macros
            WHERE actions::text ~ '""type""\s*:\s*""setTeam""'
        UNION ALL
        SELECT format('request form %s (department %s) has a default team', id, department_id) FROM ticketing.request_forms
            WHERE default_team_id IS NOT NULL
        UNION ALL
        SELECT format('saved view %s filters by team', id) FROM ticketing.saved_views
            WHERE filter ->> 'teamId' IS NOT NULL OR filter ->> 'noTeam' = 'true'
    ) found;
    IF report IS NOT NULL THEN
        RAISE EXCEPTION E'Teams are being removed (a Department is the team) but configuration still refers to a team. Resolve each item first, then run the migration again:\n%', report;
    END IF;
END $$;");

            migrationBuilder.Sql("UPDATE ticketing.saved_views SET filter = jsonb_set(filter, '{queue}', '\"all\"') WHERE filter ->> 'queue' = 'team'");
            migrationBuilder.Sql("UPDATE ticketing.user_queue_preferences SET default_queue = 'all' WHERE default_queue = 'team'");
            migrationBuilder.Sql("UPDATE ticketing.user_queue_preferences SET columns = columns - 'team' WHERE columns IS NOT NULL AND columns ? 'team'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: no schema changed, and the queue/column rewrites have no recorded original ('All' stays valid).
        }
    }
}
