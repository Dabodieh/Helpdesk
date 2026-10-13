using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddEntraSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entra_settings",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    client_secret_protected = table.Column<string>(type: "text", nullable: false),
                    secret_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    application_object_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    redirect_origins = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    bootstrap_admin_subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    configured_by = table.Column<Guid>(type: "uuid", nullable: false),
                    configured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_entra_settings", x => x.id);
                    table.CheckConstraint("ck_entra_settings_single_row", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entra_settings",
                schema: "identity");
        }
    }
}
