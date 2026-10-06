using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class TicketMergeAndLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "merged_at",
                schema: "ticketing",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_by_user_id",
                schema: "ticketing",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "merged_into_ticket_id",
                schema: "ticketing",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ticket_links",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_links", x => x.id);
                    table.CheckConstraint("ck_ticket_links_not_self", "source_ticket_id <> target_ticket_id");
                    table.CheckConstraint("ck_ticket_links_type", "type IN ('Related','DuplicateOf','ParentOf')");
                    table.ForeignKey(
                        name: "fk_ticket_links_source_department",
                        columns: x => new { x.source_ticket_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "tickets",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ticket_links_target_department",
                        columns: x => new { x.target_ticket_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "tickets",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_merged_into",
                schema: "ticketing",
                table: "tickets",
                column: "merged_into_ticket_id",
                filter: "merged_into_ticket_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tickets_merged_into_ticket_id_department_id",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "merged_into_ticket_id", "department_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_tickets_merge_shape",
                schema: "ticketing",
                table: "tickets",
                sql: "(merged_into_ticket_id IS NULL) = (merged_at IS NULL) AND (merged_into_ticket_id IS NULL OR merged_into_ticket_id <> id)");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_links_source_ticket_id_department_id",
                schema: "ticketing",
                table: "ticket_links",
                columns: new[] { "source_ticket_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_links_target",
                schema: "ticketing",
                table: "ticket_links",
                column: "target_ticket_id");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_links_target_ticket_id_department_id",
                schema: "ticketing",
                table: "ticket_links",
                columns: new[] { "target_ticket_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ux_ticket_links_pair",
                schema: "ticketing",
                table: "ticket_links",
                columns: new[] { "source_ticket_id", "target_ticket_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_merged_into_department",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "merged_into_ticket_id", "department_id" },
                principalSchema: "ticketing",
                principalTable: "tickets",
                principalColumns: new[] { "id", "department_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tickets_merged_into_department",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "ticket_links",
                schema: "ticketing");

            migrationBuilder.DropIndex(
                name: "ix_tickets_merged_into",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "IX_tickets_merged_into_ticket_id_department_id",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tickets_merge_shape",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "merged_at",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "merged_by_user_id",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "merged_into_ticket_id",
                schema: "ticketing",
                table: "tickets");
        }
    }
}
