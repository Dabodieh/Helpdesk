using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class TicketTypesAndFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Phase 6 fields were per form. Keep them: copy the rows aside, build the department-level model, then fill it (ids are preserved, so ticket values
            // that reference a field keep pointing at it). Keys that now collide inside a department get a numeric suffix (the first one keeps its key).
            migrationBuilder.Sql("CREATE TABLE ticketing.__form_fields_backup AS SELECT * FROM ticketing.form_fields;");

            migrationBuilder.DropTable(
                name: "form_fields",
                schema: "ticketing");

            migrationBuilder.AddColumn<Guid>(
                name: "ticket_type_id",
                schema: "ticketing",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "ticketing",
                table: "ticket_field_values",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "ticket_type_id",
                schema: "ticketing",
                table: "request_forms",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ticket_fields",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    help_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    field_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    applies_to_all_types = table.Column<bool>(type: "boolean", nullable: false),
                    requester_visible = table.Column<bool>(type: "boolean", nullable: false),
                    requester_editable = table.Column<bool>(type: "boolean", nullable: false),
                    agent_visible = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_fields", x => x.id);
                    table.UniqueConstraint("ux_ticket_fields_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_ticket_fields_editable_visible", "NOT requester_editable OR requester_visible");
                    table.CheckConstraint("ck_ticket_fields_key", "key ~ '^[a-z][a-z0-9_]{0,39}$'");
                    table.CheckConstraint("ck_ticket_fields_label", "length(btrim(label)) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_ticket_fields_type", "field_type IN ('ShortText','LongText','SingleSelect','MultiSelect','Checkbox','Date','Number')");
                });

            migrationBuilder.CreateTable(
                name: "ticket_types",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_types", x => x.id);
                    table.UniqueConstraint("ux_ticket_types_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_ticket_types_name", "length(btrim(name)) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "request_form_fields",
                schema: "ticketing",
                columns: table => new
                {
                    form_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_request_form_fields", x => new { x.form_id, x.field_id });
                    table.ForeignKey(
                        name: "fk_request_form_fields_field_department",
                        columns: x => new { x.field_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "ticket_fields",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_request_form_fields_form_department",
                        columns: x => new { x.form_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "request_forms",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_type_fields",
                schema: "ticketing",
                columns: table => new
                {
                    type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_type_fields", x => new { x.type_id, x.field_id });
                    table.ForeignKey(
                        name: "fk_ticket_type_fields_field_department",
                        columns: x => new { x.field_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "ticket_fields",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_type_fields_type_department",
                        columns: x => new { x.type_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "ticket_types",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_department_type",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "department_id", "ticket_type_id" },
                filter: "ticket_type_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tickets_ticket_type_id_department_id",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "ticket_type_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ux_ticket_field_values_ticket_key",
                schema: "ticketing",
                table: "ticket_field_values",
                columns: new[] { "ticket_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_request_forms_ticket_type_id_department_id",
                schema: "ticketing",
                table: "request_forms",
                columns: new[] { "ticket_type_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_request_form_fields_field",
                schema: "ticketing",
                table: "request_form_fields",
                column: "field_id");

            migrationBuilder.CreateIndex(
                name: "IX_request_form_fields_field_id_department_id",
                schema: "ticketing",
                table: "request_form_fields",
                columns: new[] { "field_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_request_form_fields_form_id_department_id",
                schema: "ticketing",
                table: "request_form_fields",
                columns: new[] { "form_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_fields_department_order",
                schema: "ticketing",
                table: "ticket_fields",
                columns: new[] { "department_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_ticket_fields_department_key",
                schema: "ticketing",
                table: "ticket_fields",
                columns: new[] { "department_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_type_fields_field",
                schema: "ticketing",
                table: "ticket_type_fields",
                column: "field_id");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_type_fields_field_id_department_id",
                schema: "ticketing",
                table: "ticket_type_fields",
                columns: new[] { "field_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_type_fields_type_id_department_id",
                schema: "ticketing",
                table: "ticket_type_fields",
                columns: new[] { "type_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ux_ticket_types_department_default",
                schema: "ticketing",
                table: "ticket_types",
                column: "department_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ux_ticket_types_department_name",
                schema: "ticketing",
                table: "ticket_types",
                columns: new[] { "department_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_request_forms_type_department",
                schema: "ticketing",
                table: "request_forms",
                columns: new[] { "ticket_type_id", "department_id" },
                principalSchema: "ticketing",
                principalTable: "ticket_types",
                principalColumns: new[] { "id", "department_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_type_department",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "ticket_type_id", "department_id" },
                principalSchema: "ticketing",
                principalTable: "ticket_types",
                principalColumns: new[] { "id", "department_id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(
                """
                INSERT INTO ticketing.ticket_fields (id, department_id, key, label, help_text, field_type, required, options, sort_order, is_active, applies_to_all_types, requester_visible, requester_editable, agent_visible, created_at, updated_at)
                SELECT id, department_id, CASE WHEN rn = 1 THEN key ELSE left(key, 36) || '_' || rn END, label, help_text, field_type, required, options, sort_order, is_active, false, requester_visible, false, agent_visible, created_at, updated_at
                FROM (SELECT b.*, row_number() OVER (PARTITION BY department_id, key ORDER BY created_at, id) AS rn FROM ticketing.__form_fields_backup b) x;
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO ticketing.request_form_fields (form_id, field_id, department_id, sort_order, required)
                SELECT form_id, id, department_id, sort_order, NULL FROM ticketing.__form_fields_backup;
                """);
            migrationBuilder.Sql("UPDATE ticketing.ticket_field_values SET updated_at = created_at;");
            migrationBuilder.Sql("DROP TABLE ticketing.__form_fields_backup;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down restores the per-form shape: a field linked to several forms stays with the first of them; fields linked to none are dropped (their ticket values keep their snapshots).
            migrationBuilder.Sql(
                """
                CREATE TABLE ticketing.__ticket_fields_backup AS
                SELECT DISTINCT ON (f.id) f.*, l.form_id, l.required AS link_required, l.sort_order AS link_order
                FROM ticketing.ticket_fields f JOIN ticketing.request_form_fields l ON l.field_id = f.id
                ORDER BY f.id, l.sort_order;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_request_forms_type_department",
                schema: "ticketing",
                table: "request_forms");

            migrationBuilder.DropForeignKey(
                name: "fk_tickets_type_department",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "request_form_fields",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "ticket_type_fields",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "ticket_fields",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "ticket_types",
                schema: "ticketing");

            migrationBuilder.DropIndex(
                name: "ix_tickets_department_type",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_ticket_type_id_department_id",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ux_ticket_field_values_ticket_key",
                schema: "ticketing",
                table: "ticket_field_values");

            migrationBuilder.DropIndex(
                name: "IX_request_forms_ticket_type_id_department_id",
                schema: "ticketing",
                table: "request_forms");

            migrationBuilder.DropColumn(
                name: "ticket_type_id",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "ticketing",
                table: "ticket_field_values");

            migrationBuilder.DropColumn(
                name: "ticket_type_id",
                schema: "ticketing",
                table: "request_forms");

            migrationBuilder.CreateTable(
                name: "form_fields",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_visible = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    form_id = table.Column<Guid>(type: "uuid", nullable: false),
                    help_text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    requester_visible = table.Column<bool>(type: "boolean", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
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
            migrationBuilder.Sql(
                """
                INSERT INTO ticketing.form_fields (id, form_id, department_id, key, label, help_text, field_type, required, options, sort_order, is_active, requester_visible, agent_visible, created_at, updated_at)
                SELECT id, form_id, department_id, key, label, help_text, field_type, COALESCE(link_required, required), options, link_order, is_active, requester_visible, agent_visible, created_at, updated_at
                FROM ticketing.__ticket_fields_backup;
                """);
            migrationBuilder.Sql("DROP TABLE ticketing.__ticket_fields_backup;");
        }
    }
}
