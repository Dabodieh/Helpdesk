using System.Text.Json;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary><c>Columns</c> null = the standard set in the standard order; otherwise exactly the listed optional columns in that order (Ref and Subject are always shown).</summary>
internal sealed record QueuePreferencesDto(IReadOnlyList<string>? Columns, string? DefaultQueue, Guid? DefaultViewId);

/// <summary>Replaces the preferences. Set at most one default (a queue or one of the caller's own saved views); null clears.</summary>
internal sealed record QueuePreferencesRequest(IReadOnlyList<string>? Columns, string? DefaultQueue, Guid? DefaultViewId);

/// <summary>
/// The caller's own queue preferences, stored per account (so they survive sign-in and browser changes). Nothing here is department data and nothing grants
/// access: the queue and any saved view still run through the normal list endpoint and its department scope. A default view that was deleted (or is not the
/// caller's) reads back as "no default" instead of failing, so the client falls back to its standard landing queue.
/// </summary>
internal sealed class QueuePreferencesService(TicketingDbContext db, ICurrentUser current, TimeProvider clock)
{
    /// <summary>The optional columns a user may show and order. Ref and Subject are always present.</summary>
    public static readonly string[] SupportedColumns = ["requester", "status", "priority", "sla", "team", "assignee", "updated"];

    public static readonly string[] Queues = ["my", "team", "unassigned", "all", "following"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<QueuePreferencesDto> GetAsync(CancellationToken ct)
    {
        var me = current.UserId;
        var row = await db.UserQueuePreferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == me, ct);
        if (row is null)
        {
            return new QueuePreferencesDto(null, null, null);
        }

        var columns = row.ColumnsJson is null ? null : JsonSerializer.Deserialize<string[]>(row.ColumnsJson, Json)?.Where(c => SupportedColumns.Contains(c)).Distinct().ToArray();
        var view = row.DefaultViewId is { } viewId && await db.SavedViews.AsNoTracking().AnyAsync(v => v.Id == viewId && v.OwnerUserId == me, ct) ? row.DefaultViewId : null;
        var queue = row.DefaultQueue is { } q && Queues.Contains(q) ? q : null;
        return new QueuePreferencesDto(columns, view is null ? queue : null, view);
    }

    public async Task<QueuePreferencesDto> SetAsync(QueuePreferencesRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        string[]? columns = null;
        if (request.Columns is not null)
        {
            columns = [.. request.Columns.Select(c => c.Trim().ToLowerInvariant())];
            if (columns.Any(c => !SupportedColumns.Contains(c)) || columns.Distinct().Count() != columns.Length)
            {
                errors["columns"] = [$"columns must be distinct values of {string.Join(", ", SupportedColumns)}."];
            }
        }

        var queue = request.DefaultQueue?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(queue) && !Queues.Contains(queue))
        {
            errors["defaultQueue"] = [$"defaultQueue must be one of {string.Join(", ", Queues)}."];
        }

        if (!string.IsNullOrEmpty(queue) && request.DefaultViewId is not null)
        {
            errors["defaultQueue"] = ["Choose either a default queue or a default view, not both."];
        }

        var me = current.UserId;
        if (request.DefaultViewId is { } view && !await db.SavedViews.AsNoTracking().AnyAsync(v => v.Id == view && v.OwnerUserId == me, ct))
        {
            errors["defaultViewId"] = ["Choose one of your own saved views."];
        }

        TicketValidation.ThrowIfAny(errors);

        var row = await db.UserQueuePreferences.SingleOrDefaultAsync(p => p.UserId == me, ct);
        if (row is null)
        {
            row = new UserQueuePreference { UserId = me };
            db.UserQueuePreferences.Add(row);
        }

        row.ColumnsJson = columns is null ? null : JsonSerializer.Serialize(columns, Json);
        row.DefaultQueue = string.IsNullOrEmpty(queue) ? null : queue;
        row.DefaultViewId = request.DefaultViewId;
        row.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }
}
