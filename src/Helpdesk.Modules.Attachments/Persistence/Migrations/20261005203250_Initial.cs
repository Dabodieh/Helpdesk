using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Attachments.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "attachments");

            migrationBuilder.CreateTable(
                name: "attachments",
                schema: "attachments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_type = table.Column<string>(type: "character varying(127)", maxLength: 127, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    block_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    scan_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_inline = table.Column<bool>(type: "boolean", nullable: false),
                    content_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    source_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attachments", x => x.id);
                    table.CheckConstraint("ck_attachments_scan_state", "scan_state IN ('NotScanned','Clean','Infected','Skipped')");
                    table.CheckConstraint("ck_attachments_size", "size_bytes >= 0");
                    table.CheckConstraint("ck_attachments_state", "state IN ('Stored','Blocked')");
                    table.CheckConstraint("ck_attachments_state_shape", "(state = 'Stored' AND storage_key IS NOT NULL AND block_reason IS NULL) OR (state = 'Blocked' AND storage_key IS NULL AND block_reason IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_ticket",
                schema: "attachments",
                table: "attachments",
                columns: new[] { "department_id", "ticket_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_attachments_message_source",
                schema: "attachments",
                table: "attachments",
                columns: new[] { "department_id", "message_id", "source_ref" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attachments",
                schema: "attachments");
        }
    }
}
