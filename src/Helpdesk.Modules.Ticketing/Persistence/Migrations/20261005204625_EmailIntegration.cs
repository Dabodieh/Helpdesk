using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class EmailIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_delivery_state",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.AddColumn<Guid>(
                name: "mailbox_id",
                schema: "ticketing",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "body_html",
                schema: "ticketing",
                table: "messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delivery_error",
                schema: "ticketing",
                table: "messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_ref",
                schema: "ticketing",
                table: "messages",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ticket_participants",
                schema: "ticketing",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email_normalised = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_participants", x => new { x.ticket_id, x.email_normalised });
                    table.CheckConstraint("ck_ticket_participants_email", "length(btrim(email)) > 0");
                    table.ForeignKey(
                        name: "fk_ticket_participants_ticket_department",
                        columns: x => new { x.ticket_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "tickets",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_messages_source_ref",
                schema: "ticketing",
                table: "messages",
                columns: new[] { "department_id", "source", "source_ref" },
                unique: true,
                filter: "source_ref IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_delivery_only_agent_public",
                schema: "ticketing",
                table: "messages",
                sql: "delivery_state = 'None' OR (kind = 'Public' AND author_type = 'Agent')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_delivery_state",
                schema: "ticketing",
                table: "messages",
                sql: "delivery_state IN ('None','NotDelivered','Pending','Sent','Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages",
                sql: "source IN ('web','manual','email')");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_participants_ticket_id_department_id",
                schema: "ticketing",
                table: "ticket_participants",
                columns: new[] { "ticket_id", "department_id" });

            // A ticket that arrived by email keeps its mailbox; the composite key forces that mailbox to belong to the ticket's department
            // (replies can never leave from another department's identity). Mailboxes are deactivated, never deleted: RESTRICT.
            migrationBuilder.Sql("""
                ALTER TABLE ticketing.tickets
                    ADD CONSTRAINT fk_tickets_mailbox_department FOREIGN KEY (mailbox_id, department_id)
                    REFERENCES mail.mailboxes (id, department_id) ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_participants",
                schema: "ticketing");

            migrationBuilder.DropIndex(
                name: "ux_messages_source_ref",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_delivery_only_agent_public",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_delivery_state",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "mailbox_id",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "body_html",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "delivery_error",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "source_ref",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_delivery_state",
                schema: "ticketing",
                table: "messages",
                sql: "delivery_state IN ('None','NotDelivered')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages",
                sql: "source IN ('web','manual')");
        }
    }
}
