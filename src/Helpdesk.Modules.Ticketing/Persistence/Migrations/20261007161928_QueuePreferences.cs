using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class QueuePreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_queue_preferences",
                schema: "ticketing",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    columns = table.Column<string>(type: "jsonb", nullable: true),
                    default_queue = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    default_view_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_queue_preferences", x => x.user_id);
                    table.CheckConstraint("ck_user_queue_preferences_default", "default_queue IS NULL OR default_view_id IS NULL");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_queue_preferences",
                schema: "ticketing");
        }
    }
}
