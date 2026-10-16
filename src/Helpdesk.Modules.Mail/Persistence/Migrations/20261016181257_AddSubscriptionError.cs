using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Mail.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class AddSubscriptionError : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "subscription_error",
                schema: "mail",
                table: "mailbox_sync",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "subscription_error",
                schema: "mail",
                table: "mailbox_sync");
        }
    }
}
