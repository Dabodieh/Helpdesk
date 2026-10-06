using Helpdesk.Modules.Knowledge.Services;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Helpdesk.Modules.Knowledge.Endpoints;

/// <summary>
/// Reading is for every signed-in employee (the service restricts to published content and the article's audience); managing needs
/// <c>department.knowledge.manage</c> in the department of the route. Draft articles exist only on the management routes.
/// </summary>
internal static class KnowledgeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var portal = app.MapGroup("/api/portal/knowledge").WithTags("Knowledge");
        portal.MapGet("categories", async (KnowledgePortalService s, CancellationToken ct) => TypedResults.Ok(await s.CategoriesAsync(ct))).RequireAuthenticatedOnly();
        portal.MapGet("articles", async (string? q, Guid? categoryId, Guid? departmentId, string? cursor, int? limit, KnowledgePortalService s, CancellationToken ct) =>
                TypedResults.Ok(await s.SearchAsync(q, categoryId, departmentId, cursor, limit, ct)))
            .RequireAuthenticatedOnly();
        portal.MapGet("articles/{slug}", async (string slug, KnowledgePortalService s, CancellationToken ct) => TypedResults.Ok(await s.GetAsync(slug, ct))).RequireAuthenticatedOnly();
        portal.MapPut("articles/{slug}/feedback", async (string slug, FeedbackRequest body, KnowledgePortalService s, CancellationToken ct) => TypedResults.Ok(await s.SetFeedbackAsync(slug, body, ct)))
            .RequireAuthenticatedOnly();

        var admin = app.MapGroup("/api/departments/{departmentId:guid}/knowledge").WithTags("Knowledge administration");
        admin.MapGet("categories", async (Guid departmentId, KnowledgeAdminService s, CancellationToken ct) => TypedResults.Ok(await s.ListCategoriesAsync(departmentId, ct)))
            .RequirePermission(PermissionCodes.DepartmentKnowledgeManage);
        admin.MapPost("categories", async (Guid departmentId, CategoryRequest body, KnowledgeAdminService s, CancellationToken ct) =>
                TypedResults.Created($"/api/departments/{departmentId}/knowledge/categories", await s.CreateCategoryAsync(departmentId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentKnowledgeManage);
        admin.MapPatch("categories/{id:guid}", async (Guid departmentId, Guid id, CategoryRequest body, KnowledgeAdminService s, CancellationToken ct) =>
                TypedResults.Ok(await s.UpdateCategoryAsync(departmentId, id, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentKnowledgeManage);
        admin.MapPut("categories/order", async (Guid departmentId, IdsRequest body, KnowledgeAdminService s, CancellationToken ct) => TypedResults.Ok(await s.ReorderCategoriesAsync(departmentId, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentKnowledgeManage);

        admin.MapGet("articles", async (Guid departmentId, string? status, Guid? categoryId, string? q, int? page, KnowledgeAdminService s, CancellationToken ct) =>
                TypedResults.Ok(await s.ListArticlesAsync(departmentId, status, categoryId, q, page, ct)))
            .RequirePermission(PermissionCodes.DepartmentKnowledgeManage);
        admin.MapPost("articles", async (Guid departmentId, ArticleRequest body, KnowledgeAdminService s, CancellationToken ct) =>
            {
                var created = await s.CreateArticleAsync(departmentId, body, ct);
                return TypedResults.Created($"/api/departments/{departmentId}/knowledge/articles/{created.Id}", created);
            })
            .RequirePermission(PermissionCodes.DepartmentKnowledgeManage);
        admin.MapGet("articles/{id:guid}", async (Guid departmentId, Guid id, KnowledgeAdminService s, CancellationToken ct) => TypedResults.Ok(await s.GetArticleAsync(departmentId, id, ct)))
            .RequirePermission(PermissionCodes.DepartmentKnowledgeManage);
        admin.MapPatch("articles/{id:guid}", async (Guid departmentId, Guid id, ArticleRequest body, KnowledgeAdminService s, CancellationToken ct) => TypedResults.Ok(await s.SaveDraftAsync(departmentId, id, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentKnowledgeManage);
        admin.MapPost("articles/{id:guid}/publish", async (Guid departmentId, Guid id, VersionRequest body, KnowledgeAdminService s, CancellationToken ct) => TypedResults.Ok(await s.PublishAsync(departmentId, id, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentKnowledgeManage);
        admin.MapPost("articles/{id:guid}/archive", async (Guid departmentId, Guid id, VersionRequest body, KnowledgeAdminService s, CancellationToken ct) => TypedResults.Ok(await s.ArchiveAsync(departmentId, id, body, ct)))
            .RequirePermission(PermissionCodes.DepartmentKnowledgeManage);
    }
}
