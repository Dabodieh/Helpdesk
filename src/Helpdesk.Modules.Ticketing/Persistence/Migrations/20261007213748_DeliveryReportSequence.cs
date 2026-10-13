using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class DeliveryReportSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "delivery_report_seq",
                schema: "ticketing",
                table: "messages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delivery_report_seq",
                schema: "ticketing",
                table: "messages");
        }
    }
}
