using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Helpdesk.Modules.Audit.Persistence;

internal sealed class AuditEvent
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string Category { get; set; } = "";

    public string Action { get; set; } = "";

    public Guid? ActorUserId { get; set; }

    public string? ActorDisplayName { get; set; }

    public string ObjectType { get; set; } = "";

    public Guid? ObjectId { get; set; }

    public Guid? DepartmentId { get; set; }

    public string? Previous { get; set; }

    public string? Next { get; set; }

    public string Source { get; set; } = "";

    public string? CorrelationId { get; set; }
}

/// <summary>Read model plus migrations. Application code never saves through this context: audit rows are inserted by <see cref="AuditWriter"/>.</summary>
internal sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public const string Schema = "audit";

    public DbSet<AuditEvent> Events => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.Category).HasColumnName("category").HasMaxLength(32);
            e.Property(x => x.Action).HasColumnName("action").HasMaxLength(100);
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.ActorDisplayName).HasColumnName("actor_display_name").HasMaxLength(200);
            e.Property(x => x.ObjectType).HasColumnName("object_type").HasMaxLength(64);
            e.Property(x => x.ObjectId).HasColumnName("object_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Previous).HasColumnName("previous").HasColumnType("jsonb");
            e.Property(x => x.Next).HasColumnName("next").HasColumnType("jsonb");
            e.Property(x => x.Source).HasColumnName("source").HasMaxLength(32);
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            e.HasIndex(x => new { x.DepartmentId, x.OccurredAt, x.Id }).HasDatabaseName("ix_audit_events_department_time");
            e.HasIndex(x => new { x.Category, x.OccurredAt, x.Id }).HasDatabaseName("ix_audit_events_category_time");
            e.ToTable(t => t.HasCheckConstraint("ck_audit_events_category", "category IN ('organisation','identity')"));
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw ReadOnly();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    private static NotSupportedException ReadOnly() =>
        new("The audit log is append-only and is written only through IAuditWriter.");
}

internal sealed class AuditDesignTimeFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<AuditDbContext>(AuditDbContext.Schema));
}
