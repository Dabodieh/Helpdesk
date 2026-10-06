using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Phase6Portal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_public_activity_at",
                schema: "ticketing",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<Guid>(
                name: "request_form_id",
                schema: "ticketing",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "ticketing",
                table: "tickets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "agent");

            // Existing tickets: the requester-visible activity time starts at their last update; tickets that came through a mailbox are email tickets.
            migrationBuilder.Sql("UPDATE ticketing.tickets SET last_public_activity_at = updated_at, source = CASE WHEN mailbox_id IS NOT NULL THEN 'email' ELSE 'agent' END;");

            migrationBuilder.CreateTable(
                name: "request_forms",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    visible_to_requesters = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    default_team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    default_priority = table.Column<short>(type: "smallint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_forms", x => x.id);
                    table.UniqueConstraint("ux_request_forms_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_request_forms_name", "length(btrim(name)) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_request_forms_priority", "default_priority IS NULL OR default_priority BETWEEN 1 AND 4");
                });

            migrationBuilder.CreateTable(
                name: "requester_links",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_email_normalised = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requester_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_requester_links_requester_department",
                        columns: x => new { x.requester_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "requesters",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_field_values",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_id = table.Column<Guid>(type: "uuid", nullable: true),
                    field_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    field_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    display = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    requester_visible = table.Column<bool>(type: "boolean", nullable: false),
                    agent_visible = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_field_values", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_field_values_ticket_department",
                        columns: x => new { x.ticket_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "tickets",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "form_fields",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    help_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    field_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    requester_visible = table.Column<bool>(type: "boolean", nullable: false),
                    agent_visible = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_fields", x => x.id);
                    table.UniqueConstraint("ux_form_fields_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_form_fields_key", "key ~ '^[a-z][a-z0-9_]{0,39}$'");
                    table.CheckConstraint("ck_form_fields_label", "length(btrim(label)) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_form_fields_type", "field_type IN ('ShortText','LongText','SingleSelect','MultiSelect','Checkbox','Date','Number')");
                    table.ForeignKey(
                        name: "fk_form_fields_form_department",
                        columns: x => new { x.form_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "request_forms",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_requester_public_activity",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "requester_id", "last_public_activity_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_source",
                schema: "ticketing",
                table: "tickets",
                sql: "source IN ('agent','email','portal')");

            migrationBuilder.CreateIndex(
                name: "ix_requesters_email",
                schema: "ticketing",
                table: "requesters",
                column: "email_normalised");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages",
                sql: "source IN ('web','manual','email','automation','portal')");

            migrationBuilder.CreateIndex(
                name: "IX_form_fields_form_id_department_id",
                schema: "ticketing",
                table: "form_fields",
                columns: new[] { "form_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_form_fields_form_order",
                schema: "ticketing",
                table: "form_fields",
                columns: new[] { "form_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_form_fields_form_key",
                schema: "ticketing",
                table: "form_fields",
                columns: new[] { "form_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_request_forms_portal",
                schema: "ticketing",
                table: "request_forms",
                columns: new[] { "sort_order", "name" },
                filter: "is_active AND visible_to_requesters");

            migrationBuilder.CreateIndex(
                name: "ux_request_forms_department_name",
                schema: "ticketing",
                table: "request_forms",
                columns: new[] { "department_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requester_links_requester_id_department_id",
                schema: "ticketing",
                table: "requester_links",
                columns: new[] { "requester_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ux_requester_links_requester",
                schema: "ticketing",
                table: "requester_links",
                column: "requester_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_requester_links_user_department",
                schema: "ticketing",
                table: "requester_links",
                columns: new[] { "user_id", "department_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_field_values_key",
                schema: "ticketing",
                table: "ticket_field_values",
                columns: new[] { "department_id", "key" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_field_values_ticket",
                schema: "ticketing",
                table: "ticket_field_values",
                columns: new[] { "ticket_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_field_values_ticket_id_department_id",
                schema: "ticketing",
                table: "ticket_field_values",
                columns: new[] { "ticket_id", "department_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "form_fields",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "requester_links",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "ticket_field_values",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "request_forms",
                schema: "ticketing");

            migrationBuilder.DropIndex(
                name: "ix_tickets_requester_public_activity",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_source",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_requesters_email",
                schema: "ticketing",
                table: "requesters");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "last_public_activity_at",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "request_form_id",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages",
                sql: "source IN ('web','manual','email','automation')");
        }
    }
}
