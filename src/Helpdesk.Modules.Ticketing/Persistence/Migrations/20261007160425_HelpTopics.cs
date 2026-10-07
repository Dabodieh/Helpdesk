using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class HelpTopics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "help_topics",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_form_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_help_topics", x => x.id);
                    table.CheckConstraint("ck_help_topics_name", "length(btrim(name)) BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "fk_help_topics_form_department",
                        columns: x => new { x.request_form_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "request_forms",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_help_topics_form",
                schema: "ticketing",
                table: "help_topics",
                column: "request_form_id");

            migrationBuilder.CreateIndex(
                name: "IX_help_topics_request_form_id_department_id",
                schema: "ticketing",
                table: "help_topics",
                columns: new[] { "request_form_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ux_help_topics_department_name",
                schema: "ticketing",
                table: "help_topics",
                columns: new[] { "department_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "help_topics",
                schema: "ticketing");
        }
    }
}
