using System.Net;
using Helpdesk.SharedKernel.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Infrastructure;

/// <summary>The application under test: Testing environment, DevSignIn enabled, PostgreSQL database created for this fixture.</summary>
public sealed class HelpdeskFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string PlatformAdminSubject = "platform-admin";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:ConnectionString", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Authentication:DevSignIn:Enabled", "true");
        builder.UseSetting("Authentication:BootstrapPlatformAdminSubjects:0", PlatformAdminSubject);
    }
}

/// <summary>
/// xUnit class fixture for integration tests: one uniquely named PostgreSQL database per fixture (env HELPDESK_TEST_DB, see
/// <see cref="TestEnvironment"/>), migrated with the real <c>migrate</c> runner, dropped on dispose. Tests in a class share the
/// database, so create uniquely named data (see <see cref="Unique"/>).
/// </summary>
public sealed class HelpdeskFixture : IAsyncLifetime
{
    private TestDatabase? _database;
    private TestUser? _platformAdmin;

    public HelpdeskFactory Factory { get; private set; } = null!;

    public string ConnectionString => _database!.ConnectionString;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync();
        Factory = new HelpdeskFactory(_database.ConnectionString);
        await Factory.Services.GetRequiredService<ModuleMigrationRunner>().RunAsync();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    /// <summary>A client with no session.</summary>
    public ApiClient CreateAnonymousClient(WebApplicationFactory<Program>? factory = null) =>
        new((factory ?? Factory).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    /// <summary>Signs in through the real dev-login endpoint (same cookie principal as Entra) and fetches a CSRF token.</summary>
    public async Task<TestUser> SignInAsync(string subject, string? name = null, string? email = null, WebApplicationFactory<Program>? factory = null)
    {
        var client = CreateAnonymousClient(factory);
        var url = $"/api/auth/dev-login?subject={Uri.EscapeDataString(subject)}&name={Uri.EscapeDataString(name ?? subject)}";
        if (email is not null)
        {
            url += $"&email={Uri.EscapeDataString(email)}";
        }

        (await client.GetAsync(url)).Expect(HttpStatusCode.Redirect);
        await client.RefreshCsrfAsync();
        var me = (await client.GetAsync("/api/me")).Expect(HttpStatusCode.OK);
        return new TestUser(subject, me.Id, client);
    }

    /// <summary>The bootstrapped platform admin (config subject <see cref="HelpdeskFactory.PlatformAdminSubject"/>), cached per fixture.</summary>
    public async Task<TestUser> PlatformAdminAsync() => _platformAdmin ??= await SignInAsync(HelpdeskFactory.PlatformAdminSubject, "Platform Admin");

    /// <summary>A new user with no memberships and no platform rights.</summary>
    public Task<TestUser> NewUserAsync(string prefix = "user") =>
        SignInAsync($"{prefix}-{Unique.Id()}", $"{prefix} {Unique.Id()}", $"{prefix}-{Unique.Id()}@example.test");

    public async Task<DepartmentRef> CreateDepartmentAsync(string? key = null, string? name = null)
    {
        var admin = await PlatformAdminAsync();
        key ??= Unique.Key();
        name ??= $"Dept {Unique.Id()}";
        var response = (await admin.Client.PostAsync("/api/departments", new { key, name })).Expect(HttpStatusCode.Created);
        return new DepartmentRef(response.Id, key, name, response.Version);
    }

    /// <summary>Adds (or changes) a membership through the real API as platform admin.</summary>
    public async Task AddMemberAsync(DepartmentRef department, TestUser user, string role)
    {
        var admin = await PlatformAdminAsync();
        (await admin.Client.PutAsync($"{department.Url}/members/{user.Id}", new { role })).Expect(HttpStatusCode.OK);
    }

    /// <summary>A new signed-in user holding <paramref name="role"/> in <paramref name="department"/>.</summary>
    public async Task<TestUser> NewMemberAsync(DepartmentRef department, string role)
    {
        var user = await NewUserAsync(role.ToLowerInvariant());
        await AddMemberAsync(department, user, role);
        return user;
    }

    public async Task<ApiResponse> CreateTeamAsync(DepartmentRef department, string? name = null)
    {
        var admin = await PlatformAdminAsync();
        return (await admin.Client.PostAsync($"{department.Url}/teams", new { name = name ?? $"Team {Unique.Id()}" })).Expect(HttpStatusCode.Created);
    }

    /// <summary>Runs service-level code in a DI scope of the application under test.</summary>
    public async Task WithScopeAsync(Func<IServiceProvider, Task> action)
    {
        using var scope = Factory.Services.CreateScope();
        await action(scope.ServiceProvider);
    }

    public async Task<int> ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    public Task<long> CountAuditAsync(string action, Guid? objectId = null) =>
        ScalarAsync<long>(
            "SELECT count(*) FROM audit.audit_events WHERE action = @a AND (@o::uuid IS NULL OR object_id = @o::uuid)",
            new NpgsqlParameter("a", action),
            new NpgsqlParameter("o", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)objectId ?? DBNull.Value });
}

public static class Unique
{
    private static long _counter;

    /// <summary>Short unique token (lower-case letters/digits) for names and subjects.</summary>
    public static string Id() => $"{Interlocked.Increment(ref _counter):x}{Guid.NewGuid().ToString("N")[..6]}";

    /// <summary>A valid, unique department key (^[A-Z][A-Z0-9]{1,9}$).</summary>
    public static string Key() => ("T" + Guid.NewGuid().ToString("N")[..7]).ToUpperInvariant();
}
