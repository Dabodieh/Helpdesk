using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Audit.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class TicketingSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_category",
                schema: "audit",
                table: "audit_events");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_object_time",
                schema: "audit",
                table: "audit_events",
                columns: new[] { "object_id", "occurred_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_category",
                schema: "audit",
                table: "audit_events",
                sql: "category IN ('organisation','identity','ticketing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_events_object_time",
                schema: "audit",
                table: "audit_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_category",
                schema: "audit",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_category",
                schema: "audit",
                table: "audit_events",
                sql: "category IN ('organisation','identity')");
        }
    }
}
