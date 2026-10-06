using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Audit.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class KnowledgeCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_category",
                schema: "audit",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_category",
                schema: "audit",
                table: "audit_events",
                sql: "category IN ('organisation','identity','ticketing','mail','knowledge')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_events_category",
                schema: "audit",
                table: "audit_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_events_category",
                schema: "audit",
                table: "audit_events",
                sql: "category IN ('organisation','identity','ticketing','mail')");
        }
    }
}
