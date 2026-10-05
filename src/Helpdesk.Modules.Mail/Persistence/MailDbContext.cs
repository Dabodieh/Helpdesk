using Helpdesk.Modules.Mail.Domain;
using Helpdesk.SharedKernel.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Helpdesk.Modules.Mail.Persistence;

internal sealed class MailDbContext(DbContextOptions<MailDbContext> options) : DbContext(options)
{
    public const string Schema = "mail";

    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();

    public DbSet<MailboxSync> MailboxSyncs => Set<MailboxSync>();

    public DbSet<MailMessage> Messages => Set<MailMessage>();

    public DbSet<MailNotification> Notifications => Set<MailNotification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Mailbox>(e =>
        {
            e.ToTable("mailboxes", t =>
            {
                t.HasCheckConstraint("ck_mailboxes_provider", "provider IN ('graph','fake')");
                t.HasCheckConstraint("ck_mailboxes_address", "length(btrim(address)) > 0 AND address_normalised = lower(btrim(address))");
                t.HasCheckConstraint("ck_mailboxes_display_name", "length(btrim(display_name)) > 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Address).HasColumnName("address").HasMaxLength(320);
            e.Property(x => x.AddressNormalised).HasColumnName("address_normalised").HasMaxLength(320);
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200);
            e.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(16);
            e.Property(x => x.ProviderRef).HasColumnName("provider_ref").HasMaxLength(128);
            e.Property(x => x.Signature).HasColumnName("signature").HasMaxLength(4000);
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.IsDefault).HasColumnName("is_default");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

            // Composite FK target for messages (and, from Ticketing's migration, tickets): a mailbox reference always names its department too.
            e.HasAlternateKey(x => new { x.Id, x.DepartmentId }).HasName("ux_mailboxes_id_department");

            // One mailbox belongs to exactly one department (ADR-010): the address is unique installation-wide.
            e.HasIndex(x => x.AddressNormalised).IsUnique().HasDatabaseName("ux_mailboxes_address");

            // At most one default sender per department.
            e.HasIndex(x => x.DepartmentId).IsUnique().HasFilter("is_default").HasDatabaseName("ux_mailboxes_department_default");
            e.HasIndex(x => x.DepartmentId).HasDatabaseName("ix_mailboxes_department");
            e.HasOne(x => x.Sync).WithOne().HasForeignKey<MailboxSync>(s => s.MailboxId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MailboxSync>(e =>
        {
            e.ToTable("mailbox_sync", t =>
            {
                t.HasCheckConstraint("ck_mailbox_sync_state", "subscription_state IN ('None','Active','Failed')");
                t.HasCheckConstraint("ck_mailbox_sync_failures", "consecutive_failures >= 0");
            });
            e.HasKey(x => x.MailboxId);
            e.Property(x => x.MailboxId).HasColumnName("mailbox_id").ValueGeneratedNever();
            e.Property(x => x.SubscriptionId).HasColumnName("subscription_id").HasMaxLength(128);
            e.Property(x => x.SubscriptionExpiresAt).HasColumnName("subscription_expires_at");
            e.Property(x => x.SubscriptionState).HasColumnName("subscription_state").HasMaxLength(16);
            e.Property(x => x.ClientStateHash).HasColumnName("client_state_hash");
            e.Property(x => x.DeltaToken).HasColumnName("delta_token");
            e.Property(x => x.LastSyncAt).HasColumnName("last_sync_at");
            e.Property(x => x.LastSyncError).HasColumnName("last_sync_error").HasMaxLength(200);
            e.Property(x => x.ConsecutiveFailures).HasColumnName("consecutive_failures");
            e.Property(x => x.Dirty).HasColumnName("dirty");

            // Webhooks find the mailbox through OUR subscription record, never from the payload.
            e.HasIndex(x => x.SubscriptionId).IsUnique().HasFilter("subscription_id IS NOT NULL").HasDatabaseName("ux_mailbox_sync_subscription");
        });

        modelBuilder.Entity<MailMessage>(e =>
        {
            e.ToTable("messages", t =>
            {
                t.HasCheckConstraint("ck_mail_messages_direction", "direction IN ('Inbound','Outbound')");
                t.HasCheckConstraint(
                    "ck_mail_messages_state",
                    "state IN ('Received','Ticketed','Completed','Ignored','Queued','Sending','Sent','Failed')");

                // Inbound mail has a provider id and only inbound states; outbound mail has a token and only outbound states.
                t.HasCheckConstraint(
                    "ck_mail_messages_direction_shape",
                    "(direction = 'Inbound' AND provider_message_id IS NOT NULL AND state IN ('Received','Ticketed','Completed','Ignored','Failed')) "
                    + "OR (direction = 'Outbound' AND outbound_token IS NOT NULL AND state IN ('Queued','Sending','Sent','Failed'))");
                t.HasCheckConstraint("ck_mail_messages_attempts", "attempts >= 0");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.MailboxId).HasColumnName("mailbox_id");
            e.Property(x => x.DepartmentId).HasColumnName("department_id");
            e.Property(x => x.Direction).HasColumnName("direction").HasMaxLength(16);
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(16);
            e.Property(x => x.ProviderMessageId).HasColumnName("provider_message_id").HasMaxLength(512);
            e.Property(x => x.InternetMessageId).HasColumnName("internet_message_id").HasMaxLength(998);
            e.Property(x => x.ConversationId).HasColumnName("conversation_id").HasMaxLength(512);
            e.Property(x => x.InReplyTo).HasColumnName("in_reply_to").HasMaxLength(998);
            e.Property(x => x.References).HasColumnName("references_ids");
            e.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(998);
            e.Property(x => x.FromAddress).HasColumnName("from_address").HasMaxLength(320);
            e.Property(x => x.FromName).HasColumnName("from_name").HasMaxLength(200);
            e.Property(x => x.ToJson).HasColumnName("to_addresses").HasColumnType("jsonb");
            e.Property(x => x.CcJson).HasColumnName("cc_addresses").HasColumnType("jsonb");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.BodyText).HasColumnName("body_text");
            e.Property(x => x.BodyHtmlOriginal).HasColumnName("body_html_original");
            e.Property(x => x.HeadersJson).HasColumnName("headers").HasColumnType("jsonb");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.TicketMessageId).HasColumnName("ticket_message_id");
            e.Property(x => x.CorrelationMethod).HasColumnName("correlation_method").HasMaxLength(16);
            e.Property(x => x.SubjectHintReference).HasColumnName("subject_hint_reference").HasMaxLength(32);
            e.Property(x => x.Flags).HasColumnName("flags").HasMaxLength(200);
            e.Property(x => x.IgnoreReason).HasColumnName("ignore_reason").HasMaxLength(64);
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(200);
            e.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            e.Property(x => x.OutboundToken).HasColumnName("outbound_token").HasMaxLength(64);
            e.Property(x => x.ReplyToMessageId).HasColumnName("reply_to_message_id");
            e.Property(x => x.ProviderDraftId).HasColumnName("provider_draft_id").HasMaxLength(512);
            e.Property(x => x.TicketReference).HasColumnName("ticket_reference").HasMaxLength(32);
            e.Property(x => x.RequestedByUserId).HasColumnName("requested_by_user_id");
            e.Property(x => x.DeliveryReportedAt).HasColumnName("delivery_reported_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            e.HasOne<Mailbox>().WithMany().HasForeignKey(x => new { x.MailboxId, x.DepartmentId })
                .HasPrincipalKey(m => new { m.Id, m.DepartmentId }).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_mail_messages_mailbox_department");

            // Idempotency boundary for inbound mail: (mailbox, provider immutable id).
            e.HasIndex(x => new { x.MailboxId, x.ProviderMessageId }).IsUnique().HasFilter("provider_message_id IS NOT NULL")
                .HasDatabaseName("ux_mail_messages_mailbox_provider_id");

            // Correlation lookups (all scoped to a department / mailbox).
            e.HasIndex(x => new { x.DepartmentId, x.InternetMessageId }).HasFilter("internet_message_id IS NOT NULL").HasDatabaseName("ix_mail_messages_department_internet_id");
            e.HasIndex(x => new { x.MailboxId, x.ConversationId }).HasFilter("conversation_id IS NOT NULL").HasDatabaseName("ix_mail_messages_mailbox_conversation");
            e.HasIndex(x => x.OutboundToken).IsUnique().HasFilter("outbound_token IS NOT NULL").HasDatabaseName("ux_mail_messages_outbound_token");
            e.HasIndex(x => new { x.DepartmentId, x.TicketId }).HasFilter("ticket_id IS NOT NULL").HasDatabaseName("ix_mail_messages_ticket");
            e.HasIndex(x => x.TicketMessageId).HasFilter("ticket_message_id IS NOT NULL").HasDatabaseName("ix_mail_messages_ticket_message");

            // Outbound dispatcher and the failed-mail list.
            e.HasIndex(x => new { x.State, x.NextAttemptAt }).HasFilter("direction = 'Outbound' AND state IN ('Queued','Sending')").HasDatabaseName("ix_mail_messages_outbound_due");
            e.HasIndex(x => new { x.DepartmentId, x.CreatedAt, x.Id }).IsDescending(false, true, true)
                .HasFilter("state IN ('Failed','Ignored')").HasDatabaseName("ix_mail_messages_attention");

            // Per-sender rate limiting window.
            e.HasIndex(x => new { x.MailboxId, x.FromAddress, x.CreatedAt }).HasFilter("direction = 'Inbound'").HasDatabaseName("ix_mail_messages_sender_window");
        });

        modelBuilder.Entity<MailNotification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.MailboxId).HasColumnName("mailbox_id");
            e.Property(x => x.ChangeType).HasColumnName("change_type").HasMaxLength(32);
            e.Property(x => x.ResourceId).HasColumnName("resource_id").HasMaxLength(512);
            e.Property(x => x.ReceivedAt).HasColumnName("received_at");
            e.HasIndex(x => new { x.MailboxId, x.ReceivedAt }).HasDatabaseName("ix_mail_notifications_mailbox_time");
        });
    }
}

internal sealed class MailDesignTimeFactory : IDesignTimeDbContextFactory<MailDbContext>
{
    public MailDbContext CreateDbContext(string[] args) =>
        new(ModuleDbContextExtensions.DesignTimeOptions<MailDbContext>(MailDbContext.Schema));
}
