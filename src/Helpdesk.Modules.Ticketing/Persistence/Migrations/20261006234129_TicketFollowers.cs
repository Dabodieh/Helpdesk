using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class TicketFollowers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ticket_followers",
                schema: "ticketing",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_followers", x => new { x.ticket_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_ticket_followers_ticket_department",
                        columns: x => new { x.ticket_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "tickets",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_followers_ticket_id_department_id",
                schema: "ticketing",
                table: "ticket_followers",
                columns: new[] { "ticket_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_followers_user",
                schema: "ticketing",
                table: "ticket_followers",
                columns: new[] { "user_id", "ticket_id" });

            // A follower must hold a membership of the ticket's department; removing the membership removes the follow (access never comes from this row).
            migrationBuilder.Sql("""
                ALTER TABLE ticketing.ticket_followers
                    ADD CONSTRAINT fk_ticket_followers_membership FOREIGN KEY (user_id, department_id)
                    REFERENCES organisation.department_memberships (user_id, department_id) ON DELETE CASCADE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_followers",
                schema: "ticketing");
        }
    }
}
