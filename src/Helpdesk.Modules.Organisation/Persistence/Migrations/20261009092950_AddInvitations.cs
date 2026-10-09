using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Organisation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitations",
                schema: "organisation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    email_normalised = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    email_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => x.id);
                    table.CheckConstraint("ck_invitations_email_state", "email_state IN ('NotConfigured','Accepted','Failed','Uncertain')");
                    table.CheckConstraint("ck_invitations_role", "role IN ('DepartmentAdmin','TeamLead','Agent','Viewer')");
                    table.CheckConstraint("ck_invitations_status", "status IN ('Pending','Accepted','Revoked')");
                    table.ForeignKey(
                        name: "FK_invitations_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "organisation",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invitations_created_at",
                schema: "organisation",
                table: "invitations",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_invitations_department_id",
                schema: "organisation",
                table: "invitations",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ux_invitations_pending",
                schema: "organisation",
                table: "invitations",
                columns: new[] { "email_normalised", "department_id" },
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ux_invitations_token_hash",
                schema: "organisation",
                table: "invitations",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invitations",
                schema: "organisation");
        }
    }
}
