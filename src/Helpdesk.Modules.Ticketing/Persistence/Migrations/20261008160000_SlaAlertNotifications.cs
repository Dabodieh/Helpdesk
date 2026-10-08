using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <summary>Allows the two SLA alert notification types. A constraint change only: no table or column changes.</summary>
    [DbContext(typeof(TicketingDbContext))]
    [Migration("20261008160000_SlaAlertNotifications")]
    internal partial class SlaAlertNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "ck_notifications_type", schema: "ticketing", table: "notifications");
            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_type",
                schema: "ticketing",
                table: "notifications",
                sql: "type IN ('assigned','requester_reply','followed_update','mentioned','sla_approaching','sla_breached')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM ticketing.notifications WHERE type IN ('sla_approaching','sla_breached')");
            migrationBuilder.DropCheckConstraint(name: "ck_notifications_type", schema: "ticketing", table: "notifications");
            migrationBuilder.AddCheckConstraint(
                name: "ck_notifications_type",
                schema: "ticketing",
                table: "notifications",
                sql: "type IN ('assigned','requester_reply','followed_update','mentioned')");
        }
    }
}
