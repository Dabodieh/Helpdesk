using Helpdesk.Modules.Organisation.Contracts;
using Helpdesk.Modules.Ticketing.Domain;
using Helpdesk.Modules.Ticketing.Persistence;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Helpdesk.Modules.Ticketing.Portal;

/// <summary>
/// A requester-facing entry in the service catalogue: a plain-language name and description that POINTS AT one existing request form of the same department.
/// It is navigation only: the form stays authoritative for department, audience, fields, validation, ticket type and routing, and a topic is usable exactly when its form is.
/// </summary>
internal sealed class HelpTopic
{
    public Guid Id { get; set; }

    public Guid DepartmentId { get; set; }

    public Guid RequestFormId { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public uint Version { get; set; }
}

internal sealed record HelpTopicAdminDto(
    Guid Id, Guid DepartmentId, string Name, string? Description, bool IsActive, int Order, Guid FormId, string FormName, bool FormAvailable, string Version);

internal sealed record HelpTopicRequest(string? Name, string? Description, bool? IsActive, Guid? FormId, string? Version);

/// <summary>Help topic administration (permission department.forms.manage on the department route; every query names the department). The form is only chosen, never copied.</summary>
internal sealed class HelpTopicsAdminService(TicketingDbContext db, TicketingAudit audit, TimeProvider clock)
{
    public const int MaxTopics = 50;
    private const string TopicObject = "help_topic";

    public async Task<IReadOnlyList<HelpTopicAdminDto>> ListAsync(Guid departmentId, CancellationToken ct)
    {
        var rows = await db.HelpTopics.AsNoTracking().Where(t => t.DepartmentId == departmentId).OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Join(db.RequestForms.AsNoTracking(), t => t.RequestFormId, f => f.Id, (t, f) => new { Topic = t, Form = f }).ToListAsync(ct);
        return [.. rows.Select(r => ToDto(r.Topic, r.Form))];
    }

    public async Task<HelpTopicAdminDto> CreateAsync(Guid departmentId, HelpTopicRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var (name, description) = Validate(errors, request, creating: true);
        if (request.FormId is null)
        {
            errors["formId"] = ["Choose the request form this topic opens."];
        }

        TicketValidation.ThrowIfAny(errors);
        var form = await FormOfAsync(departmentId, request.FormId!.Value, ct);
        if (await db.HelpTopics.CountAsync(t => t.DepartmentId == departmentId, ct) >= MaxTopics)
        {
            throw new ConflictException($"A department can keep at most {MaxTopics} help topics.", ProblemCodes.InvalidState);
        }

        var now = ClockExtensions.GetUtcNowMicroseconds(clock);
        var last = await db.HelpTopics.Where(t => t.DepartmentId == departmentId).MaxAsync(t => (int?)t.SortOrder, ct) ?? 0;
        var topic = new HelpTopic
        {
            Id = Guid.CreateVersion7(), DepartmentId = departmentId, RequestFormId = form.Id, Name = name!, Description = description, IsActive = request.IsActive ?? true,
            SortOrder = last + 10, CreatedAt = now, UpdatedAt = now,
        };
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.HelpTopics.Add(topic);
        await SaveAsync(ct);
        await audit.WriteAsync(TicketAuditActions.HelpTopicCreated, TopicObject, topic.Id, departmentId, null, new { name = topic.Name, formId = topic.RequestFormId, isActive = topic.IsActive }, ct);
        await tx.CommitAsync(ct);
        return ToDto(topic, form);
    }

    public async Task<HelpTopicAdminDto> UpdateAsync(Guid departmentId, Guid topicId, HelpTopicRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = TicketValidation.Version(errors, request.Version);
        var (name, description) = Validate(errors, request, creating: false);
        TicketValidation.ThrowIfAny(errors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var topic = await db.HelpTopics.SingleOrDefaultAsync(t => t.Id == topicId && t.DepartmentId == departmentId, ct) ?? throw new NotFoundException();
        if (topic.Version != version)
        {
            throw new ConflictException("The topic was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }

        db.Entry(topic).Property(t => t.Version).OriginalValue = version;
        var previousForm = topic.RequestFormId;
        if (request.FormId is { } formId && formId != topic.RequestFormId)
        {
            topic.RequestFormId = (await FormOfAsync(departmentId, formId, ct)).Id;
        }

        topic.Name = name ?? topic.Name;
        if (request.Description is not null)
        {
            topic.Description = description;
        }

        topic.IsActive = request.IsActive ?? topic.IsActive;
        topic.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
        await SaveAsync(ct);
        await audit.WriteAsync(TicketAuditActions.HelpTopicUpdated, TopicObject, topic.Id, departmentId, previousForm == topic.RequestFormId ? null : new { formId = previousForm },
            new { name = topic.Name, formId = topic.RequestFormId, isActive = topic.IsActive }, ct);
        await tx.CommitAsync(ct);
        return (await ListAsync(departmentId, ct)).Single(t => t.Id == topic.Id);
    }

    public async Task<IReadOnlyList<HelpTopicAdminDto>> ReorderAsync(Guid departmentId, Sla.ReorderRequest request, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.HelpTopics.Where(t => t.DepartmentId == departmentId).ToListAsync(ct);
        var ids = request.Ids ?? [];
        if (ids.Count != rows.Count || ids.Distinct().Count() != ids.Count || !ids.All(i => rows.Any(r => r.Id == i)))
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["ids"] = ["List every help topic of the department exactly once."] });
        }

        for (var i = 0; i < ids.Count; i++)
        {
            var row = rows.Single(r => r.Id == ids[i]);
            if (row.SortOrder != (i + 1) * 10)
            {
                row.SortOrder = (i + 1) * 10;
                row.UpdatedAt = ClockExtensions.GetUtcNowMicroseconds(clock);
            }
        }

        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(TicketAuditActions.HelpTopicUpdated, TopicObject, departmentId, departmentId, null, new { reordered = ids.Count }, ct);
        await tx.CommitAsync(ct);
        return await ListAsync(departmentId, ct);
    }

    private async Task<RequestForm> FormOfAsync(Guid departmentId, Guid formId, CancellationToken ct) =>
        await db.RequestForms.AsNoTracking().SingleOrDefaultAsync(f => f.Id == formId && f.DepartmentId == departmentId, ct)
        ?? throw new RequestValidationException("formId", "Choose a request form of this department.");

    private static (string? Name, string? Description) Validate(Dictionary<string, string[]> errors, HelpTopicRequest r, bool creating)
    {
        string? name = null;
        if (r.Name is not null || creating)
        {
            name = r.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 100)
            {
                errors["name"] = ["Name must be 1 to 100 characters."];
                name = null;
            }
        }

        var description = r.Description?.Trim();
        if (description is { Length: > 300 })
        {
            errors["description"] = ["Description is at most 300 characters."];
        }

        return (name, string.IsNullOrEmpty(description) ? null : description);
    }

    /// <summary>"Available" here only tells the administrator whether the form is active and offered; whether a particular requester may use it is the form's audience, decided per request.</summary>
    private static HelpTopicAdminDto ToDto(HelpTopic t, RequestForm f) =>
        new(t.Id, t.DepartmentId, t.Name, t.Description, t.IsActive, t.SortOrder, f.Id, f.Name, f.IsActive && f.VisibleToRequesters, ConcurrencyVersion.ToToken(t.Version));

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The item was changed elsewhere. Reload and retry.", ProblemCodes.VersionConflict);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("A help topic with this name already exists.", ProblemCodes.Duplicate);
        }
    }
}

internal sealed record PortalHelpTopicDto(Guid Id, string Name, string? Description, string Department, Guid FormId);

/// <summary>
/// The catalogue as a requester sees it. A topic exists for the caller ONLY when it is active AND its request form is usable by the caller right now
/// (<see cref="PortalFormAccess"/>, the same single audience decision as the form itself) and its department is active. Anything else is a plain 404/absent:
/// a topic can never widen what a form allows, and no form or routing detail beyond the form id needed to open it is returned.
/// </summary>
internal sealed class PortalHelpTopicsService(TicketingDbContext db, PortalFormAccess access, IOrganisationDirectory organisation)
{
    public async Task<IReadOnlyList<PortalHelpTopicDto>> ListAsync(CancellationToken ct)
    {
        var rows = await db.HelpTopics.AsNoTracking().Where(t => t.IsActive)
            .Join(access.Usable().AsNoTracking(), t => t.RequestFormId, f => f.Id, (t, f) => new { Topic = t, FormId = f.Id })
            .OrderBy(x => x.Topic.SortOrder).ThenBy(x => x.Topic.Name).Take(500).ToListAsync(ct);
        var departments = await organisation.GetDepartmentsAsync([.. rows.Select(r => r.Topic.DepartmentId).Distinct()], ct);
        var withSender = await access.DepartmentsWithSenderAsync(rows.Select(r => r.Topic.DepartmentId), ct); // a topic leads to a form, which needs a usable default mailbox (D22)
        return [.. rows.Where(r => withSender.Contains(r.Topic.DepartmentId) && departments.TryGetValue(r.Topic.DepartmentId, out var d) && d.IsActive)
            .Select(r => new PortalHelpTopicDto(r.Topic.Id, r.Topic.Name, r.Topic.Description, departments[r.Topic.DepartmentId].Name, r.FormId))
            .OrderBy(t => t.Department, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<PortalHelpTopicDto> GetAsync(Guid id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(t => t.Id == id) ?? throw new NotFoundException();
}
