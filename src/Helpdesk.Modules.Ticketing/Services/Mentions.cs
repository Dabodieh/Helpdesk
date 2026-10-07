using Helpdesk.Modules.Identity.Contracts;
using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;

namespace Helpdesk.Modules.Ticketing.Services;

/// <summary>
/// Who may be @mentioned in an internal note of a department: an active user who, by department membership, may read tickets AND internal notes there. That is the
/// same rule the notification uses when it is created and again when it is listed, so a mention can never be the way a note reaches someone. It grants nothing.
/// </summary>
internal sealed class MentionService(TicketResolver resolver, IAuthorizer authorizer, IOrganisationDirectory organisation, IUserDirectory users, ICurrentUser current)
{
    public const int MaxMentionsPerNote = 10;
    public const int MaxResults = 10;
    public const int MinQuery = 2;

    /// <summary>The picker: eligible colleagues of the ticket's department whose name matches. Needs tickets.note (the right to write the note).</summary>
    public async Task<IReadOnlyList<MentionDto>> SearchAsync(string route, string? q, CancellationToken ct)
    {
        var key = await resolver.FindAsync(route, ct) ?? throw new NotFoundException();
        await authorizer.RequireAsync(PermissionCodes.TicketsNote, key.DepartmentId, ct);
        var term = q?.Trim() ?? "";
        if (term.Length < MinQuery)
        {
            throw new RequestValidationException("q", $"q must be at least {MinQuery} characters.");
        }

        var people = await users.GetManyAsync([.. await EligibleIdsAsync(key.DepartmentId, ct)], ct);
        return [.. people.Values.Where(p => p.IsActive && p.Id != current.UserId && p.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).Take(MaxResults).Select(p => new MentionDto(p.Id, p.DisplayName))];
    }

    /// <summary>
    /// Validates the mentions of a note being posted: internal notes only, distinct, bounded, and every user eligible NOW. Returns the distinct ids (a mention of
    /// oneself is kept as an association but never notifies). The error never says whether a foreign user exists.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ValidateAsync(Guid departmentId, bool isInternalNote, IReadOnlyList<Guid>? requested, CancellationToken ct)
    {
        var ids = (requested ?? []).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        if (!isInternalNote)
        {
            throw new RequestValidationException("mentionUserIds", "Only internal notes can mention colleagues.");
        }

        if (ids.Length > MaxMentionsPerNote)
        {
            throw new RequestValidationException("mentionUserIds", $"A note can mention at most {MaxMentionsPerNote} colleagues.");
        }

        var eligible = (await EligibleIdsAsync(departmentId, ct)).ToHashSet();
        var found = await users.GetManyAsync(ids, ct);
        if (ids.Any(id => !eligible.Contains(id) || !found.TryGetValue(id, out var u) || !u.IsActive))
        {
            throw new RequestValidationException("mentionUserIds", "Choose colleagues who can read internal notes in this department.");
        }

        return ids;
    }

    private async Task<IReadOnlyCollection<Guid>> EligibleIdsAsync(Guid departmentId, CancellationToken ct)
    {
        var readers = (await organisation.GetUserIdsWithPermissionAsync(departmentId, PermissionCodes.TicketsRead, ct)).ToHashSet();
        return [.. (await organisation.GetUserIdsWithPermissionAsync(departmentId, PermissionCodes.TicketsNotesRead, ct)).Where(readers.Contains)];
    }
}
