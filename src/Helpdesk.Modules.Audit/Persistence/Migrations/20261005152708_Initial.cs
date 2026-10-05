using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Audit.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    object_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous = table.Column<string>(type: "jsonb", nullable: true),
                    next = table.Column<string>(type: "jsonb", nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.CheckConstraint("ck_audit_events_category", "category IN ('organisation','identity')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_category_time",
                schema: "audit",
                table: "audit_events",
                columns: new[] { "category", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_department_time",
                schema: "audit",
                table: "audit_events",
                columns: new[] { "department_id", "occurred_at", "id" });

            // Append-only guard at the database level: no UPDATE, DELETE or TRUNCATE, whatever role the application uses.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit.audit_events_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit.audit_events is append-only' USING ERRCODE = 'insufficient_privilege';
                END
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_events_no_update_delete BEFORE UPDATE OR DELETE ON audit.audit_events
                FOR EACH ROW EXECUTE FUNCTION audit.audit_events_append_only();
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_events_no_truncate BEFORE TRUNCATE ON audit.audit_events
                FOR EACH STATEMENT EXECUTE FUNCTION audit.audit_events_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "audit");

            migrationBuilder.Sql("DROP FUNCTION audit.audit_events_append_only();");
        }
    }
}
