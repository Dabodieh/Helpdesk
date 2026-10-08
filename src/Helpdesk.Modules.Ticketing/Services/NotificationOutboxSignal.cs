using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>Age of the oldest notification outbox item still waiting. Growing means the drain job is stuck or one item keeps failing (SRE-005).</summary>
internal sealed class NotificationOutboxAgeSignal(IServiceScopeFactory scopes, TimeProvider clock) : IOperationalSignal
{
    public string Name => "helpdesk_notification_outbox_oldest_age_seconds";

    public string Description => "Age of the oldest unprocessed notification outbox item.";

    public string Unit => "s";

    public async Task<double?> ReadAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var oldest = await db.NotificationOutbox.AsNoTracking().IgnoreDepartmentScope().Where(i => i.ProcessedAt == null).MinAsync(i => (DateTimeOffset?)i.CreatedAt, cancellationToken);
        return oldest is null ? 0 : Math.Max(0, (clock.GetUtcNow() - oldest.Value).TotalSeconds);
    }
}
