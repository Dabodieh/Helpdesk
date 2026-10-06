using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Helpdesk.Modules.Knowledge.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "knowledge");

            migrationBuilder.CreateTable(
                name: "categories",
                schema: "knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                    table.UniqueConstraint("ux_categories_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_categories_name", "length(btrim(name)) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "articles",
                schema: "knowledge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    visibility = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    draft_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    draft_summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    draft_body = table.Column<string>(type: "text", nullable: false),
                    draft_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    draft_updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    published_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    published_summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    published_body = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "english")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "published_title", "published_summary", "published_body" }),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_articles", x => x.id);
                    table.CheckConstraint("ck_articles_published_shape", "(status = 'Draft' AND published_at IS NULL) OR (status IN ('Published','Archived') AND published_at IS NOT NULL AND published_title IS NOT NULL AND published_body IS NOT NULL)");
                    table.CheckConstraint("ck_articles_slug", "slug ~ '^[a-z0-9][a-z0-9-]{0,79}$'");
                    table.CheckConstraint("ck_articles_status", "status IN ('Draft','Published','Archived')");
                    table.CheckConstraint("ck_articles_visibility", "visibility IN ('Everyone','Requesters')");
                    table.ForeignKey(
                        name: "fk_articles_category_department",
                        columns: x => new { x.category_id, x.department_id },
                        principalSchema: "knowledge",
                        principalTable: "categories",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "article_feedback",
                schema: "knowledge",
                columns: table => new
                {
                    article_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    helpful = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_article_feedback", x => new { x.article_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_feedback_article",
                        column: x => x.article_id,
                        principalSchema: "knowledge",
                        principalTable: "articles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_articles_category",
                schema: "knowledge",
                table: "articles",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "IX_articles_category_id_department_id",
                schema: "knowledge",
                table: "articles",
                columns: new[] { "category_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_articles_department_status",
                schema: "knowledge",
                table: "articles",
                columns: new[] { "department_id", "status", "updated_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_articles_published",
                schema: "knowledge",
                table: "articles",
                columns: new[] { "published_at", "id" },
                descending: new bool[0],
                filter: "status = 'Published'");

            migrationBuilder.CreateIndex(
                name: "ix_articles_search",
                schema: "knowledge",
                table: "articles",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ux_articles_slug",
                schema: "knowledge",
                table: "articles",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_categories_department_name",
                schema: "knowledge",
                table: "categories",
                columns: new[] { "department_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "article_feedback",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "articles",
                schema: "knowledge");

            migrationBuilder.DropTable(
                name: "categories",
                schema: "knowledge");
        }
    }
}
