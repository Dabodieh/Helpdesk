using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Helpdesk.Modules.Ticketing.Persistence;

internal sealed partial class TicketingDbContext(DbContextOptions<TicketingDbContext> options, IDepartmentScope scope) : DbContext(options)
{
    public const string Schema = "ticketing";

    public DbSet<Requester> Requesters => Set<Requester>();

    public DbSet<DepartmentCounter> DepartmentCounters => Set<DepartmentCounter>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<TicketParticipant> Participants => Set<TicketParticipant>();

    public DbSet<TicketLink> TicketLinks => Set<TicketLink>();

    public DbSet<SavedView> SavedViews => Set<SavedView>();

    public DbSet<CannedResponse> CannedResponses => Set<CannedResponse>();

    /// <summary>Read by the global query filters on every query (EF evaluates context members per query). Empty = nothing visible.</summary>
    internal Guid[] ScopedDepartmentIds => scope.DepartmentIds;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Requester>(e =>
        {
            e.ToTable("requesters", t =>
            {
                t.HasCheckConstraint("ck_requesters_email_not_blank", "length(btrim(email)) > 0");
                t.HasCheckConstraint("ck_requesters_display_name_not_blank", "length(btrim(display_name)) > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(320);
            e.Property(x => x.EmailNormalised).HasColumnName("email_normalised").HasMaxLength(320);
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_requesters_id_department");
            e.HasIndex(x => new { x.DepartmentId, x.EmailNormalised }).IsUnique().HasDatabaseName("ux_requesters_department_email");
            e.HasIndex(x => x.EmailNormalised).HasDatabaseName("ix_requesters_email");
            e.HasQueryFilter(x => ScopedDepartmentIds.Contains(x.DepartmentId));
        });

        modelBuilder.Entity<DepartmentCounter>(e =>
        {
            e.ToTable("department_counters", t => t.HasCheckConstraint("ck_department_counters_next_number", "next_number >= 1"));
            e.HasKey(x => x.DepartmentId);
            e.Property(x => x.DepartmentId).HasColumnName("department_id").ValueGeneratedNever();
            e.Property(x => x.NextNumber).HasColumnName("next_number");
        });

        modelBuilder.Entity<Ticket>(e =>
        {
            e.ToTable("tickets", t =>
            {
                t.HasCheckConstraint("ck_tickets_number_positive", "number > 0");
                t.HasCheckConstraint("ck_tickets_reference_format", "reference ~ '^[A-Z][A-Z0-9]{1,9}-[0-9]+$' AND reference LIKE '%-' || number::text");
                t.HasCheckConstraint("ck_tickets_subject", "length(btrim(subject)) BETWEEN 1 AND 200");
                t.HasCheckConstraint("ck_tickets_status", "status IN ('Open','Pending','Resolved','Closed')");
                t.HasCheckConstraint("ck_tickets_priority", "priority BETWEEN 1 AND 4");
                t.HasCheckConstraint("ck_tickets_source", "source IN ('agent','email','portal')");
                t.HasCheckConstraint(
                    "ck_tickets_status_timestamps",
                    "(status = 'Closed') = (closed_at IS NOT NULL) AND (status IN ('Resolved','Closed')) = (resolved_at IS NOT NULL)");
                t.HasCheckConstraint(
                    "ck_tickets_merge_shape",
                    "(merged_into_ticket_id IS NULL) = (merged_at IS NULL) AND (merged_into_ticket_id IS NULL OR merged_into_ticket_id <> id)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Number).HasColumnName("number");
            e.Property(x => x.Reference).HasColumnName("reference").HasMaxLength(32);
            e.Property(x => x.RequesterId).HasColumnName("requester_id");
            e.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(200);
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(16).HasConversion<string>();
            e.Property(x => x.Priority).HasColumnName("priority").HasConversion<short>();
            e.Property(x => x.TeamId).HasColumnName("team_id");
            e.Property(x => x.AssigneeUserId).HasColumnName("assignee_user_id");
            e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            e.Property(x => x.MailboxId).HasColumnName("mailbox_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.LastPublicActivityAt).HasColumnName("last_public_activity_at");
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(16);
            e.Property(x => x.RequestFormId).HasColumnName("request_form_id");
            e.Property(x => x.TicketTypeId).HasColumnName("ticket_type_id");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.MergedIntoTicketId).HasColumnName("merged_into_ticket_id");
            e.Property(x => x.MergedAt).HasColumnName("merged_at");
            e.Property(x => x.MergedByUserId).HasColumnName("merged_by_user_id");
            e.Ignore(x => x.IsMerged);
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

            // Composite FK target for messages; (requester_id, department_id) keeps the requester in the ticket's department.
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_tickets_id_department");
            e.HasOne(x => x.Requester).WithMany().HasForeignKey(x => new { x.RequesterId, x.DepartmentId })
                .HasPrincipalKey(r => new { r.Id, r.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_tickets_requester_department");

            // The canonical ticket must be in the same department (nullable columns: the FK is not enforced while merged_into_ticket_id is null).
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => new { x.MergedIntoTicketId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_tickets_merged_into_department");
            e.HasIndex(x => x.MergedIntoTicketId).HasFilter("merged_into_ticket_id IS NOT NULL").HasDatabaseName("ix_tickets_merged_into");

            e.HasOne<TicketType>().WithMany().HasForeignKey(x => new { x.TicketTypeId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_tickets_type_department");

            // Indexing choices (TICKETING.md "Indexing"): see the Initial migration for the rationale of each.
            e.HasIndex(x => x.Reference).IsUnique().HasDatabaseName("ux_tickets_reference");
            e.HasIndex(x => new { x.DepartmentId, x.Number }).IsUnique().HasDatabaseName("ux_tickets_department_number");
            e.HasIndex(x => new { x.DepartmentId, x.Status, x.UpdatedAt, x.Id }).IsDescending(false, false, true, true).HasDatabaseName("ix_tickets_department_status_updated");
            e.HasIndex(x => new { x.AssigneeUserId, x.UpdatedAt, x.Id }).IsDescending(false, true, true)
                .HasFilter("assignee_user_id IS NOT NULL").HasDatabaseName("ix_tickets_assignee_updated");
            e.HasIndex(x => new { x.TeamId, x.Status, x.UpdatedAt, x.Id }).IsDescending(false, false, true, true)
                .HasFilter("team_id IS NOT NULL").HasDatabaseName("ix_tickets_team_status_updated");
            e.HasIndex(x => new { x.DepartmentId, x.UpdatedAt, x.Id }).IsDescending(false, true, true)
                .HasFilter("assignee_user_id IS NULL").HasDatabaseName("ix_tickets_unassigned_updated");
            e.HasIndex(x => new { x.RequesterId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_tickets_requester_created");
            e.HasIndex(x => new { x.RequesterId, x.LastPublicActivityAt, x.Id }).IsDescending(false, true, true).HasDatabaseName("ix_tickets_requester_public_activity");
            e.HasIndex(x => new { x.DepartmentId, x.TicketTypeId }).HasFilter("ticket_type_id IS NOT NULL").HasDatabaseName("ix_tickets_department_type");
            e.HasQueryFilter(x => ScopedDepartmentIds.Contains(x.DepartmentId));
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.ToTable("messages", t =>
            {
                t.HasCheckConstraint("ck_messages_kind", "kind IN ('Public','Internal')");
                t.HasCheckConstraint("ck_messages_author_type", "author_type IN ('Agent','Requester','System')");
                t.HasCheckConstraint("ck_messages_source", "source IN ('web','manual','email','automation','portal')");
                t.HasCheckConstraint("ck_messages_delivery_state", "delivery_state IN ('None','NotDelivered','Pending','Sent','Failed')");

                // Only an agent's public reply has a delivery state; requester mail and notes are always None.
                t.HasCheckConstraint("ck_messages_delivery_only_agent_public", "delivery_state = 'None' OR (kind = 'Public' AND author_type = 'Agent')");
                t.HasCheckConstraint("ck_messages_body", "length(btrim(body)) > 0 AND char_length(body) <= 20000");
                // An internal note is never authored by, or addressed to, a requester and never carries a delivery state.
                t.HasCheckConstraint("ck_messages_internal_never_requester_or_delivered", "kind <> 'Internal' OR (author_type <> 'Requester' AND delivery_state = 'None')");
                t.HasCheckConstraint(
                    "ck_messages_author_matches_type",
                    "(author_type = 'Agent' AND author_user_id IS NOT NULL AND author_requester_id IS NULL) "
                    + "OR (author_type = 'Requester' AND author_requester_id IS NOT NULL AND author_user_id IS NULL) "
                    + "OR (author_type = 'System' AND author_user_id IS NULL AND author_requester_id IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16).HasConversion<string>();
            e.Property(x => x.AuthorType).HasColumnName("author_type").HasMaxLength(16).HasConversion<string>();
            e.Property(x => x.AuthorUserId).HasColumnName("author_user_id");
            e.Property(x => x.AuthorRequesterId).HasColumnName("author_requester_id");
            e.Property(x => x.Body).HasColumnName("body");
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(16);
            e.Property(x => x.DeliveryState).HasColumnName("delivery_state").HasMaxLength(16).HasConversion<string>();
            e.Property(x => x.DeliveryError).HasColumnName("delivery_error").HasMaxLength(200);
            e.Property(x => x.SourceRef).HasColumnName("source_ref").HasMaxLength(512);
            e.Property(x => x.BodyHtml).HasColumnName("body_html");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => new { x.TicketId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_messages_ticket_department");
            e.HasIndex(x => new { x.TicketId, x.CreatedAt, x.Id }).IsDescending(false, true, true).HasDatabaseName("ix_messages_ticket_created");

            // Idempotency of producers (email ingestion): the same source reference can never create a second message.
            e.HasIndex(x => new { x.DepartmentId, x.Source, x.SourceRef }).IsUnique().HasFilter("source_ref IS NOT NULL").HasDatabaseName("ux_messages_source_ref");
            e.HasQueryFilter(x => ScopedDepartmentIds.Contains(x.DepartmentId));
        });

        modelBuilder.Entity<SavedView>(e =>
        {
            e.ToTable("saved_views", t => t.HasCheckConstraint("ck_saved_views_name", "length(btrim(name)) BETWEEN 1 AND 80"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(80);
            e.Property(x => x.Filter).HasColumnName("filter").HasColumnType("jsonb");
            e.Property(x => x.IsPinned).HasColumnName("is_pinned");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => new { x.OwnerUserId, x.CreatedAt }).HasDatabaseName("ix_saved_views_owner");
        });

        modelBuilder.Entity<CannedResponse>(e =>
        {
            e.ToTable("canned_responses", t =>
            {
                t.HasCheckConstraint("ck_canned_scope", "scope IN ('Personal','Department')");
                t.HasCheckConstraint(
                    "ck_canned_scope_shape",
                    "(scope = 'Personal' AND owner_user_id IS NOT NULL AND department_id IS NULL) OR (scope = 'Department' AND department_id IS NOT NULL AND owner_user_id IS NULL)");
                t.HasCheckConstraint("ck_canned_title", "length(btrim(title)) BETWEEN 1 AND 120");
                t.HasCheckConstraint("ck_canned_body", "length(btrim(body)) > 0 AND char_length(body) <= 5000");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(16);
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(120);
            e.Property(x => x.Body).HasColumnName("body");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasIndex(x => new { x.DepartmentId, x.Title }).HasFilter("scope = 'Department'").HasDatabaseName("ix_canned_department");
            e.HasIndex(x => new { x.OwnerUserId, x.Title }).HasFilter("scope = 'Personal'").HasDatabaseName("ix_canned_owner");

            // Department rows are visible only inside the caller's department scope; personal rows are filtered by owner in the service.
            e.HasQueryFilter(x => x.DepartmentId == null || ScopedDepartmentIds.Contains(x.DepartmentId.Value));
        });

        modelBuilder.Entity<TicketLink>(e =>
        {
            e.ToTable("ticket_links", t =>
            {
                t.HasCheckConstraint("ck_ticket_links_type", "type IN ('Related','DuplicateOf','ParentOf')");
                t.HasCheckConstraint("ck_ticket_links_not_self", "source_ticket_id <> target_ticket_id");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.SourceTicketId).HasColumnName("source_ticket_id");
            e.Property(x => x.TargetTicketId).HasColumnName("target_ticket_id");
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(16);
            e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => new { x.SourceTicketId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_ticket_links_source_department");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => new { x.TargetTicketId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_ticket_links_target_department");
            e.HasIndex(x => new { x.SourceTicketId, x.TargetTicketId }).IsUnique().HasDatabaseName("ux_ticket_links_pair");
            e.HasIndex(x => x.TargetTicketId).HasDatabaseName("ix_ticket_links_target");
            e.HasQueryFilter(x => ScopedDepartmentIds.Contains(x.DepartmentId));
        });

        ConfigureServiceManagement(modelBuilder);
        ConfigurePortal(modelBuilder);

        modelBuilder.Entity<TicketParticipant>(e =>
        {
            e.ToTable("ticket_participants", t => t.HasCheckConstraint("ck_ticket_participants_email", "length(btrim(email)) > 0"));
            e.HasKey(x => new { x.TicketId, x.EmailNormalised });
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.EmailNormalised).HasColumnName("email_normalised").HasMaxLength(320);
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(320);
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => new { x.TicketId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_ticket_participants_ticket_department");
            e.HasQueryFilter(x => ScopedDepartmentIds.Contains(x.DepartmentId));
        });
    }
}

internal sealed class TicketingDesignTimeFactory : IDesignTimeDbContextFactory<TicketingDbContext>
{
    public TicketingDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<TicketingDbContext>(TicketingDbContext.Schema), new EmptyDepartmentScope());
}
