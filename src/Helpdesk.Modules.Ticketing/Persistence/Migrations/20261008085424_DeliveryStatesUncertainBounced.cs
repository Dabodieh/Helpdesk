using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryStatesUncertainBounced : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_delivery_state",
                schema: "ticketing",
                table: "messages");

            // Existing Failed rows already carry the category that separates the three cases; make that explicit.
            migrationBuilder.Sql("UPDATE ticketing.messages SET delivery_state = 'Uncertain' WHERE delivery_state = 'Failed' AND delivery_error = 'outcome_uncertain';");
            migrationBuilder.Sql("UPDATE ticketing.messages SET delivery_state = 'Bounced' WHERE delivery_state = 'Failed' AND delivery_error = 'delivery_failed';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_delivery_state",
                schema: "ticketing",
                table: "messages",
                sql: "delivery_state IN ('None','NotDelivered','Pending','Sent','Failed','Uncertain','Bounced')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_messages_delivery_state",
                schema: "ticketing",
                table: "messages");

            migrationBuilder.Sql("UPDATE ticketing.messages SET delivery_state = 'Failed' WHERE delivery_state IN ('Uncertain','Bounced');");

            migrationBuilder.AddCheckConstraint(
                name: "ck_messages_delivery_state",
                schema: "ticketing",
                table: "messages",
                sql: "delivery_state IN ('None','NotDelivered','Pending','Sent','Failed')");
        }
    }
}
