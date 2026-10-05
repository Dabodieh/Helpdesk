using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Modules.Ticketing.Persistence.Migrations
{
    /// <inheritdoc />
    internal partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ticketing");

            migrationBuilder.CreateTable(
                name: "department_counters",
                schema: "ticketing",
                columns: table => new
                {
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_department_counters", x => x.department_id);
                    table.CheckConstraint("ck_department_counters_next_number", "next_number >= 1");
                });

            migrationBuilder.CreateTable(
                name: "requesters",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    email_normalised = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requesters", x => x.id);
                    table.UniqueConstraint("ux_requesters_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_requesters_display_name_not_blank", "length(btrim(display_name)) > 0");
                    table.CheckConstraint("ck_requesters_email_not_blank", "length(btrim(email)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    requester_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    priority = table.Column<short>(type: "smallint", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assignee_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tickets", x => x.id);
                    table.UniqueConstraint("ux_tickets_id_department", x => new { x.id, x.department_id });
                    table.CheckConstraint("ck_tickets_number_positive", "number > 0");
                    table.CheckConstraint("ck_tickets_priority", "priority BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_tickets_reference_format", "reference ~ '^[A-Z][A-Z0-9]{1,9}-[0-9]+$' AND reference LIKE '%-' || number::text");
                    table.CheckConstraint("ck_tickets_status", "status IN ('Open','Pending','Resolved','Closed')");
                    table.CheckConstraint("ck_tickets_status_timestamps", "(status = 'Closed') = (closed_at IS NOT NULL) AND (status IN ('Resolved','Closed')) = (resolved_at IS NOT NULL)");
                    table.CheckConstraint("ck_tickets_subject", "length(btrim(subject)) BETWEEN 1 AND 200");
                    table.ForeignKey(
                        name: "fk_tickets_requester_department",
                        columns: x => new { x.requester_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "requesters",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "ticketing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    author_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_requester_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    delivery_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.id);
                    table.CheckConstraint("ck_messages_author_matches_type", "(author_type = 'Agent' AND author_user_id IS NOT NULL AND author_requester_id IS NULL) OR (author_type = 'Requester' AND author_requester_id IS NOT NULL AND author_user_id IS NULL) OR (author_type = 'System' AND author_user_id IS NULL AND author_requester_id IS NULL)");
                    table.CheckConstraint("ck_messages_author_type", "author_type IN ('Agent','Requester','System')");
                    table.CheckConstraint("ck_messages_body", "length(btrim(body)) > 0 AND char_length(body) <= 20000");
                    table.CheckConstraint("ck_messages_delivery_state", "delivery_state IN ('None','NotDelivered')");
                    table.CheckConstraint("ck_messages_internal_never_requester_or_delivered", "kind <> 'Internal' OR (author_type <> 'Requester' AND delivery_state = 'None')");
                    table.CheckConstraint("ck_messages_kind", "kind IN ('Public','Internal')");
                    table.CheckConstraint("ck_messages_source", "source IN ('web','manual')");
                    table.ForeignKey(
                        name: "fk_messages_ticket_department",
                        columns: x => new { x.ticket_id, x.department_id },
                        principalSchema: "ticketing",
                        principalTable: "tickets",
                        principalColumns: new[] { "id", "department_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            // Final index choices (TICKETING.md "Indexing"; every list is keyset-paged on (sort key DESC, id DESC) inside the caller's departments):
            //  ux_tickets_reference, ux_tickets_department_number  uniqueness; reference lookup (the {ticket} route accepts IT-1042) and the numbering backstop.
            //  ix_tickets_department_status_updated   general queue / department filter / queue-counts (department_id = ANY(scope), status, newest first).
            //  ix_tickets_assignee_updated (partial)  My Queue: tickets of one assignee, newest first; also serves the SET NULL of the assignee FK.
            //  ix_tickets_team_status_updated (partial) Team Queue: tickets of the caller's teams.
            //  ix_tickets_unassigned_updated (partial) Unassigned queue per department.
            //  ix_tickets_requester_created           requester ticket counts and history.
            //  ix_messages_ticket_created             message list per ticket, newest first (also the FK index of messages -> tickets).
            //  ux_requesters_department_email         requester upsert by normalised email; requester lookup is a prefix/substring scan of one department's requesters (small).
            // priority_desc and created_* sorts run over the same scoped set without a dedicated index (not hot; revisit with real data).
            // The EF-generated FK indexes IX_messages_ticket_id_department_id / IX_tickets_requester_id_department_id are kept as generated.
            migrationBuilder.CreateIndex(
                name: "ix_messages_ticket_created",
                schema: "ticketing",
                table: "messages",
                columns: new[] { "ticket_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_messages_ticket_id_department_id",
                schema: "ticketing",
                table: "messages",
                columns: new[] { "ticket_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ux_requesters_department_email",
                schema: "ticketing",
                table: "requesters",
                columns: new[] { "department_id", "email_normalised" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_assignee_updated",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "assignee_user_id", "updated_at", "id" },
                descending: new[] { false, true, true },
                filter: "assignee_user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_department_status_updated",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "department_id", "status", "updated_at", "id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_requester_created",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "requester_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_tickets_requester_id_department_id",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "requester_id", "department_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_team_status_updated",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "team_id", "status", "updated_at", "id" },
                descending: new[] { false, false, true, true },
                filter: "team_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_unassigned_updated",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "department_id", "updated_at", "id" },
                descending: new[] { false, true, true },
                filter: "assignee_user_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_tickets_department_number",
                schema: "ticketing",
                table: "tickets",
                columns: new[] { "department_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tickets_reference",
                schema: "ticketing",
                table: "tickets",
                column: "reference",
                unique: true);

            // Cross-schema integrity (ADR-012). Organisation (30) and Identity (20) migrate before Ticketing (40). Not modelled in EF.
            migrationBuilder.Sql("""
                ALTER TABLE ticketing.requesters
                    ADD CONSTRAINT fk_requesters_department FOREIGN KEY (department_id) REFERENCES organisation.departments (id) ON DELETE RESTRICT;
                ALTER TABLE ticketing.department_counters
                    ADD CONSTRAINT fk_department_counters_department FOREIGN KEY (department_id) REFERENCES organisation.departments (id) ON DELETE RESTRICT;
                ALTER TABLE ticketing.tickets
                    ADD CONSTRAINT fk_tickets_department FOREIGN KEY (department_id) REFERENCES organisation.departments (id) ON DELETE RESTRICT;
                ALTER TABLE ticketing.tickets
                    ADD CONSTRAINT fk_tickets_created_by_user FOREIGN KEY (created_by_user_id) REFERENCES identity.users (id) ON DELETE RESTRICT;
                ALTER TABLE ticketing.messages
                    ADD CONSTRAINT fk_messages_author_user FOREIGN KEY (author_user_id) REFERENCES identity.users (id) ON DELETE RESTRICT;
                """);

            // Team consistency: a ticket can only name a team of its own department (MATCH SIMPLE: not checked while team_id is null).
            migrationBuilder.Sql("""
                ALTER TABLE ticketing.tickets
                    ADD CONSTRAINT fk_tickets_team_department FOREIGN KEY (team_id, department_id)
                    REFERENCES organisation.teams (id, department_id) ON DELETE RESTRICT;
                """);

            // Assignee consistency: the assignee must hold a membership in the ticket's department. Removing the membership
            // silently unassigns the ticket (known limitation, TICKETING.md invariant 4); only assignee_user_id is nulled.
            migrationBuilder.Sql("""
                ALTER TABLE ticketing.tickets
                    ADD CONSTRAINT fk_tickets_assignee_membership FOREIGN KEY (assignee_user_id, department_id)
                    REFERENCES organisation.department_memberships (user_id, department_id) ON DELETE SET NULL (assignee_user_id);
                """);

            // A requester message must come from a requester of the message's (= ticket's) department.
            migrationBuilder.Sql("""
                ALTER TABLE ticketing.messages
                    ADD CONSTRAINT fk_messages_author_requester FOREIGN KEY (author_requester_id, department_id)
                    REFERENCES ticketing.requesters (id, department_id) ON DELETE RESTRICT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "department_counters",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "tickets",
                schema: "ticketing");

            migrationBuilder.DropTable(
                name: "requesters",
                schema: "ticketing");
        }
    }
}
