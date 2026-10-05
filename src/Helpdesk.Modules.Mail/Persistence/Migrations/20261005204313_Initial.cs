using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Mail.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "mail");

            migrationBuilder.CreateTable(
                name: "mailboxes",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    address_normalised = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    signature = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailboxes", x => x.id);
                    table.UniqueConstraint("ux_mailboxes_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_mailboxes_address", "length(btrim(address)) > 0 AND address_normalised = lower(btrim(address))");
                    table.CheckConstraint("ck_mailboxes_display_name", "length(btrim(display_name)) > 0");
                    table.CheckConstraint("ck_mailboxes_provider", "provider IN ('graph','fake')");
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    resource_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mailbox_sync",
                schema: "mail",
                columns: table => new
                {
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    subscription_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    subscription_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    client_state_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    delta_token = table.Column<string>(type: "text", nullable: true),
                    last_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_sync_error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    dirty = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_sync", x => x.mailbox_id);
                    table.CheckConstraint("ck_mailbox_sync_failures", "consecutive_failures >= 0");
                    table.CheckConstraint("ck_mailbox_sync_state", "subscription_state IN ('None','Active','Failed')");
                    table.ForeignKey(
                        name: "FK_mailbox_sync_mailboxes_mailbox_id",
                        column: x => x.mailbox_id,
                        principalSchema: "mail",
                        principalTable: "mailboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "mail",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    provider_message_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    internet_message_id = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    conversation_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    in_reply_to = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: true),
                    references_ids = table.Column<string>(type: "text", nullable: true),
                    subject = table.Column<string>(type: "character varying(998)", maxLength: 998, nullable: false),
                    from_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    from_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    to_addresses = table.Column<string>(type: "jsonb", nullable: false),
                    cc_addresses = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    body_text = table.Column<string>(type: "text", nullable: true),
                    body_html_original = table.Column<string>(type: "text", nullable: true),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ticket_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_method = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    subject_hint_reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    flags = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ignore_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outbound_token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reply_to_message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_draft_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ticket_reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    delivery_reported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.id);
                    table.CheckConstraint("ck_mail_messages_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_mail_messages_direction", "direction IN ('Inbound','Outbound')");
                    table.CheckConstraint("ck_mail_messages_direction_shape", "(direction = 'Inbound' AND provider_message_id IS NOT NULL AND state IN ('Received','Ticketed','Completed','Ignored','Failed')) OR (direction = 'Outbound' AND outbound_token IS NOT NULL AND state IN ('Queued','Sending','Sent','Failed'))");
                    table.CheckConstraint("ck_mail_messages_state", "state IN ('Received','Ticketed','Completed','Ignored','Queued','Sending','Sent','Failed')");
                    table.ForeignKey(
                        name: "fk_mail_messages_mailbox_department",
                        columns: x => new { x.mailbox_id, x.department_id },
                        principalSchema: "mail",
                        principalTable: "mailboxes",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_mailbox_sync_subscription",
                schema: "mail",
                table: "mailbox_sync",
                column: "subscription_id",
                unique: true,
                filter: "subscription_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mailboxes_department",
                schema: "mail",
                table: "mailboxes",
                column: "department_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ux_mailboxes_address",
                schema: "mail",
                table: "mailboxes",
                column: "address_normalised",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_attention",
                schema: "mail",
                table: "messages",
                columns: new[] { "department_id", "created_at", "id" },
                descending: new[] { false, true, true },
                filter: "state IN ('Failed','Ignored')");

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_department_internet_id",
                schema: "mail",
                table: "messages",
                columns: new[] { "department_id", "internet_message_id" },
                filter: "internet_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_mailbox_conversation",
                schema: "mail",
                table: "messages",
                columns: new[] { "mailbox_id", "conversation_id" },
                filter: "conversation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_outbound_due",
                schema: "mail",
                table: "messages",
                columns: new[] { "state", "next_attempt_at" },
                filter: "direction = 'Outbound' AND state IN ('Queued','Sending')");

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_sender_window",
                schema: "mail",
                table: "messages",
                columns: new[] { "mailbox_id", "from_address", "created_at" },
                filter: "direction = 'Inbound'");

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_ticket",
                schema: "mail",
                table: "messages",
                columns: new[] { "department_id", "ticket_id" },
                filter: "ticket_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mail_messages_ticket_message",
                schema: "mail",
                table: "messages",
                column: "ticket_message_id",
                filter: "ticket_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_messages_mailbox_id_department_id",
                schema: "mail",
                table: "messages",
                columns: new[] { "mailbox_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ux_mail_messages_mailbox_provider_id",
                schema: "mail",
                table: "messages",
                columns: new[] { "mailbox_id", "provider_message_id" },
                unique: true,
                filter: "provider_message_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_mail_messages_outbound_token",
                schema: "mail",
                table: "messages",
                column: "outbound_token",
                unique: true,
                filter: "outbound_token IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mail_notifications_mailbox_time",
                schema: "mail",
                table: "notifications",
                columns: new[] { "mailbox_id", "received_at" });

            // Cross-schema integrity (hand-written: EF does not model other modules' tables). A mailbox belongs to a real department;
            // departments are never hard-deleted, so RESTRICT.
            migrationBuilder.Sql("""
                ALTER TABLE mail.mailboxes
                    ADD CONSTRAINT fk_mailboxes_department FOREIGN KEY (department_id)
                    REFERENCES organisation.departments (id) ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mailbox_sync",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "mail");

            migrationBuilder.DropTable(
                name: "mailboxes",
                schema: "mail");
        }
    }
}
