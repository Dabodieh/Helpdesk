using Helpdesk.Modules.Ticketing.Domain;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Persistence;

/// <summary>Phase 6 model: requester identity links, request forms, custom fields and their ticket values.</summary>
internal sealed partial class TicketingDbContext
{
    public DbSet<Portal.HelpTopic> HelpTopics => Set<Portal.HelpTopic>();

    public DbSet<UserQueuePreference> UserQueuePreferences => Set<UserQueuePreference>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<TicketNotifyState> TicketNotifyStates => Set<TicketNotifyState>();

    public DbSet<MessageMention> MessageMentions => Set<MessageMention>();

    public DbSet<Services.NotificationOutboxItem> NotificationOutbox => Set<Services.NotificationOutboxItem>();

    public DbSet<Automation.AutomationIntent> AutomationIntents => Set<Automation.AutomationIntent>();

    public DbSet<TicketLifecycleEvent> TicketLifecycleEvents => Set<TicketLifecycleEvent>();

    public DbSet<RequesterLink> RequesterLinks => Set<RequesterLink>();

    public DbSet<RequestForm> RequestForms => Set<RequestForm>();

    public DbSet<TicketField> TicketFields => Set<TicketField>();

    public DbSet<TicketType> TicketTypes => Set<TicketType>();

    public DbSet<TicketTypeField> TicketTypeFields => Set<TicketTypeField>();

    public DbSet<RequestFormAudienceUser> RequestFormAudienceUsers => Set<RequestFormAudienceUser>();

    public DbSet<RequestFormField> RequestFormFields => Set<RequestFormField>();

    public DbSet<TicketFieldValue> TicketFieldValues => Set<TicketFieldValue>();

    private void ConfigurePortal(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserQueuePreference>(e =>
        {
            e.ToTable("user_queue_preferences", t => t.HasCheckConstraint("ck_user_queue_preferences_default", "default_queue IS NULL OR default_view_id IS NULL"));
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasColumnName("user_id").ValueGeneratedNever();
            e.Property(x => x.ColumnsJson).HasColumnName("columns").HasColumnType("jsonb");
            e.Property(x => x.DefaultQueue).HasColumnName("default_queue").HasMaxLength(16);
            e.Property(x => x.DefaultViewId).HasColumnName("default_view_id");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<Portal.HelpTopic>(e =>
        {
            e.ToTable("help_topics", t => t.HasCheckConstraint("ck_help_topics_name", "length(btrim(name)) BETWEEN 1 AND 100"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.RequestFormId).HasColumnName("request_form_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(300);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            // The topic and its form are always of the SAME department (composite key), and a form with topics cannot be deleted (forms are only deactivated anyway).
            e.HasOne<RequestForm>().WithMany().HasForeignKey(x => new { x.RequestFormId, x.DepartmentId })
                .HasPrincipalKey(f => new { f.Id, f.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_help_topics_form_department");
            e.HasIndex(x => new { x.DepartmentId, x.Name }).IsUnique().HasDatabaseName("ux_help_topics_department_name");
            e.HasIndex(x => x.RequestFormId).HasDatabaseName("ix_help_topics_form");
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.ToTable("notifications", t => t.HasCheckConstraint("ck_notifications_type", "type IN ('assigned','requester_reply','followed_update','mentioned','sla_approaching','sla_breached')"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.Type).HasColumnName("type").HasMaxLength(24);
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ReadAt).HasColumnName("read_at");
            // One unread notification per user and ticket (the dedupe key and the upsert target); recent-per-user for the list.
            e.HasIndex(x => new { x.UserId, x.TicketId }).IsUnique().HasFilter("read_at IS NULL").HasDatabaseName("ux_notifications_unread_user_ticket");
            e.HasIndex(x => new { x.UserId, x.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_notifications_user_recent");
        });

        modelBuilder.Entity<Services.NotificationOutboxItem>(e =>
        {
            e.ToTable("notification_outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.SubjectUserId).HasColumnName("subject_user_id");
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(100);
            e.HasIndex(x => x.CreatedAt).HasFilter("processed_at IS NULL").HasDatabaseName("ix_notification_outbox_pending");
        });

        modelBuilder.Entity<TicketLifecycleEvent>(e =>
        {
            e.ToTable("ticket_lifecycle_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(x => x.FromValue).HasColumnName("from_value").HasMaxLength(64);
            e.Property(x => x.ToValue).HasColumnName("to_value").HasMaxLength(64);
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.HasIndex(x => new { x.DepartmentId, x.OccurredAt }).HasDatabaseName("ix_ticket_lifecycle_department_time");
            e.HasIndex(x => new { x.TicketId, x.OccurredAt }).HasDatabaseName("ix_ticket_lifecycle_ticket_time");
        });

        modelBuilder.Entity<Automation.AutomationIntent>(e =>
        {
            e.ToTable("automation_outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.Trigger).HasColumnName("trigger").HasMaxLength(32);
            e.Property(x => x.TriggerKey).HasColumnName("trigger_key").HasMaxLength(200);
            e.Property(x => x.Provenance).HasColumnName("provenance").HasMaxLength(16);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(100);
            e.HasIndex(x => x.CreatedAt).HasFilter("processed_at IS NULL").HasDatabaseName("ix_automation_outbox_pending");
            e.HasIndex(x => new { x.TicketId, x.TriggerKey }).HasDatabaseName("ix_automation_outbox_ticket_key");
        });

        modelBuilder.Entity<MessageMention>(e =>
        {
            e.ToTable("message_mentions");
            e.HasKey(x => new { x.MessageId, x.UserId });
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_message_mentions_message");
            e.HasIndex(x => x.TicketId).HasDatabaseName("ix_message_mentions_ticket");
        });

        modelBuilder.Entity<TicketNotifyState>(e =>
        {
            e.ToTable("ticket_notify_state");
            e.HasKey(x => x.TicketId);
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.AssigneeUserId).HasColumnName("assignee_user_id");
        });

        modelBuilder.Entity<RequesterLink>(e =>
        {
            e.ToTable("requester_links");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.RequesterId).HasColumnName("requester_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.LinkedEmailNormalised).HasColumnName("linked_email_normalised").HasMaxLength(320);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<Requester>().WithMany().HasForeignKey(x => new { x.RequesterId, x.DepartmentId })
                .HasPrincipalKey(r => new { r.Id, r.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_requester_links_requester_department");

            // A requester belongs to at most one user; a user may have one requester per department.
            e.HasIndex(x => x.RequesterId).IsUnique().HasDatabaseName("ux_requester_links_requester");
            e.HasIndex(x => new { x.UserId, x.DepartmentId }).IsUnique().HasDatabaseName("ux_requester_links_user_department");
        });

        modelBuilder.Entity<RequestForm>(e =>
        {
            e.ToTable("request_forms", t =>
            {
                t.HasCheckConstraint("ck_request_forms_name", "length(btrim(name)) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_request_forms_priority", "default_priority IS NULL OR default_priority BETWEEN 1 AND 4");
                t.HasCheckConstraint("ck_request_forms_audience", "audience IN ('AllEmployees','Selected')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.VisibleToRequesters).HasColumnName("visible_to_requesters");
            e.Property(x => x.Audience).HasColumnName("audience").HasMaxLength(16);
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.DefaultTeamId).HasColumnName("default_team_id");
            e.Property(x => x.DefaultPriority).HasColumnName("default_priority").HasConversion<short?>();
            e.Property(x => x.TicketTypeId).HasColumnName("ticket_type_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_request_forms_id_department");
            e.HasOne<TicketType>().WithMany().HasForeignKey(x => new { x.TicketTypeId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_request_forms_type_department");
            e.HasIndex(x => new { x.DepartmentId, x.Name }).IsUnique().HasDatabaseName("ux_request_forms_department_name");
            e.HasIndex(x => new { x.SortOrder, x.Name }).HasFilter("is_active AND visible_to_requesters").HasDatabaseName("ix_request_forms_portal");
        });

        modelBuilder.Entity<TicketField>(e =>
        {
            e.ToTable("ticket_fields", t =>
            {
                t.HasCheckConstraint("ck_ticket_fields_type", "field_type IN ('ShortText','LongText','SingleSelect','MultiSelect','Checkbox','Date','Number')");
                t.HasCheckConstraint("ck_ticket_fields_key", "key ~ '^[a-z][a-z0-9_]{0,39}$'");
                t.HasCheckConstraint("ck_ticket_fields_label", "length(btrim(label)) BETWEEN 1 AND 100");
                t.HasCheckConstraint("ck_ticket_fields_editable_visible", "NOT requester_editable OR requester_visible");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(40);
            e.Property(x => x.Label).HasColumnName("label").HasMaxLength(100);
            e.Property(x => x.HelpText).HasColumnName("help_text").HasMaxLength(500);
            e.Property(x => x.FieldType).HasColumnName("field_type").HasMaxLength(16);
            e.Property(x => x.Required).HasColumnName("required");
            e.Property(x => x.Options).HasColumnName("options").HasColumnType("jsonb");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.AppliesToAllTypes).HasColumnName("applies_to_all_types");
            e.Property(x => x.RequesterVisible).HasColumnName("requester_visible");
            e.Property(x => x.RequesterEditable).HasColumnName("requester_editable");
            e.Property(x => x.AgentVisible).HasColumnName("agent_visible");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_ticket_fields_id_department");
            e.HasIndex(x => new { x.DepartmentId, x.Key }).IsUnique().HasDatabaseName("ux_ticket_fields_department_key");
            e.HasIndex(x => new { x.DepartmentId, x.SortOrder }).HasDatabaseName("ix_ticket_fields_department_order");
        });

        modelBuilder.Entity<TicketType>(e =>
        {
            e.ToTable("ticket_types", t => t.HasCheckConstraint("ck_ticket_types_name", "length(btrim(name)) BETWEEN 1 AND 100"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.IsDefault).HasColumnName("is_default");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_ticket_types_id_department");
            e.HasIndex(x => new { x.DepartmentId, x.Name }).IsUnique().HasDatabaseName("ux_ticket_types_department_name");
            e.HasIndex(x => x.DepartmentId).IsUnique().HasFilter("is_default").HasDatabaseName("ux_ticket_types_department_default");
        });

        modelBuilder.Entity<TicketTypeField>(e =>
        {
            e.ToTable("ticket_type_fields");
            e.HasKey(x => new { x.TypeId, x.FieldId });
            e.Property(x => x.TypeId).HasColumnName("type_id");
            e.Property(x => x.FieldId).HasColumnName("field_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.Required).HasColumnName("required");
            e.HasOne<TicketType>().WithMany().HasForeignKey(x => new { x.TypeId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_ticket_type_fields_type_department");
            e.HasOne<TicketField>().WithMany().HasForeignKey(x => new { x.FieldId, x.DepartmentId })
                .HasPrincipalKey(f => new { f.Id, f.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_ticket_type_fields_field_department");
            e.HasIndex(x => x.FieldId).HasDatabaseName("ix_ticket_type_fields_field");
        });

        modelBuilder.Entity<RequestFormAudienceUser>(e =>
        {
            e.ToTable("request_form_audience_users");
            e.HasKey(x => new { x.FormId, x.UserId });
            e.Property(x => x.FormId).HasColumnName("form_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne<RequestForm>().WithMany().HasForeignKey(x => new { x.FormId, x.DepartmentId })
                .HasPrincipalKey(f => new { f.Id, f.DepartmentId }).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_request_form_audience_form_department");
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_request_form_audience_user");
        });

        modelBuilder.Entity<RequestFormField>(e =>
        {
            e.ToTable("request_form_fields");
            e.HasKey(x => new { x.FormId, x.FieldId });
            e.Property(x => x.FormId).HasColumnName("form_id");
            e.Property(x => x.FieldId).HasColumnName("field_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.Required).HasColumnName("required");
            e.HasOne<RequestForm>().WithMany().HasForeignKey(x => new { x.FormId, x.DepartmentId })
                .HasPrincipalKey(f => new { f.Id, f.DepartmentId }).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_request_form_fields_form_department");
            e.HasOne<TicketField>().WithMany().HasForeignKey(x => new { x.FieldId, x.DepartmentId })
                .HasPrincipalKey(f => new { f.Id, f.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_request_form_fields_field_department");
            e.HasIndex(x => x.FieldId).HasDatabaseName("ix_request_form_fields_field");
        });

        modelBuilder.Entity<TicketFieldValue>(e =>
        {
            e.ToTable("ticket_field_values");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.FormId).HasColumnName("form_id");
            e.Property(x => x.FieldId).HasColumnName("field_id");
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(40);
            e.Property(x => x.Label).HasColumnName("label").HasMaxLength(100);
            e.Property(x => x.FieldType).HasColumnName("field_type").HasMaxLength(16);
            e.Property(x => x.ValueJson).HasColumnName("value").HasColumnType("jsonb");
            e.Property(x => x.Display).HasColumnName("display").HasMaxLength(4000);
            e.Property(x => x.RequesterVisible).HasColumnName("requester_visible");
            e.Property(x => x.AgentVisible).HasColumnName("agent_visible");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => new { x.TicketId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_ticket_field_values_ticket_department");
            e.HasIndex(x => new { x.TicketId, x.Key }).IsUnique().HasDatabaseName("ux_ticket_field_values_ticket_key");
            e.HasIndex(x => new { x.TicketId, x.SortOrder }).HasDatabaseName("ix_ticket_field_values_ticket");
            e.HasIndex(x => new { x.DepartmentId, x.Key }).HasDatabaseName("ix_ticket_field_values_key");
            e.HasQueryFilter(x => ScopedDepartmentIds.Contains(x.DepartmentId));
        });
    }
}
