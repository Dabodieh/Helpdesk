using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class RequestFormAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audience",
                schema: "ticketing",
                table: "request_forms",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "AllEmployees");

            // Compatibility decision: every existing form keeps its previous behaviour (offered to all signed-in employees). New forms are created restricted by the application.
            migrationBuilder.Sql("ALTER TABLE ticketing.request_forms ALTER COLUMN audience DROP DEFAULT");

            migrationBuilder.CreateTable(
                name: "request_form_audience_users",
                schema: "ticketing",
                columns: table => new
                {
                    form_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_form_audience_users", x => new { x.form_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_request_form_audience_form_department",
                        columns: x => new { x.form_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "request_forms",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_request_forms_audience",
                schema: "ticketing",
                table: "request_forms",
                sql: "audience IN ('AllEmployees','Selected')");

            migrationBuilder.CreateIndex(
                name: "ix_request_form_audience_user",
                schema: "ticketing",
                table: "request_form_audience_users",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_request_form_audience_users_form_id_department_id",
                schema: "ticketing",
                table: "request_form_audience_users",
                columns: new[] { "form_id", "department_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "request_form_audience_users",
                schema: "ticketing");

            migrationBuilder.DropCheckConstraint(
                name: "ck_request_forms_audience",
                schema: "ticketing",
                table: "request_forms");

            migrationBuilder.DropColumn(
                name: "audience",
                schema: "ticketing",
                table: "request_forms");
        }
    }
}
