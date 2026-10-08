using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketParties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_protected",
                schema: "ticketing",
                table: "ticket_participants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "removed_at",
                schema: "ticketing",
                table: "ticket_participants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "removed_by_user_id",
                schema: "ticketing",
                table: "ticket_participants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "requester_id",
                schema: "ticketing",
                table: "ticket_participants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_participants_requester",
                schema: "ticketing",
                table: "ticket_participants",
                column: "requester_id",
                filter: "requester_id IS NOT NULL AND removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_participants_requester_id_department_id",
                schema: "ticketing",
                table: "ticket_participants",
                columns: new[] { "requester_id", "department_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_ticket_participants_protected_requester",
                schema: "ticketing",
                table: "ticket_participants",
                sql: "NOT is_protected OR requester_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_ticket_participants_requester_department",
                schema: "ticketing",
                table: "ticket_participants",
                columns: new[] { "requester_id", "department_id" },
                principalSchema: "ticketing",
                principalTable: "requesters",
                principalColumns: new[] { "id", "department_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_ticket_participants_requester_department",
                schema: "ticketing",
                table: "ticket_participants");

            migrationBuilder.DropIndex(
                name: "ix_ticket_participants_requester",
                schema: "ticketing",
                table: "ticket_participants");

            migrationBuilder.DropIndex(
                name: "IX_ticket_participants_requester_id_department_id",
                schema: "ticketing",
                table: "ticket_participants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ticket_participants_protected_requester",
                schema: "ticketing",
                table: "ticket_participants");

            migrationBuilder.DropColumn(
                name: "is_protected",
                schema: "ticketing",
                table: "ticket_participants");

            migrationBuilder.DropColumn(
                name: "removed_at",
                schema: "ticketing",
                table: "ticket_participants");

            migrationBuilder.DropColumn(
                name: "removed_by_user_id",
                schema: "ticketing",
                table: "ticket_participants");

            migrationBuilder.DropColumn(
                name: "requester_id",
                schema: "ticketing",
                table: "ticket_participants");
        }
    }
}
