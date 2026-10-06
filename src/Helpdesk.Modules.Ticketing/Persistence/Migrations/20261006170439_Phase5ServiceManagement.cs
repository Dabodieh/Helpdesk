using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Phase5ServiceManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.CreateTable(
                name: "automation_rules",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    stop_processing = table.Column<bool>(type: "boolean", nullable: false),
                    conditions = table.Column<string>(type: "jsonb", nullable: false),
                    actions = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_rules", x => x.id);
                    table.UniqueConstraint("ux_automation_rules_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_automation_rules_name", "length(btrim(name)) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "business_hours",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    weekly = table.Column<string>(type: "jsonb", nullable: false),
                    exceptions = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_hours", x => x.id);
                    table.UniqueConstraint("ux_business_hours_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_business_hours_name", "length(btrim(name)) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "sla_events",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    clock = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    detail = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sla_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_sla_events_ticket_department",
                        columns: x => new { x.ticket_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "tickets",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ticket_sla",
                schema: "ticketing",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    snapshot = table.Column<string>(type: "jsonb", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approaching_lead_minutes = table.Column<int>(type: "integer", nullable: false),
                    fr_target_minutes = table.Column<int>(type: "integer", nullable: true),
                    fr_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fr_met_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fr_warned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fr_breached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    res_target_minutes = table.Column<int>(type: "integer", nullable: true),
                    res_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    res_paused_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    res_remaining_seconds = table.Column<long>(type: "bigint", nullable: true),
                    res_met_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    res_warned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    res_breached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    res_cycle = table.Column<int>(type: "integer", nullable: false),
                    next_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_warn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_event_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_sla", x => x.ticket_id);
                    table.ForeignKey(
                        name: "fk_ticket_sla_ticket_department",
                        columns: x => new { x.ticket_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "tickets",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "automation_executions",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    trigger_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    chain_id = table.Column<Guid>(type: "uuid", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    provenance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    matched = table.Column<bool>(type: "boolean", nullable: false),
                    conditions = table.Column<string>(type: "jsonb", nullable: false),
                    actions = table.Column<string>(type: "jsonb", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    skip_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_automation_executions", x => x.id);
                    table.CheckConstraint("ck_automation_executions_state", "state IN ('Running','Completed','Failed','Skipped')");
                    table.ForeignKey(
                        name: "fk_automation_executions_rule_department",
                        columns: x => new { x.rule_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "automation_rules",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_automation_executions_ticket_department",
                        columns: x => new { x.ticket_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "tickets",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sla_policies",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    conditions = table.Column<string>(type: "jsonb", nullable: false),
                    calendar_id = table.Column<Guid>(type: "uuid", nullable: true),
                    first_response_minutes = table.Column<int>(type: "integer", nullable: true),
                    resolution_minutes = table.Column<int>(type: "integer", nullable: true),
                    pause_when_pending = table.Column<bool>(type: "boolean", nullable: false),
                    approaching_lead_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sla_policies", x => x.id);
                    table.CheckConstraint("ck_sla_policies_lead", "approaching_lead_minutes BETWEEN 0 AND 10080");
                    table.CheckConstraint("ck_sla_policies_name", "length(btrim(name)) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_sla_policies_targets", "(first_response_minutes IS NOT NULL OR resolution_minutes IS NOT NULL) AND COALESCE(first_response_minutes, 1) > 0 AND COALESCE(resolution_minutes, 1) > 0");
                    table.ForeignKey(
                        name: "fk_sla_policies_calendar_department",
                        columns: x => new { x.calendar_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "business_hours",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages",
                sql: "source IN ('web','manual','email','automation')");

            migrationBuilder.CreateIndex(
                name: "ix_automation_executions_department",
                schema: "ticketing",
                table: "automation_executions",
                columns: new[] { "department_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_automation_executions_rule_id_department_id",
                schema: "ticketing",
                table: "automation_executions",
                columns: new[] { "rule_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_executions_ticket",
                schema: "ticketing",
                table: "automation_executions",
                columns: new[] { "ticket_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_automation_executions_ticket_id_department_id",
                schema: "ticketing",
                table: "automation_executions",
                columns: new[] { "ticket_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ux_automation_executions_event",
                schema: "ticketing",
                table: "automation_executions",
                columns: new[] { "rule_id", "ticket_id", "trigger_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_rules_trigger",
                schema: "ticketing",
                table: "automation_rules",
                columns: new[] { "department_id", "trigger", "sort_order" },
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ux_business_hours_department_name",
                schema: "ticketing",
                table: "business_hours",
                columns: new[] { "department_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sla_events_ticket",
                schema: "ticketing",
                table: "sla_events",
                columns: new[] { "ticket_id", "at" });

            migrationBuilder.CreateIndex(
                name: "IX_sla_events_ticket_id_department_id",
                schema: "ticketing",
                table: "sla_events",
                columns: new[] { "ticket_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sla_policies_calendar_id_department_id",
                schema: "ticketing",
                table: "sla_policies",
                columns: new[] { "calendar_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_department_order",
                schema: "ticketing",
                table: "sla_policies",
                columns: new[] { "department_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_sla_department_due",
                schema: "ticketing",
                table: "ticket_sla",
                columns: new[] { "department_id", "next_due_at" },
                filter: "next_due_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_sla_department_warn",
                schema: "ticketing",
                table: "ticket_sla",
                columns: new[] { "department_id", "next_warn_at" },
                filter: "next_warn_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_sla_next_event",
                schema: "ticketing",
                table: "ticket_sla",
                column: "next_event_at",
                filter: "next_event_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_sla_ticket_id_department_id",
                schema: "ticketing",
                table: "ticket_sla",
                columns: new[] { "ticket_id", "department_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_executions",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "sla_events",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "sla_policies",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "ticket_sla",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "automation_rules",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "business_hours",
                schema: "ticketing");

            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_source",
                schema: "ticketing",
                table: "messages",
                sql: "source IN ('web','manual','email')");
        }
    }
}
