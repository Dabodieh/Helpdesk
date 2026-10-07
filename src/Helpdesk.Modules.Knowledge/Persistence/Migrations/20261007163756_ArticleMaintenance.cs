using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Knowledge.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class ArticleMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "owner_user_id",
                schema: "knowledge",
                table: "articles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "review_on",
                schema: "knowledge",
                table: "articles",
                type: "date",
                nullable: true);

            // Existing articles are owned by their author; whether that person is still eligible is decided when the article is read (otherwise it shows as unowned).
            migrationBuilder.Sql("UPDATE knowledge.articles SET owner_user_id = author_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "owner_user_id",
                schema: "knowledge",
                table: "articles");

            migrationBuilder.DropColumn(
                name: "review_on",
                schema: "knowledge",
                table: "articles");
        }
    }
}
