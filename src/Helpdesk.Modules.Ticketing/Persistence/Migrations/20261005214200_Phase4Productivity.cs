using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Phase4Productivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "canned_responses",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canned_responses", x => x.id);
                    table.CheckConstraint("ck_canned_body", "length(btrim(body)) > 0 AND char_length(body) <= 5000");
                    table.CheckConstraint("ck_canned_scope", "scope IN ('Personal','Department')");
                    table.CheckConstraint("ck_canned_scope_shape", "(scope = 'Personal' AND owner_user_id IS NOT NULL AND department_id IS NULL) OR (scope = 'Department' AND department_id IS NOT NULL AND owner_user_id IS NULL)");
                    table.CheckConstraint("ck_canned_title", "length(btrim(title)) BETWEEN 1 AND 120");
                });

            migrationBuilder.CreateTable(
                name: "saved_views",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    filter = table.Column<string>(type: "jsonb", nullable: false),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_views", x => x.id);
                    table.CheckConstraint("ck_saved_views_name", "length(btrim(name)) BETWEEN 1 AND 80");
                });

            migrationBuilder.CreateIndex(
                name: "ix_canned_department",
                schema: "ticketing",
                table: "canned_responses",
                columns: new[] { "department_id", "title" },
                filter: "scope = 'Department'");

            migrationBuilder.CreateIndex(
                name: "ix_canned_owner",
                schema: "ticketing",
                table: "canned_responses",
                columns: new[] { "owner_user_id", "title" },
                filter: "scope = 'Personal'");

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_owner",
                schema: "ticketing",
                table: "saved_views",
                columns: new[] { "owner_user_id", "created_at" });

            // Search (Phase 4): trigram indexes make the ILIKE '%text%' predicates of the ticket search index-assisted (reference uses its
            // unique btree for prefixes). pg_trgm is a trusted extension, so the database owner may create it.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("CREATE INDEX ix_tickets_subject_trgm ON ticketing.tickets USING gin (subject gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_requesters_name_trgm ON ticketing.requesters USING gin (display_name gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_requesters_email_trgm ON ticketing.requesters USING gin (email_normalised gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_messages_body_trgm ON ticketing.messages USING gin (body gin_trgm_ops);");

            // A user cannot have two saved views with the same name (case-insensitive).
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_saved_views_owner_name ON ticketing.saved_views (owner_user_id, lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ticketing.ix_tickets_subject_trgm, ticketing.ix_requesters_name_trgm, ticketing.ix_requesters_email_trgm, ticketing.ix_messages_body_trgm;");

            migrationBuilder.DropTable(
                name: "canned_responses",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "saved_views",
                schema: "ticketing");
        }
    }
}
