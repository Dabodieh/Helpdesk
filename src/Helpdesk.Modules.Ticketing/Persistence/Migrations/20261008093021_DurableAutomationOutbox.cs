using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class DurableAutomationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "ticketing",
                table: "notification_outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                schema: "ticketing",
                table: "notification_outbox",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "automation_outbox",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    trigger_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provenance = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_outbox", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_outbox_pending",
                schema: "ticketing",
                table: "automation_outbox",
                column: "created_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_automation_outbox_ticket_key",
                schema: "ticketing",
                table: "automation_outbox",
                columns: new[] { "ticket_id", "trigger_key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_outbox",
                schema: "ticketing");

            migrationBuilder.DropColumn(
                name: "attempts",
                schema: "ticketing",
                table: "notification_outbox");

            migrationBuilder.DropColumn(
                name: "last_error",
                schema: "ticketing",
                table: "notification_outbox");
        }
    }
}
