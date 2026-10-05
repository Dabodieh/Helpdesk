using Npgsql;

namespace Helpdesk.Host.Tests.Infrastructure;

public static class TestEnvironment
{
    public const string DatabaseEnvironmentVariable = "HELPDESK_TEST_DB";

    /// <summary>Server-level connection (no database). Override with HELPDESK_TEST_DB; the default matches `docker compose up -d` with .env.example.</summary>
    public static string ServerConnectionString =>
        Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable) is { Length: > 0 } configured
            ? configured
            : "Host=localhost;Port=5432;Username=helpdesk;Password=helpdesk_dev";

    public static string WithDatabase(string database) =>
        new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = database, Pooling = true, MaxPoolSize = 20 }.ConnectionString;
}

/// <summary>A uniquely named PostgreSQL database that exists for the lifetime of one test fixture.</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    // CREATE DATABASE copies template1 and fails when run concurrently, so fixtures creating databases in parallel take turns.
    private static readonly SemaphoreSlim CreateLock = new(1, 1);

    private TestDatabase(string name) => Name = name;

    public string Name { get; }

    public string ConnectionString => TestEnvironment.WithDatabase(Name);

    public static async Task<TestDatabase> CreateAsync()
    {
        var name = $"helpdesk_test_{Guid.NewGuid():N}";
        await CreateLock.WaitAsync();
        try
        {
            await using var connection = new NpgsqlConnection(TestEnvironment.WithDatabase("postgres"));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            CreateLock.Release();
        }

        return new TestDatabase(name);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(TestEnvironment.WithDatabase("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
