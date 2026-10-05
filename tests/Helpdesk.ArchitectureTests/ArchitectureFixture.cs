using System.Reflection;
using Helpdesk.Modules.Audit;
using Helpdesk.Modules.Identity;
using Helpdesk.Modules.Organisation;
using Helpdesk.SharedKernel.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Helpdesk.ArchitectureTests;

internal static class Modules
{
    public static readonly Assembly SharedKernel = typeof(IAuthorizer).Assembly;
    public static readonly Assembly Audit = typeof(AuditModule).Assembly;
    public static readonly Assembly Identity = typeof(IdentityModule).Assembly;
    public static readonly Assembly Organisation = typeof(OrganisationModule).Assembly;
    public static readonly Assembly Host = typeof(Program).Assembly;

    public static readonly IReadOnlyList<Assembly> All = [Audit, Identity, Organisation];

    public static string Root(Assembly module) => module.GetName().Name!; // e.g. Helpdesk.Modules.Identity

    public static string ContractsNamespace(Assembly module) => Root(module) + ".Contracts";

    /// <summary>Full names of every type of the module outside its Contracts namespace (its internals and its module registration class).</summary>
    public static string[] NonContractTypeNames(Assembly module) =>
        module.GetTypes()
            .Where(t => t.Namespace is not null && !t.Namespace.StartsWith(ContractsNamespace(module), StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .Where(n => !n.Contains('<', StringComparison.Ordinal))
            .ToArray();
}

/// <summary>The real application in the Testing environment (no database needed: nothing here sends a request).</summary>
public sealed class AppFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Testing");
        b.UseSetting("Database:ConnectionString", "Host=localhost;Database=unused;Username=x;Password=x");
        b.UseSetting("Authentication:DevSignIn:Enabled", "true");
    });

    public IServiceProvider Services => _factory.Services;

    public void Dispose() => _factory.Dispose();
}
