using System.Globalization;
using System.Text.Json;
using Helpdesk.Modules.Audit.Persistence;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Paging;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Audit.Services;

internal sealed record AuditActorDto(Guid Id, string? DisplayName);

internal sealed record AuditEventDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    AuditActorDto? Actor,
    string Action,
    string ObjectType,
    Guid? ObjectId,
    JsonElement? Previous,
    JsonElement? Next,
    string Source,
    string Category,
    Guid? DepartmentId);

internal sealed record AuditPage(IReadOnlyList<AuditEventDto> Items, string? NextCursor);

internal sealed class AuditQueries(AuditDbContext db)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public Task<AuditPage> ForDepartmentAsync(Guid departmentId, string? cursor, int? limit, CancellationToken ct) =>
        PageAsync(db.Events.Where(e => e.DepartmentId == departmentId && e.Category == Contracts.AuditCategories.Organisation), cursor, limit, ct);

    /// <summary>Platform view: organisation and identity categories only (the only categories that exist in Phase 1; never ticket content).</summary>
    public Task<AuditPage> ForPlatformAsync(string? cursor, int? limit, CancellationToken ct) =>
        PageAsync(db.Events.Where(e => e.Category == Contracts.AuditCategories.Organisation || e.Category == Contracts.AuditCategories.Identity), cursor, limit, ct);

    private static async Task<AuditPage> PageAsync(IQueryable<AuditEvent> source, string? cursor, int? limit, CancellationToken ct)
    {
        var take = limit ?? DefaultLimit;
        if (take is < 1 or > MaxLimit)
        {
            throw new RequestValidationException("limit", $"limit must be between 1 and {MaxLimit}.");
        }

        var query = source.AsNoTracking();
        if (cursor is not null)
        {
            if (!TryDecode(cursor, out var at, out var id))
            {
                throw new RequestValidationException("cursor", "Invalid cursor.");
            }

            query = query.Where(e => e.OccurredAt < at || (e.OccurredAt == at && e.Id.CompareTo(id) < 0));
        }

        var rows = await query.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(take + 1).ToListAsync(ct);
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var next = hasMore ? Encode(rows[^1]) : null;
        return new AuditPage(rows.Select(ToDto).ToList(), next);
    }

    private static string Encode(AuditEvent last) =>
        Cursor.Encode(string.Create(CultureInfo.InvariantCulture, $"{last.OccurredAt.UtcTicks}|{last.Id}"));

    private static bool TryDecode(string cursor, out DateTimeOffset at, out Guid id)
    {
        at = default;
        id = default;
        if (!Cursor.TryDecode(cursor, out var payload))
        {
            return false;
        }

        var parts = payload.Split('|');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < 0 || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParse(parts[1], out id))
        {
            return false;
        }

        at = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }

    private static AuditEventDto ToDto(AuditEvent e) => new(
        e.Id,
        e.OccurredAt,
        e.ActorUserId is { } actor ? new AuditActorDto(actor, e.ActorDisplayName) : null,
        e.Action,
        e.ObjectType,
        e.ObjectId,
        ParseJson(e.Previous),
        ParseJson(e.Next),
        e.Source,
        e.Category,
        e.DepartmentId);

    private static JsonElement? ParseJson(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
}
