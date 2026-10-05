using Helpdesk.Modules.Organisation.Authorization;
using Helpdesk.Modules.Organisation.Domain;
using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Database;
using Helpdesk.SharedKernel.Errors;
using Helpdesk.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Organisation.Services;

internal sealed record DepartmentDto(Guid Id, string Key, string Name, string? Description, bool IsActive, string? MyRole, string Version);

internal sealed record CreateDepartmentRequest(string? Key, string? Name, string? Description);

internal sealed record UpdateDepartmentRequest(string? Version, string? Name, string? Description, bool? IsActive);

internal sealed class DepartmentService(
    OrganisationDbContext db,
    OrganisationAudit audit,
    ICurrentUser current,
    IAuthorizer authorizer,
    MembershipCache memberships,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<DepartmentDto>> ListAsync(CancellationToken ct)
    {
        var roles = await memberships.GetRolesAsync(ct);
        IQueryable<Department> query = db.Departments.AsNoTracking();
        if (!current.IsPlatformAdmin)
        {
            var ids = roles.Keys.ToArray();
            query = query.Where(d => ids.Contains(d.Id));
        }

        var rows = await query.OrderBy(d => d.Name).ThenBy(d => d.Id).ToListAsync(ct);
        return rows.Select(d => ToDto(d, roles)).ToList();
    }

    public async Task<DepartmentDto> GetAsync(Guid id, CancellationToken ct)
    {
        var department = await db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException();
        return ToDto(department, await memberships.GetRolesAsync(ct));
    }

    public async Task<DepartmentDto> CreateAsync(CreateDepartmentRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var key = request.Key?.Trim();
        if (!Validation.IsValidKey(key))
        {
            errors["key"] = ["key must match ^[A-Z][A-Z0-9]{1,9}$."];
        }

        Validation.Name(errors, "name", request.Name);
        Validation.Description(errors, request.Description);
        Validation.ThrowIfAny(errors);

        var now = clock.GetUtcNow();
        var department = new Department
        {
            Id = Guid.CreateVersion7(),
            Key = key!,
            Name = request.Name!.Trim(),
            Description = Validation.NormaliseDescription(request.Description),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Departments.Add(department);
        await SaveAsync(ct);
        await audit.WriteAsync("department.created", "department", department.Id, department.Id, null,
            new { key = department.Key, name = department.Name, description = department.Description, isActive = true }, ct);
        await tx.CommitAsync(ct);
        return ToDto(department, await memberships.GetRolesAsync(ct));
    }

    public async Task<DepartmentDto> UpdateAsync(Guid id, UpdateDepartmentRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var version = Validation.Version(errors, request.Version);
        if (request.Name is not null)
        {
            Validation.Name(errors, "name", request.Name);
        }

        Validation.Description(errors, request.Description);
        if (request.Name is null && request.Description is null && request.IsActive is null)
        {
            errors["body"] = ["Provide at least one of name, description, isActive."];
        }

        Validation.ThrowIfAny(errors);

        if (request.Name is not null || request.Description is not null)
        {
            await authorizer.RequireAsync(PermissionCodes.DepartmentManage, id, ct);
        }

        if (request.IsActive is not null)
        {
            await authorizer.RequireAsync(PermissionCodes.PlatformDepartmentsManage, null, ct);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var department = await db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException();
        if (department.Version != version)
        {
            throw new ConflictException("The department was changed by someone else. Reload and retry.");
        }

        db.Entry(department).Property(d => d.Version).OriginalValue = version;

        var previous = new Dictionary<string, object?>();
        var next = new Dictionary<string, object?>();
        var newName = request.Name?.Trim();
        if (newName is not null && newName != department.Name)
        {
            previous["name"] = department.Name;
            next["name"] = newName;
            department.Name = newName;
        }

        if (request.Description is not null)
        {
            var newDescription = Validation.NormaliseDescription(request.Description);
            if (newDescription != department.Description)
            {
                previous["description"] = department.Description;
                next["description"] = newDescription;
                department.Description = newDescription;
            }
        }

        var activeChanged = request.IsActive is { } active && active != department.IsActive;
        var wasActive = department.IsActive;
        if (activeChanged)
        {
            department.IsActive = request.IsActive!.Value;
        }

        if (previous.Count > 0 || activeChanged)
        {
            department.UpdatedAt = clock.GetUtcNow();
            await SaveAsync(ct);
            if (previous.Count > 0)
            {
                await audit.WriteAsync("department.updated", "department", department.Id, department.Id, previous, next, ct);
            }

            if (activeChanged)
            {
                await audit.WriteAsync(department.IsActive ? "department.activated" : "department.deactivated", "department",
                    department.Id, department.Id, new { isActive = wasActive }, new { isActive = department.IsActive }, ct);
            }
        }

        await tx.CommitAsync(ct);
        return ToDto(department, await memberships.GetRolesAsync(ct));
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The department was changed by someone else. Reload and retry.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, out var constraint))
        {
            throw new ConflictException(constraint switch
            {
                "ux_departments_key_lower" => "A department with this key already exists.",
                "ux_departments_name_lower" => "A department with this name already exists.",
                _ => "A department with these values already exists.",
            });
        }
    }

    internal static DepartmentDto ToDto(Department d, IReadOnlyDictionary<Guid, string> roles) =>
        new(d.Id, d.Key, d.Name, d.Description, d.IsActive, roles.TryGetValue(d.Id, out var role) ? role : null, ConcurrencyVersion.ToToken(d.Version));
}
