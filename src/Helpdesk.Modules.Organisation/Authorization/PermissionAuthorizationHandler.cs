using Helpdesk.Modules.Organisation.Persistence;
using Helpdesk.SharedKernel.Authorization;
using Helpdesk.SharedKernel.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Helpdesk.Modules.Organisation.Authorization;

/// <summary>
/// Evaluates <see cref="PermissionRequirement"/> inside the ASP.NET authorization pipeline. Missing membership fails with the
/// not-found reason (mapped to 404), insufficient permission with the forbidden reason (403).
/// </summary>
internal sealed class PermissionAuthorizationHandler(ICurrentUser current, MembershipCache memberships, OrganisationDbContext db)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var definition = PermissionCatalogue.Get(requirement.Permission);

        if (!current.IsAuthenticated)
        {
            context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureReasons.Forbidden));
            return;
        }

        EvaluationOutcome outcome;
        if (definition.Kind == PermissionKind.Platform)
        {
            if (context.Resource is not null)
            {
                throw new InvalidOperationException($"Platform permission '{requirement.Permission}' is evaluated without a department resource.");
            }

            outcome = PermissionEvaluator.Evaluate(definition, current.IsPlatformAdmin, null, false);
        }
        else
        {
            if (context.Resource is not DepartmentResource resource)
            {
                throw new InvalidOperationException($"Permission '{requirement.Permission}' requires a department resource.");
            }

            var roles = await memberships.GetRolesAsync(CancellationToken.None);
            roles.TryGetValue(resource.DepartmentId, out var role);
            var exists = role is not null
                || (current.IsPlatformAdmin && definition.Kind == PermissionKind.Administrative
                    && await db.Departments.AnyAsync(d => d.Id == resource.DepartmentId));
            outcome = PermissionEvaluator.Evaluate(definition, current.IsPlatformAdmin, role, exists);
        }

        switch (outcome)
        {
            case EvaluationOutcome.Allowed:
                context.Succeed(requirement);
                break;
            case EvaluationOutcome.NotFound:
                context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureReasons.NotFound));
                break;
            default:
                context.Fail(new AuthorizationFailureReason(this, AuthorizationFailureReasons.Forbidden));
                break;
        }
    }
}
