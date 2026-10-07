using Helpdesk.Modules.Knowledge.Endpoints;
using Helpdesk.Modules.Knowledge.Persistence;
using Helpdesk.Modules.Knowledge.Services;
using Helpdesk.SharedKernel.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Modules.Knowledge;

/// <summary>Department knowledge base: categories and articles (draft/published/archived), PostgreSQL full-text search, article feedback.</summary>
public static class KnowledgeModule
{
    public const int MigrationOrder = 60;

    public static IServiceCollection AddKnowledgeModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddModuleDbContext<KnowledgeDbContext>(KnowledgeDbContext.Schema, "knowledge", MigrationOrder);
        services.AddScoped<KnowledgeAudit>();
        services.AddScoped<KnowledgeAdminService>();
        services.AddScoped<KnowledgePortalService>();
        services.AddScoped<Contracts.IReplyLinkArticles, ReplyLinkArticles>();
        return services;
    }

    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        KnowledgeEndpoints.Map(app);
        return app;
    }
}
