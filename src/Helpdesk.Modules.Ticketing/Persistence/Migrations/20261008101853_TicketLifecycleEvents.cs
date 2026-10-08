using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketLifecycleEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_lifecycle_events",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    from_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    to_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_lifecycle_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_lifecycle_department_time",
                schema: "ticketing",
                table: "ticket_lifecycle_events",
                columns: new[] { "department_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_lifecycle_ticket_time",
                schema: "ticketing",
                table: "ticket_lifecycle_events",
                columns: new[] { "ticket_id", "occurred_at" });

            // Existing history: the same content-free facts, taken from the audit stream (ids and enumerations only; no actor, subject or address is copied).
            migrationBuilder.Sql(
                """
                INSERT INTO ticketing.ticket_lifecycle_events (id, ticket_id, department_id, kind, from_value, to_value, occurred_at)
                SELECT gen_random_uuid(), e.object_id, e.department_id,
                       CASE e.action WHEN 'ticket.created' THEN 'created' WHEN 'ticket.status_changed' THEN 'status' WHEN 'ticket.priority_changed' THEN 'priority'
                                     WHEN 'ticket.team_changed' THEN 'team' WHEN 'ticket.assignee_changed' THEN 'assignee' WHEN 'ticket.message_added' THEN 'message'
                                     WHEN 'ticket.merged_into' THEN 'merged' END,
                       CASE e.action WHEN 'ticket.status_changed' THEN e.previous->>'status' WHEN 'ticket.priority_changed' THEN e.previous->>'priority'
                                     WHEN 'ticket.team_changed' THEN e.previous->>'teamId' WHEN 'ticket.assignee_changed' THEN e.previous->>'assigneeUserId' END,
                       CASE e.action WHEN 'ticket.created' THEN COALESCE(e.next->>'source', 'agent') WHEN 'ticket.status_changed' THEN e.next->>'status'
                                     WHEN 'ticket.priority_changed' THEN e.next->>'priority' WHEN 'ticket.team_changed' THEN e.next->>'teamId'
                                     WHEN 'ticket.assignee_changed' THEN e.next->>'assigneeUserId'
                                     WHEN 'ticket.message_added' THEN (e.next->>'kind') || ':' || (e.next->>'authorType') END,
                       e.occurred_at
                FROM audit.audit_events e
                WHERE e.category = 'ticketing' AND e.object_type = 'ticket' AND e.department_id IS NOT NULL
                  AND e.action IN ('ticket.created', 'ticket.status_changed', 'ticket.priority_changed', 'ticket.team_changed', 'ticket.assignee_changed', 'ticket.message_added', 'ticket.merged_into');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_lifecycle_events",
                schema: "ticketing");
        }
    }
}
