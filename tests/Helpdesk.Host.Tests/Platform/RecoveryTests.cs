using System.Net;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.SharedKernel.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Helpdesk.Host.Tests.Platform;

/// <summary>Recovery rules (ADR-021): the key ring survives a replaced host, and the migration order satisfies every cross-schema foreign key.</summary>
public sealed class RecoveryTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    [Fact]
    public async Task A_session_signed_in_on_one_host_is_accepted_by_a_replacement_host_because_the_key_ring_is_in_the_database()
    {
        using var first = fixture.Factory.WithWebHostBuilder(_ => { });
        using var http = first.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var login = await http.GetAsync("/api/auth/dev-login?subject=key-ring&name=key-ring");
        var cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        Assert.NotEmpty(cookie);
        Assert.True(await fixture.ScalarAsync<long>("SELECT count(*) FROM identity.data_protection_keys") >= 1);

        // A brand new host process-equivalent (own service provider, own in-memory state) reads the same key ring.
        using var second = fixture.Factory.WithWebHostBuilder(_ => { });
        using var other = second.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await other.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Every_cross_schema_foreign_key_points_at_a_module_that_migrates_earlier()
    {
        var order = fixture.Factory.Services.GetServices<IModuleMigration>().ToDictionary(m => m.ModuleName, m => m.Order, StringComparer.Ordinal);

        var edges = new List<(string From, string To, string Constraint)>();
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT ns.nspname, nt.nspname, c.conname
                FROM pg_constraint c
                JOIN pg_class cs ON cs.oid = c.conrelid JOIN pg_namespace ns ON ns.oid = cs.relnamespace
                JOIN pg_class ct ON ct.oid = c.confrelid JOIN pg_namespace nt ON nt.oid = ct.relnamespace
                WHERE c.contype = 'f' AND ns.nspname <> nt.nspname
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                edges.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (var (from, to, constraint) in edges)
        {
            Assert.True(order.TryGetValue(from, out var fromOrder) && order.TryGetValue(to, out var toOrder) && toOrder < fromOrder, $"{constraint}: {from} references {to}, which must migrate first.");
        }
    }
}
