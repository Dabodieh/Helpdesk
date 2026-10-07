using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Mail.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryReportSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "delivery_seq",
                schema: "mail",
                table: "messages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Rows that already exist start at 1 so a report of their current outcome is newer than the 0 every ticket message starts with.
            migrationBuilder.Sql("UPDATE mail.messages SET delivery_seq = 1 WHERE direction = 'Outbound'");

            // Every outbound state change moves the sequence on by one, whichever code path made it (never a wall clock).
            migrationBuilder.Sql(
                """
                CREATE FUNCTION mail.bump_delivery_seq() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.direction = 'Outbound' AND NEW.state IS DISTINCT FROM OLD.state THEN
                        NEW.delivery_seq := OLD.delivery_seq + 1;
                    END IF;
                    RETURN NEW;
                END;
                $$
                """);
            migrationBuilder.Sql("CREATE TRIGGER trg_messages_bump_delivery_seq BEFORE UPDATE ON mail.messages FOR EACH ROW EXECUTE FUNCTION mail.bump_delivery_seq()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_messages_bump_delivery_seq ON mail.messages");
            migrationBuilder.Sql("DROP FUNCTION mail.bump_delivery_seq()");
            migrationBuilder.DropColumn(
                name: "delivery_seq",
                schema: "mail",
                table: "messages");
        }
    }
}
