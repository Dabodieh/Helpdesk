using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class InternalNoteMentions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_type",
                schema: "ticketing",
                table: "notifications");

            migrationBuilder.AddColumn<Guid>(
                name: "message_id",
                schema: "ticketing",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "message_id",
                schema: "ticketing",
                table: "notification_outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "subject_user_id",
                schema: "ticketing",
                table: "notification_outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "message_mentions",
                schema: "ticketing",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_mentions", x => new { x.message_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_message_mentions_message",
                        column: x => x.message_id,
                        principalSchema: "ticketing",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_type",
                schema: "ticketing",
                table: "notifications",
                sql: "type IN ('assigned','requester_reply','followed_update','mentioned')");

            migrationBuilder.CreateIndex(
                name: "ix_message_mentions_ticket",
                schema: "ticketing",
                table: "message_mentions",
                column: "ticket_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mention notifications do not exist before this migration (derived data); remove them so the older type check holds.
            migrationBuilder.Sql("DELETE FROM ticketing.notifications WHERE type = 'mentioned'");

            migrationBuilder.DropTable(
                name: "message_mentions",
                schema: "ticketing");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notifications_type",
                schema: "ticketing",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "message_id",
                schema: "ticketing",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "message_id",
                schema: "ticketing",
                table: "notification_outbox");

            migrationBuilder.DropColumn(
                name: "subject_user_id",
                schema: "ticketing",
                table: "notification_outbox");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_type",
                schema: "ticketing",
                table: "notifications",
                sql: "type IN ('assigned','requester_reply','followed_update')");
        }
    }
}
