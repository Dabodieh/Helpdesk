using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Organisation.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "organisation");

            migrationBuilder.CreateTable(
                name: "departments",
                schema: "organisation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => x.id);
                    table.CheckConstraint("ck_departments_key_format", "key ~ '^[A-Z][A-Z0-9]{1,9}$'");
                    table.CheckConstraint("ck_departments_name_not_blank", "length(btrim(name)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "department_memberships",
                schema: "organisation",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_department_memberships", x => new { x.user_id, x.department_id });
                    table.CheckConstraint("ck_department_memberships_role", "role IN ('DepartmentAdmin','TeamLead','Agent','Viewer')");
                    table.ForeignKey(
                        name: "FK_department_memberships_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "organisation",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "teams",
                schema: "organisation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_teams", x => x.id);
                    table.UniqueConstraint("ux_teams_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_teams_name_not_blank", "length(btrim(name)) > 0");
                    table.ForeignKey(
                        name: "FK_teams_departments_department_id",
                        column: x => x.department_id,
                        principalSchema: "organisation",
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "team_memberships",
                schema: "organisation",
                columns: table => new
                {
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_team_memberships", x => new { x.team_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_team_memberships_department_membership",
                        columns: x => new { x.user_id, x.department_id },
                        principalSchema: "organisation",
                        principalTable: "department_memberships",
                        principalColumns: new[] { "user_id", "department_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_team_memberships_team_department",
                        columns: x => new { x.team_id, x.department_id },
                        principalSchema: "organisation",
                        principalTable: "teams",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_department_memberships_department_id",
                schema: "organisation",
                table: "department_memberships",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_memberships_team_id_department_id",
                schema: "organisation",
                table: "team_memberships",
                columns: new[] { "team_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_team_memberships_user_id",
                schema: "organisation",
                table: "team_memberships",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_team_memberships_user_id_department_id",
                schema: "organisation",
                table: "team_memberships",
                columns: new[] { "user_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_teams_department_id",
                schema: "organisation",
                table: "teams",
                column: "department_id");

            // Case-insensitive uniqueness (expression indexes are not modelled in EF).
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_departments_key_lower ON organisation.departments (lower(key));");
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_departments_name_lower ON organisation.departments (lower(name));");
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_teams_department_name_lower ON organisation.teams (department_id, lower(name));");

            // Cross-schema integrity (ADR-009): memberships reference identity.users. The identity migration must be applied first.
            migrationBuilder.Sql("""
                ALTER TABLE organisation.department_memberships
                    ADD CONSTRAINT fk_department_memberships_user FOREIGN KEY (user_id) REFERENCES identity.users (id) ON DELETE RESTRICT;
                """);

            // A department key is immutable after creation.
            migrationBuilder.Sql("""
                CREATE FUNCTION organisation.departments_key_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.key IS DISTINCT FROM OLD.key THEN
                        RAISE EXCEPTION 'organisation.departments.key is immutable' USING ERRCODE = 'integrity_constraint_violation';
                    END IF;
                    RETURN NEW;
                END
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER departments_key_immutable BEFORE UPDATE ON organisation.departments
                FOR EACH ROW EXECUTE FUNCTION organisation.departments_key_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_memberships",
                schema: "organisation");

            migrationBuilder.DropTable(
                name: "department_memberships",
                schema: "organisation");

            migrationBuilder.DropTable(
                name: "teams",
                schema: "organisation");

            migrationBuilder.DropTable(
                name: "departments",
                schema: "organisation");

            migrationBuilder.Sql("DROP FUNCTION organisation.departments_key_immutable();");
        }
    }
}
