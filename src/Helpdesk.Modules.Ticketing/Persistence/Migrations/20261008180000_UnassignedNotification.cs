using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <summary>Allows the "unassigned" notification type (a ticket lost its assignee because the member left the department). A constraint change only.</summary>
    [DbContext(typeof(TicketingDbContext))]
    [Migration("20261008180000_UnassignedNotification")]
    internal partial class UnassignedNotification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "ck_notifications_type", schema: "ticketing", table: "notifications");
            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_type",
                schema: "ticketing",
                table: "notifications",
                sql: "type IN ('assigned','requester_reply','followed_update','mentioned','sla_approaching','sla_breached','unassigned')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM ticketing.notifications WHERE type = 'unassigned'");
            migrationBuilder.DropCheckConstraint(name: "ck_notifications_type", schema: "ticketing", table: "notifications");
            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_type",
                schema: "ticketing",
                table: "notifications",
                sql: "type IN ('assigned','requester_reply','followed_update','mentioned','sla_approaching','sla_breached')");
        }
    }
}
