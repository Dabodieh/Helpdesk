using System.Reflection;
using NetArchTest.Rules;

namespace Helpdesk.ArchitectureTests;

/// <summary>ADR-001 / ADR-009: modules reference each other only through Contracts; internals stay internal.</summary>
public sealed class ModuleBoundaryTests
{
    private static string Describe(TestResult result) =>
        result.IsSuccessful ? "" : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);

    public static TheoryData<string, string> ForbiddenDependencies => new()
    {
        // module under test, module it must not touch except through Contracts
        { "Audit", "Identity" },
        { "Audit", "Organisation" },
        { "Identity", "Audit" },
        { "Identity", "Organisation" },
        { "Organisation", "Audit" },
        { "Organisation", "Identity" },
    };

    private static Assembly ByName(string name) => name switch
    {
        "Audit" => Modules.Audit,
        "Identity" => Modules.Identity,
        "Organisation" => Modules.Organisation,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(ForbiddenDependencies))]
    public void Module_does_not_depend_on_another_modules_internals(string from, string to)
    {
        var internals = Modules.NonContractTypeNames(ByName(to));
        var result = Types.InAssembly(ByName(from)).ShouldNot().HaveDependencyOnAny(internals).GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void The_checker_sees_contract_dependencies_so_the_rules_are_not_vacuous()
    {
        // Organisation uses Identity.Contracts (IUserDirectory) and Audit.Contracts (IAuditWriter).
        Assert.False(Types.InAssembly(Modules.Organisation).ShouldNot().HaveDependencyOn(Modules.ContractsNamespace(Modules.Identity)).GetResult().IsSuccessful);
        Assert.False(Types.InAssembly(Modules.Organisation).ShouldNot().HaveDependencyOn(Modules.ContractsNamespace(Modules.Audit)).GetResult().IsSuccessful);
        // ...and a dependency on internal types is reported too (Identity's own types use Identity's own internals).
        Assert.False(Types.InAssembly(Modules.Identity).ShouldNot().HaveDependencyOnAny(Modules.NonContractTypeNames(Modules.Identity)).GetResult().IsSuccessful);
    }

    [Fact]
    public void Project_references_follow_the_module_dag()
    {
        // Audit -> SharedKernel; Identity -> SharedKernel, Audit; Organisation -> SharedKernel, Audit, Identity.
        static IEnumerable<string> ModuleRefs(Assembly a) =>
            a.GetReferencedAssemblies().Select(r => r.Name!).Where(n => n.StartsWith("Helpdesk.Modules.", StringComparison.Ordinal));

        Assert.Empty(ModuleRefs(Modules.Audit));
        Assert.Equal(["Helpdesk.Modules.Audit"], ModuleRefs(Modules.Identity).Order());
        Assert.Equal(["Helpdesk.Modules.Audit", "Helpdesk.Modules.Identity"], ModuleRefs(Modules.Organisation).Order());
        Assert.Empty(ModuleRefs(Modules.SharedKernel));
    }

    [Fact]
    public void Shared_kernel_does_not_depend_on_any_module()
    {
        var result = Types.InAssembly(Modules.SharedKernel).ShouldNot().HaveDependencyOn("Helpdesk.Modules").GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Theory]
    [InlineData("Audit")]
    [InlineData("Identity")]
    [InlineData("Organisation")]
    public void Only_contracts_and_the_module_registration_class_are_public(string module)
    {
        var assembly = ByName(module);
        var allowedRegistration = $"{Modules.Root(assembly)}.{module}Module";
        var publicTypes = assembly.GetTypes()
            .Where(t => t.IsVisible && !t.Name.Contains('<', StringComparison.Ordinal))
            .Where(t => !t.Namespace!.StartsWith(Modules.ContractsNamespace(assembly), StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .Where(n => n != allowedRegistration)
            .ToList();
        Assert.True(publicTypes.Count == 0, "Public types outside Contracts: " + string.Join(", ", publicTypes));
    }

    [Theory]
    [InlineData("Audit")]
    [InlineData("Identity")]
    [InlineData("Organisation")]
    public void Module_registration_exposes_the_agreed_entry_points(string module)
    {
        var type = ByName(module).GetType($"{Modules.Root(ByName(module))}.{module}Module")!;
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name).ToList();
        Assert.Contains($"Add{module}Module", methods);
        Assert.Contains($"Map{module}Endpoints", methods);
    }

    [Fact]
    public void Contracts_do_not_expose_persistence_entities()
    {
        foreach (var module in Modules.All)
        {
            var offenders = module.GetTypes()
                .Where(t => t.IsVisible && t.Namespace!.StartsWith(Modules.ContractsNamespace(module), StringComparison.Ordinal))
                .SelectMany(t => t.GetProperties().Select(p => p.PropertyType).Concat(t.GetMethods().Select(m => m.ReturnType)))
                .Where(t => typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(t) && t != typeof(Microsoft.EntityFrameworkCore.DbContext))
                .ToList();
            Assert.Empty(offenders);
        }
    }

    [Fact]
    public void Only_the_host_references_all_modules()
    {
        var hostRefs = Modules.Host.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.All(Modules.All, m => Assert.Contains(m.GetName().Name, hostRefs));
    }
}
