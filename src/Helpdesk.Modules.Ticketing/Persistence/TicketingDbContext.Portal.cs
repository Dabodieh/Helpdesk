using Helpdesk.Modules.Ticketing.Domain;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Persistence;

/// <summary>Phase 6 model: requester identity links, request forms, custom fields and their ticket values.</summary>
internal sealed partial class TicketingDbContext
{
    public DbSet<RequesterLink> RequesterLinks => Set<RequesterLink>();

    public DbSet<RequestForm> RequestForms => Set<RequestForm>();

    public DbSet<FormField> FormFields => Set<FormField>();

    public DbSet<TicketFieldValue> TicketFieldValues => Set<TicketFieldValue>();

    private void ConfigurePortal(ModelBuilder modelBuilder)
    {
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
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100);
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(1000);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.VisibleToRequesters).HasColumnName("visible_to_requesters");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.DefaultTeamId).HasColumnName("default_team_id");
            e.Property(x => x.DefaultPriority).HasColumnName("default_priority").HasConversion<short?>();
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_request_forms_id_department");
            e.HasIndex(x => new { x.DepartmentId, x.Name }).IsUnique().HasDatabaseName("ux_request_forms_department_name");
            e.HasIndex(x => new { x.SortOrder, x.Name }).HasFilter("is_active AND visible_to_requesters").HasDatabaseName("ix_request_forms_portal");
        });

        modelBuilder.Entity<FormField>(e =>
        {
            e.ToTable("form_fields", t =>
            {
                t.HasCheckConstraint("ck_form_fields_type", "field_type IN ('ShortText','LongText','SingleSelect','MultiSelect','Checkbox','Date','Number')");
                t.HasCheckConstraint("ck_form_fields_key", "key ~ '^[a-z][a-z0-9_]{0,39}$'");
                t.HasCheckConstraint("ck_form_fields_label", "length(btrim(label)) BETWEEN 1 AND 100");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.FormId).HasColumnName("form_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(40);
            e.Property(x => x.Label).HasColumnName("label").HasMaxLength(100);
            e.Property(x => x.HelpText).HasColumnName("help_text").HasMaxLength(500);
            e.Property(x => x.FieldType).HasColumnName("field_type").HasMaxLength(16);
            e.Property(x => x.Required).HasColumnName("required");
            e.Property(x => x.Options).HasColumnName("options").HasColumnType("jsonb");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.RequesterVisible).HasColumnName("requester_visible");
            e.Property(x => x.AgentVisible).HasColumnName("agent_visible");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_form_fields_id_department");
            e.HasOne<RequestForm>().WithMany().HasForeignKey(x => new { x.FormId, x.DepartmentId })
                .HasPrincipalKey(f => new { f.Id, f.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_form_fields_form_department");
            e.HasIndex(x => new { x.FormId, x.Key }).IsUnique().HasDatabaseName("ux_form_fields_form_key");
            e.HasIndex(x => new { x.FormId, x.SortOrder }).HasDatabaseName("ix_form_fields_form_order");
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
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => new { x.TicketId, x.DepartmentId })
                .HasPrincipalKey(t => new { t.Id, t.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_ticket_field_values_ticket_department");
            e.HasIndex(x => new { x.TicketId, x.SortOrder }).HasDatabaseName("ix_ticket_field_values_ticket");
            e.HasIndex(x => new { x.DepartmentId, x.Key }).HasDatabaseName("ix_ticket_field_values_key");
            e.HasQueryFilter(x => ScopedDepartmentIds.Contains(x.DepartmentId));
        });
    }
}
