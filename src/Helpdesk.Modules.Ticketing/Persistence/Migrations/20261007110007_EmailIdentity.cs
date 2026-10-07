using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class EmailIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_signatures",
                schema: "ticketing",
                columns: table => new
                {
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    signature_html = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_signatures", x => new { x.department_id, x.user_id });
                    table.CheckConstraint("ck_agent_signatures_length", "char_length(signature_html) <= 8000");
                });

            migrationBuilder.CreateTable(
                name: "department_auto_replies",
                schema: "ticketing",
                columns: table => new
                {
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    for_email = table.Column<bool>(type: "boolean", nullable: false),
                    for_portal = table.Column<bool>(type: "boolean", nullable: false),
                    subject_template = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body_template = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_department_auto_replies", x => x.department_id);
                    table.CheckConstraint("ck_auto_replies_body", "length(btrim(body_template)) BETWEEN 1 AND 5000");
                    table.CheckConstraint("ck_auto_replies_subject", "length(btrim(subject_template)) BETWEEN 1 AND 200");
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_signatures_user",
                schema: "ticketing",
                table: "agent_signatures",
                column: "user_id");
        
            // A signature belongs to a department membership and goes with it.
            migrationBuilder.Sql("""
                ALTER TABLE ticketing.agent_signatures
                    ADD CONSTRAINT fk_agent_signatures_membership FOREIGN KEY (user_id, department_id)
                    REFERENCES organisation.department_memberships (user_id, department_id) ON DELETE CASCADE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_signatures",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "department_auto_replies",
                schema: "ticketing");
        }
    }
}
