using Helpdesk.Modules.Mail.Domain;
using Helpdesk.Modules.Mail.Persistence;
using Helpdesk.SharedKernel.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Modules.Mail.Services;

/// <summary>Operational numbers of the mail loop (SRE-005). Counts and ages only: no address, subject or id leaves the module.</summary>
internal abstract class MailSignal(IServiceScopeFactory scopes, TimeProvider clock) : IOperationalSignal
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string Unit { get; }

    public async Task<double?> ReadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await ReadAsync(scope.ServiceProvider.GetRequiredService<MailDbContext>(), clock.GetUtcNow(), cancellationToken);
    }

    protected abstract Task<double?> ReadAsync(MailDbContext db, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>How long the oldest outbound message has been waiting to be sent. Growing means the dispatcher or the provider is stuck.</summary>
internal sealed class OutboundQueueAgeSignal(IServiceScopeFactory scopes, TimeProvider clock) : MailSignal(scopes, clock)
{
    public override string Name => "helpdesk_mail_outbound_oldest_queued_age_seconds";

    public override string Description => "Age of the oldest outbound email still queued or sending.";

    public override string Unit => "s";

    protected override async Task<double?> ReadAsync(MailDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var oldest = await db.Messages.AsNoTracking()
            .Where(m => m.Direction == MessageDirections.Outbound && (m.State == MessageStates.Queued || m.State == MessageStates.Sending))
            .MinAsync(m => (DateTimeOffset?)m.CreatedAt, ct);
        return oldest is null ? 0 : Math.Max(0, (now - oldest.Value).TotalSeconds);
    }
}

/// <summary>The longest time since an active mailbox was last synchronised (a mailbox never synchronised counts from its creation).</summary>
internal sealed class MailSyncAgeSignal(IServiceScopeFactory scopes, TimeProvider clock) : MailSignal(scopes, clock)
{
    public override string Name => "helpdesk_mail_sync_oldest_age_seconds";

    public override string Description => "Longest time since any active mailbox was last synchronised.";

    public override string Unit => "s";

    protected override async Task<double?> ReadAsync(MailDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var oldest = await db.Mailboxes.AsNoTracking().Where(m => m.IsActive)
            .MinAsync(m => (DateTimeOffset?)(m.Sync != null && m.Sync.LastSyncAt != null ? m.Sync.LastSyncAt : m.CreatedAt), ct);
        return oldest is null ? 0 : Math.Max(0, (now - oldest.Value).TotalSeconds);
    }
}

/// <summary>Outbound emails whose outcome needs an operator: Uncertain or Failed (not Bounced; that is the recipient's side).</summary>
internal sealed class OutboundNeedingAttentionSignal(IServiceScopeFactory scopes, TimeProvider clock) : MailSignal(scopes, clock)
{
    public override string Name => "helpdesk_mail_outbound_needing_attention";

    public override string Description => "Outbound emails in state Uncertain or Failed.";

    public override string Unit => "{message}";

    protected override async Task<double?> ReadAsync(MailDbContext db, DateTimeOffset now, CancellationToken ct) =>
        await db.Messages.AsNoTracking().CountAsync(m => m.Direction == MessageDirections.Outbound && (m.State == MessageStates.Uncertain || m.State == MessageStates.Failed), ct);
}
