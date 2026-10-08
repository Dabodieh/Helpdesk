using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Ticketing.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Host.Tests.Sla;

/// <summary>Up, Down, Up of the two SLA alert migrations against this fixture's own throw-away database (never a shared or production one).</summary>
public sealed class SlaAlertMigrationTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private const string Before = "20261008101853_TicketLifecycleEvents";
    private const string ConstraintOnly = "20261008160000_SlaAlertNotifications";
    private const string Latest = "20261008170000_SlaAlertInstants";

    private Task<long> AlertColumnsAsync() =>
        fixture.ScalarAsync<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema = 'ticketing' AND table_name = 'ticket_sla' AND column_name IN ('fr_alert_at','fr_alerted_at','res_alert_at','res_alerted_at')");

    private Task<long> SlaTypesAllowedAsync() =>
        fixture.ScalarAsync<long>("SELECT count(*) FROM pg_constraint WHERE conname = 'ck_notifications_type' AND pg_get_constraintdef(oid) LIKE '%sla_breached%'");

    private Task MigrateAsync(string target) =>
        fixture.WithScopeAsync(sp => sp.GetRequiredService<TicketingDbContext>().GetService<IMigrator>().MigrateAsync(target));

    [Fact]
    public async Task The_alert_migrations_go_down_and_up_again_cleanly()
    {
        Assert.Equal((4L, 1L), (await AlertColumnsAsync(), await SlaTypesAllowedAsync()));

        await MigrateAsync(ConstraintOnly); // Down of SlaAlertInstants
        Assert.Equal((0L, 1L), (await AlertColumnsAsync(), await SlaTypesAllowedAsync()));

        await MigrateAsync(Before); // Down of SlaAlertNotifications
        Assert.Equal((0L, 0L), (await AlertColumnsAsync(), await SlaTypesAllowedAsync()));

        await MigrateAsync(Latest);
        Assert.Equal((4L, 1L), (await AlertColumnsAsync(), await SlaTypesAllowedAsync()));
    }
}
