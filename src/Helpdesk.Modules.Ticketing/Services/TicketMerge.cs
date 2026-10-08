using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>Stable error codes of merge and link operations (the HTTP problem <c>code</c>).</summary>
internal static class TicketMergeCodes
{
    /// <summary>The ticket was merged into another one and is read-only.</summary>
    public const string Merged = "ticket_merged";

    /// <summary>Merges and links are limited to tickets of one department.</summary>
    public const string CrossDepartment = "cross_department";

    public const string InvalidLink = "invalid_link";
}

internal sealed record MergeSourceRequest(Guid? TicketId, string? Version);

/// <summary>The route ticket is the CANONICAL ticket (the one that remains); <c>Sources</c> are merged into it.</summary>
internal sealed record MergeTicketsRequest(string? Version, MergeSourceRequest[]? Sources);

internal sealed record LinkCandidateDto(Guid Id, string Reference, string Subject, string Status, string RequesterName, string RequesterEmail, string Version);

/// <summary>Relationship as seen from the ticket asked about: Related, DuplicateOf, DuplicatedBy, Parent (the other ticket is the parent), Child.</summary>
internal sealed record TicketLinkDto(Guid Id, string Relationship, Guid TicketId, string Reference, string Subject, string Status, string? MergedInto);

internal sealed record AddTicketLinkRequest(Guid? TicketId, string? Type);

/// <summary>
/// Merge duplicate tickets of one department into an explicit canonical ticket. Nothing is rewritten or copied: sources keep their rows,
/// messages and attachments (provenance), and the canonical conversation is the union read at query time. One transaction, row locks on
/// every involved ticket in id order, optimistic versions on all of them.
/// </summary>
internal sealed class TicketMergeService(
    TicketingDbContext db,
    TicketingAudit audit,
    TicketProjection projection,
    TicketResolver resolver,
    IAuthorizer authorizer,
    ICurrentUser current,
    SlaEngine sla,
    TicketFollowerService followers,
    TicketParties parties,
    ITicketEvents events,
    TimeProvider clock)
{
    public const int MaxSources = 20;
    public const int CandidateLimit = 10;

    public async Task<TicketDetailDto> MergeAsync(string route, MergeTicketsRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var canonicalVersion = TicketValidation.Version(errors, request.Version);
        var sources = request.Sources ?? [];
        if (sources.Length is 0 or > MaxSources || sources.Any(s => s.TicketId is null))
        {
            errors["sources"] = [$"Provide between 1 and {MaxSources} tickets to merge."];
        }

        var wanted = new Dictionary<Guid, uint>();
        foreach (var source in sources.Where(s => s.TicketId is not null))
        {
            var sourceErrors = new Dictionary<string, string[]>();
            var version = TicketValidation.Version(sourceErrors, source.Version);
            if (sourceErrors.Count > 0)
            {
                errors["sources"] = ["Every source needs a valid version."];
            }
            else if (!wanted.TryAdd(source.TicketId!.Value, version))
            {
                errors["sources"] = ["A ticket can only be listed once."];
            }
        }

        TicketValidation.ThrowIfAny(errors);

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        await authorizer.RequireAsync(PermissionCodes.TicketsUpdate, key.DepartmentId, ct);
        if (wanted.ContainsKey(key.Id))
        {
            throw new RequestValidationException("sources", "A ticket cannot be merged into itself.");
        }

        // Inaccessible and missing tickets are the same 404 (department scope filter). An accessible ticket of another department is a structured 409.
        var ids = wanted.Keys.ToArray();
        var visible = await db.Tickets.AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.DepartmentId }).ToListAsync(ct);
        if (visible.Count != ids.Length)
        {
            throw new NotFoundException();
        }

        if (visible.Any(t => t.DepartmentId != key.DepartmentId))
        {
            throw new ConflictException("Tickets can only be merged within one department.", TicketMergeCodes.CrossDepartment);
        }

        var now = clock.GetUtcNowMicroseconds();
        var lockIds = ids.Append(key.Id).Order().ToArray();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM ticketing.tickets WHERE id = ANY({lockIds}) ORDER BY id FOR UPDATE", ct);
        var canonical = await db.Tickets.Include(t => t.Requester).SingleOrDefaultAsync(t => t.Id == key.Id, ct) ?? throw new NotFoundException();
        if (canonical.IsMerged)
        {
            throw TicketService.MergedConflict();
        }

        var rows = await db.Tickets.Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var pending = new List<Ticket>();
        foreach (var source in rows.OrderBy(t => t.Number))
        {
            if (source.MergedIntoTicketId == canonical.Id)
            {
                continue; // already merged into this ticket (a repeated request): nothing to do
            }

            if (source.IsMerged)
            {
                throw new ConflictException($"{source.Reference} was already merged into another ticket.", TicketMergeCodes.Merged);
            }

            if (source.Version != wanted[source.Id])
            {
                throw TicketService.VersionConflict();
            }

            pending.Add(source);
        }

        if (pending.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return await projection.DetailAsync(canonical, canonical.Requester, ct);
        }

        if (canonical.Version != canonicalVersion)
        {
            throw TicketService.VersionConflict();
        }

        db.Entry(canonical).Property(t => t.Version).OriginalValue = canonicalVersion;
        foreach (var source in pending)
        {
            db.Entry(source).Property(t => t.Version).OriginalValue = wanted[source.Id];
            var sourceId = source.Id;

            // Tickets earlier merged into this source now point at the final canonical ticket (no chains).
            await db.Tickets.Where(t => t.MergedIntoTicketId == sourceId).ExecuteUpdateAsync(s => s.SetProperty(t => t.MergedIntoTicketId, canonical.Id), ct);

            // A relationship between the two merged tickets is meaningless now.
            var canonicalId = canonical.Id;
            await db.TicketLinks.Where(l => (l.SourceTicketId == canonicalId && l.TargetTicketId == sourceId) || (l.SourceTicketId == sourceId && l.TargetTicketId == canonicalId)).ExecuteDeleteAsync(ct);

            source.MergedIntoTicketId = canonical.Id;
            source.MergedAt = now;
            source.MergedByUserId = current.UserId;
            source.UpdatedAt = now;
        }

        canonical.UpdatedAt = now; // visible activity for agents only: requesters see nothing of a merge (LastPublicActivityAt is untouched)
        await SaveAsync(ct);

        // D9: the requester of every merged ticket stays in the conversation as a protected participant (copied on replies, shown the canonical request, never removable),
        // so a merge across different requesters cannot orphan anyone.
        foreach (var source in pending)
        {
            var sourceRequester = await db.Requesters.AsNoTracking().SingleAsync(r => r.Id == source.RequesterId, ct);
            await parties.ProtectRequesterAsync(canonical.DepartmentId, canonical.Id, canonical.RequesterId, sourceRequester, now, ct);
            await parties.CarryProtectedAsync(canonical.DepartmentId, source.Id, canonical.Id, canonical.RequesterId, now, ct);
        }

        var transferred = await followers.TransferAsync(pending.Select(s => s.Id).ToArray(), canonical, ct);
        foreach (var source in pending)
        {
            await sla.StopOnMergeAsync(source, now, ct);
            await audit.WriteAsync(TicketAuditActions.MergedInto, TicketingAudit.TicketObject, source.Id, source.DepartmentId, null,
                new { mergedInto = new { id = canonical.Id, reference = canonical.Reference } }, ct);
        }

        await audit.WriteAsync(TicketAuditActions.MergedIn, TicketingAudit.TicketObject, canonical.Id, canonical.DepartmentId, null,
            new { merged = pending.Select(s => new { id = s.Id, reference = s.Reference }).ToArray(), followersTransferred = transferred }, ct);
        await tx.CommitAsync(ct);

        await events.PublishAsync(canonical.DepartmentId, canonical.Id, canonical.Reference, TicketEventKinds.Updated, current.UserId, ct);
        foreach (var source in pending)
        {
            await events.PublishAsync(source.DepartmentId, source.Id, source.Reference, TicketEventKinds.Updated, current.UserId, ct);
        }

        return await projection.DetailAsync(canonical, canonical.Requester, ct);
    }

    /// <summary>
    /// Tickets of the same department that can be merged into or linked to the route ticket: not itself, not already merged. Visibility is the
    /// department scope filter; reference prefix or subject match; one small page, never the whole department.
    /// </summary>
    public async Task<IReadOnlyList<LinkCandidateDto>> CandidatesAsync(string route, string? q, CancellationToken ct)
    {
        var search = q?.Trim();
        if (search is not { Length: >= 2 and <= 100 } || TicketValidation.HasNul(search))
        {
            throw new RequestValidationException("q", "q must be between 2 and 100 characters.");
        }

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var escaped = TicketValidation.EscapeLike(search);
        var prefix = escaped + "%";
        var contains = "%" + escaped + "%";
        var rows = await db.Tickets.AsNoTracking()
            .Where(t => t.DepartmentId == key.DepartmentId && t.Id != key.Id && t.MergedIntoTicketId == null)
            .Where(t => EF.Functions.ILike(t.Reference, prefix, "\\") || EF.Functions.ILike(t.Subject, contains, "\\"))
            .OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id)
            .Take(CandidateLimit)
            .Select(t => new { t.Id, t.Reference, t.Subject, t.Status, t.Requester.DisplayName, t.Requester.Email, t.Version })
            .ToListAsync(ct);
        return [.. rows.Select(t => new LinkCandidateDto(t.Id, t.Reference, t.Subject, t.Status.ToString(), t.DisplayName, t.Email, ConcurrencyVersion.ToToken(t.Version)))];
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw TicketService.VersionConflict();
        }
    }
}

/// <summary>Informational relationships between tickets of one department: Related, Duplicate of, Parent, Child. Never merges, never changes either ticket.</summary>
internal sealed class TicketLinkService(
    TicketingDbContext db,
    TicketingAudit audit,
    TicketResolver resolver,
    ICurrentUser current,
    TimeProvider clock)
{
    public const int MaxDepth = 50;

    /// <summary>The wire names of the relationship asked for, relative to the route ticket.</summary>
    public static readonly string[] Types = ["Related", "DuplicateOf", "Parent", "Child"];

    public async Task<IReadOnlyList<TicketLinkDto>> ListAsync(string route, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var links = await db.TicketLinks.AsNoTracking().Where(l => l.SourceTicketId == key.Id || l.TargetTicketId == key.Id)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).ToListAsync(ct);
        if (links.Count == 0)
        {
            return [];
        }

        var otherIds = links.Select(l => l.SourceTicketId == key.Id ? l.TargetTicketId : l.SourceTicketId).Distinct().ToArray();

        // Through the scoped set: a ticket the caller cannot read is simply absent.
        var others = await db.Tickets.AsNoTracking().Where(t => otherIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Reference, t.Subject, t.Status, t.MergedIntoTicketId }).ToDictionaryAsync(t => t.Id, ct);
        var mergedTargets = others.Values.Where(t => t.MergedIntoTicketId is not null).Select(t => t.MergedIntoTicketId!.Value).Distinct().ToArray();
        var mergedRefs = mergedTargets.Length == 0
            ? []
            : await db.Tickets.AsNoTracking().Where(t => mergedTargets.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Reference, ct);

        var items = new List<TicketLinkDto>();
        foreach (var link in links)
        {
            var thisIsSource = link.SourceTicketId == key.Id;
            var otherId = thisIsSource ? link.TargetTicketId : link.SourceTicketId;
            if (!others.TryGetValue(otherId, out var other))
            {
                continue;
            }

            var relationship = link.Type switch
            {
                TicketLinkTypes.DuplicateOf => thisIsSource ? "DuplicateOf" : "DuplicatedBy",
                TicketLinkTypes.ParentOf => thisIsSource ? "Child" : "Parent",
                _ => "Related",
            };
            items.Add(new TicketLinkDto(
                link.Id, relationship, other.Id, other.Reference, other.Subject, other.Status.ToString(),
                other.MergedIntoTicketId is { } m && mergedRefs.TryGetValue(m, out var mergedRef) ? mergedRef : null));
        }

        return items;
    }

    public async Task<TicketLinkDto> AddAsync(string route, AddTicketLinkRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.TicketId is null)
        {
            errors["ticketId"] = ["ticketId is required."];
        }

        var type = Types.FirstOrDefault(t => string.Equals(t, request.Type?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (type is null)
        {
            errors["type"] = [$"type must be one of {string.Join(", ", Types)}."];
        }

        TicketValidation.ThrowIfAny(errors);

        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        var otherId = request.TicketId!.Value;
        if (otherId == key.Id)
        {
            throw new RequestValidationException("ticketId", "A ticket cannot be linked to itself.");
        }

        // The other ticket must be visible to the caller (404 otherwise, like a missing one) and in the same department (409).
        var other = await db.Tickets.AsNoTracking().Where(t => t.Id == otherId).Select(t => new { t.Id, t.DepartmentId, t.Reference }).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException();
        if (other.DepartmentId != key.DepartmentId)
        {
            throw new ConflictException("Tickets can only be linked within one department.", TicketMergeCodes.CrossDepartment);
        }

        // Stored direction: Related is symmetric (smaller id first); DuplicateOf: this is the duplicate; Parent: the other ticket is the parent; Child: this is the parent.
        var (source, target, stored) = type switch
        {
            "DuplicateOf" => (key.Id, other.Id, TicketLinkTypes.DuplicateOf),
            "Parent" => (other.Id, key.Id, TicketLinkTypes.ParentOf),
            "Child" => (key.Id, other.Id, TicketLinkTypes.ParentOf),
            _ => key.Id.CompareTo(other.Id) < 0 ? (key.Id, other.Id, TicketLinkTypes.Related) : (other.Id, key.Id, TicketLinkTypes.Related),
        };

        var lockIds = new[] { key.Id, other.Id }.Order().ToArray();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM ticketing.tickets WHERE id = ANY({lockIds}) ORDER BY id FOR UPDATE", ct);
        var merged = await db.Tickets.AsNoTracking().AnyAsync(t => (t.Id == key.Id || t.Id == other.Id) && t.MergedIntoTicketId != null, ct);
        if (merged)
        {
            throw TicketService.MergedConflict();
        }

        if (await db.TicketLinks.AnyAsync(l => (l.SourceTicketId == source && l.TargetTicketId == target) || (l.SourceTicketId == target && l.TargetTicketId == source), ct))
        {
            throw new ConflictException("These tickets are already linked.", ProblemCodes.Duplicate);
        }

        if (stored != TicketLinkTypes.Related && await ReachesAsync(stored, from: target, to: source, ct))
        {
            throw new ConflictException("This link would create a circular relationship.", TicketMergeCodes.InvalidLink);
        }

        var link = new TicketLink
        {
            Id = Guid.CreateVersion7(),
            DepartmentId = key.DepartmentId,
            SourceTicketId = source,
            TargetTicketId = target,
            Type = stored,
            CreatedByUserId = current.UserId,
            CreatedAt = clock.GetUtcNowMicroseconds(),
        };
        db.TicketLinks.Add(link);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out _))
        {
            throw new ConflictException("These tickets are already linked.", ProblemCodes.Duplicate);
        }

        var keyReference = await db.Tickets.AsNoTracking().Where(t => t.Id == key.Id).Select(t => t.Reference).SingleAsync(ct);
        await audit.WriteAsync(TicketAuditActions.LinkAdded, TicketingAudit.TicketObject, key.Id, key.DepartmentId, null,
            new { linkId = link.Id, type = stored, direction = source == key.Id ? "outgoing" : "incoming", other = new { id = other.Id, reference = other.Reference } }, ct);
        await audit.WriteAsync(TicketAuditActions.LinkAdded, TicketingAudit.TicketObject, other.Id, key.DepartmentId, null,
            new { linkId = link.Id, type = stored, direction = source == other.Id ? "outgoing" : "incoming", other = new { id = key.Id, reference = keyReference } }, ct);
        await tx.CommitAsync(ct);

        var items = await ListAsync(route, ct);
        return items.Single(i => i.Id == link.Id);
    }

    public async Task RemoveAsync(string route, Guid linkId, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var link = await db.TicketLinks.SingleOrDefaultAsync(l => l.Id == linkId && (l.SourceTicketId == key.Id || l.TargetTicketId == key.Id), ct)
            ?? throw new NotFoundException();
        var otherId = link.SourceTicketId == key.Id ? link.TargetTicketId : link.SourceTicketId;
        var refs = await db.Tickets.AsNoTracking().Where(t => t.Id == key.Id || t.Id == otherId).ToDictionaryAsync(t => t.Id, t => t.Reference, ct);
        db.TicketLinks.Remove(link);
        await db.SaveChangesAsync(ct);
        foreach (var id in new[] { key.Id, otherId })
        {
            await audit.WriteAsync(TicketAuditActions.LinkRemoved, TicketingAudit.TicketObject, id, key.DepartmentId, null,
                new { linkId, type = link.Type, other = new { id = id == key.Id ? otherId : key.Id, reference = refs.GetValueOrDefault(id == key.Id ? otherId : key.Id) } }, ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>True when following links of <paramref name="type"/> (source to target) from <paramref name="from"/> reaches <paramref name="to"/>. Bounded.</summary>
    private async Task<bool> ReachesAsync(string type, Guid from, Guid to, CancellationToken ct)
    {
        var seen = new HashSet<Guid> { from };
        var frontier = new[] { from };
        for (var depth = 0; depth < MaxDepth && frontier.Length > 0; depth++)
        {
            var next = await db.TicketLinks.AsNoTracking().Where(l => l.Type == type && frontier.Contains(l.SourceTicketId)).Select(l => l.TargetTicketId).ToArrayAsync(ct);
            if (next.Contains(to))
            {
                return true;
            }

            frontier = [.. next.Where(seen.Add)];
        }

        return frontier.Length > 0; // too deep to prove acyclic: refuse
    }
}
