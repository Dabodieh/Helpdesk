using Helpdesk.Modules.Attachments.Domain;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Helpdesk.Modules.Attachments.Persistence;

internal sealed class AttachmentsDbContext(DbContextOptions<AttachmentsDbContext> options) : DbContext(options)
{
    public const string Schema = "attachments";

    public DbSet<Attachment> Attachments => Set<Attachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Attachment>(e =>
        {
            e.ToTable("attachments", t =>
            {
                t.HasCheckConstraint("ck_attachments_state", "state IN ('Stored','Blocked')");
                t.HasCheckConstraint("ck_attachments_scan_state", "scan_state IN ('NotScanned','Clean','Infected','Skipped')");
                t.HasCheckConstraint("ck_attachments_size", "size_bytes >= 0");

                // Stored items have bytes and a key; blocked items have a reason and no key.
                t.HasCheckConstraint(
                    "ck_attachments_state_shape",
                    "(state = 'Stored' AND storage_key IS NOT NULL AND block_reason IS NULL) OR (state = 'Blocked' AND storage_key IS NULL AND block_reason IS NOT NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.MessageId).HasColumnName("message_id");
            e.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(255);
            e.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(127);
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.Sha256).HasColumnName("sha256");
            e.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(200);
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(16);
            e.Property(x => x.BlockReason).HasColumnName("block_reason").HasMaxLength(64);
            e.Property(x => x.ScanState).HasColumnName("scan_state").HasMaxLength(16);
            e.Property(x => x.IsInline).HasColumnName("is_inline");
            e.Property(x => x.ContentId).HasColumnName("content_id").HasMaxLength(255);
            e.Property(x => x.SourceRef).HasColumnName("source_ref").HasMaxLength(512);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");

            // Idempotency: one row per (department, message, source reference).
            e.HasIndex(x => new { x.DepartmentId, x.MessageId, x.SourceRef }).IsUnique().HasDatabaseName("ux_attachments_message_source");
            e.HasIndex(x => new { x.DepartmentId, x.TicketId, x.CreatedAt }).HasDatabaseName("ix_attachments_ticket");
        });
    }
}

internal sealed class AttachmentsDesignTimeFactory : IDesignTimeDbContextFactory<AttachmentsDbContext>
{
    public AttachmentsDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<AttachmentsDbContext>(AttachmentsDbContext.Schema));
}
