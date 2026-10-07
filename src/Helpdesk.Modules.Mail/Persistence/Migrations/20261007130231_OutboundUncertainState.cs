using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Mail.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class OutboundUncertainState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_mail_messages_direction_shape",
                schema: "mail",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mail_messages_state",
                schema: "mail",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mail_messages_direction_shape",
                schema: "mail",
                table: "messages",
                sql: "(direction = 'Inbound' AND provider_message_id IS NOT NULL AND state IN ('Received','Ticketed','Completed','Ignored','Failed')) OR (direction = 'Outbound' AND outbound_token IS NOT NULL AND state IN ('Queued','Sending','Sent','Uncertain','Failed'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mail_messages_state",
                schema: "mail",
                table: "messages",
                sql: "state IN ('Received','Ticketed','Completed','Ignored','Queued','Sending','Sent','Uncertain','Failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Uncertain does not exist before this migration; the nearest older state is Failed (the outcome is unknown either way).
            migrationBuilder.Sql("UPDATE mail.messages SET state = 'Failed', last_error = COALESCE(last_error, 'outcome_uncertain') WHERE state = 'Uncertain'");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mail_messages_direction_shape",
                schema: "mail",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_mail_messages_state",
                schema: "mail",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mail_messages_direction_shape",
                schema: "mail",
                table: "messages",
                sql: "(direction = 'Inbound' AND provider_message_id IS NOT NULL AND state IN ('Received','Ticketed','Completed','Ignored','Failed')) OR (direction = 'Outbound' AND outbound_token IS NOT NULL AND state IN ('Queued','Sending','Sent','Failed'))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_mail_messages_state",
                schema: "mail",
                table: "messages",
                sql: "state IN ('Received','Ticketed','Completed','Ignored','Queued','Sending','Sent','Failed')");
        }
    }
}
