using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <summary>The 80%-consumed alert instant and its "recorded" marker for each SLA clock. Nullable columns only; existing rows fill in the next time their SLA is recalculated.</summary>
    [DbContext(typeof(TicketingDbContext))]
    [Migration("20261008170000_SlaAlertInstants")]
    internal partial class SlaAlertInstants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(name: "fr_alert_at", schema: "ticketing", table: "ticket_sla", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "fr_alerted_at", schema: "ticketing", table: "ticket_sla", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "res_alert_at", schema: "ticketing", table: "ticket_sla", type: "timestamp with time zone", nullable: true);
            migrationBuilder.AddColumn<DateTimeOffset>(name: "res_alerted_at", schema: "ticketing", table: "ticket_sla", type: "timestamp with time zone", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "fr_alert_at", schema: "ticketing", table: "ticket_sla");
            migrationBuilder.DropColumn(name: "fr_alerted_at", schema: "ticketing", table: "ticket_sla");
            migrationBuilder.DropColumn(name: "res_alert_at", schema: "ticketing", table: "ticket_sla");
            migrationBuilder.DropColumn(name: "res_alerted_at", schema: "ticketing", table: "ticket_sla");
        }
    }
}
