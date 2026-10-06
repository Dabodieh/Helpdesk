using Helpdesk.Modules.Ticketing.Automation;
using Helpdesk.Modules.Ticketing.Services;
using Helpdesk.Modules.Ticketing.Sla;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Helpdesk.Modules.Ticketing.Endpoints;

/// <summary>
/// Phase 5 routes (docs/api/PHASE5_API.md). Configuration endpoints require an administrative permission of the department in the
/// route (DepartmentAdmin; 404 for departments the caller cannot see, 403 without the permission). The per-ticket SLA read uses the
/// normal ticket resource permission.
/// </summary>
internal static class ServiceManagementEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        MapBusinessHours(app);
        MapSlaPolicies(app);
        MapAutomation(app);

        app.MapGet("/api/tickets/{ticket}/sla", async (string ticket, SlaReadService sla, CancellationToken ct) =>
                await sla.GetAsync(ticket, ct) is { } dto ? Results.Ok(dto) : Results.NoContent())
            .RequireResourcePermission<TicketResolver>(PermissionCodes.TicketsRead, "ticket")
            .WithTags("SLA");
    }

    private static void MapBusinessHours(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/departments/{departmentId:guid}/business-hours").WithTags("SLA");

        g.MapGet("", async (Guid departmentId, BusinessHoursService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentSlaManage);
        g.MapPost("", async (Guid departmentId, BusinessHoursRequest body, BusinessHoursService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(departmentId, body, ct);
                return TypedResults.Created($"/api/departments/{departmentId}/business-hours/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.DepartmentSlaManage);
        g.MapGet("{id:guid}", async (Guid departmentId, Guid id, BusinessHoursService service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(departmentId, id, ct)))
            .RequirePermission(PermissionCodes.DepartmentSlaManage);
        g.MapPatch("{id:guid}", async (Guid departmentId, Guid id, BusinessHoursRequest body, BusinessHoursService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(departmentId, id, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentSlaManage);
        g.MapPost("{id:guid}/due", async (Guid departmentId, Guid id, DueCheckRequest body, BusinessHoursService service, CancellationToken ct) =>
                TypedResults.Ok(await service.CheckDueAsync(departmentId, id, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentSlaManage);
    }

    private static void MapSlaPolicies(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/departments/{departmentId:guid}/sla-policies").WithTags("SLA");

        g.MapGet("", async (Guid departmentId, SlaPolicyService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentSlaManage);
        g.MapPost("", async (Guid departmentId, SlaPolicyRequest body, SlaPolicyService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(departmentId, body, ct);
                return TypedResults.Created($"/api/departments/{departmentId}/sla-policies/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.DepartmentSlaManage);
        g.MapPatch("{id:guid}", async (Guid departmentId, Guid id, SlaPolicyRequest body, SlaPolicyService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(departmentId, id, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentSlaManage);
        g.MapPut("order", async (Guid departmentId, ReorderRequest body, SlaPolicyService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ReorderAsync(departmentId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentSlaManage);
    }

    private static void MapAutomation(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/departments/{departmentId:guid}/automations").WithTags("Automation");

        g.MapGet("", async (Guid departmentId, AutomationAdminService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentAutomationManage);
        g.MapPost("", async (Guid departmentId, RuleRequest body, AutomationAdminService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(departmentId, body, ct);
                return TypedResults.Created($"/api/departments/{departmentId}/automations/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.DepartmentAutomationManage);
        g.MapPatch("{id:guid}", async (Guid departmentId, Guid id, RuleRequest body, AutomationAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(departmentId, id, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentAutomationManage);
        g.MapPut("order/{trigger}", async (Guid departmentId, string trigger, ReorderRequest body, AutomationAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ReorderAsync(departmentId, trigger, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentAutomationManage);
        g.MapGet("history",
                async (Guid departmentId, Guid? ruleId, string? ticket, string? state, bool? matched, string? cursor, int? limit, AutomationAdminService service, CancellationToken ct) =>
                    TypedResults.Ok(await service.HistoryAsync(departmentId, ruleId, ticket, state, matched, cursor, limit, ct)))
            .RequirePermission(PermissionCodes.DepartmentAutomationManage);

        // Dry run: reads only. The ticket is resolved through the caller's own ticket scope (tickets.read), so it cannot reach other departments.
        g.MapPost("test", async (Guid departmentId, RuleTestRequest body, AutomationAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.TestAsync(departmentId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentAutomationManage);
    }
}
