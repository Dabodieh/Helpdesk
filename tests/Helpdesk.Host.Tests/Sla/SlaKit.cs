#pragma warning disable CA1861 // test payloads are one-off literals
using System.Net;
using System.Text.Json;
using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Host.Tests.Ticketing;
using Helpdesk.Modules.Ticketing.Automation;
using Helpdesk.Modules.Ticketing.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;

namespace Helpdesk.Host.Tests.Sla;

/// <summary>A clock tests can set. Only used by <see cref="TimedWorld"/>, a factory derived from the fixture's application.</summary>
public sealed class AdjustableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset value) => _now = value;

    public void Set(string iso) => _now = DateTimeOffset.Parse(iso, global::System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
}

/// <summary>
/// One department with its four roles, signed in on an application whose clock the test controls, plus helpers to configure
/// business hours / SLA policies / rules and to create and change tickets through the real API.
/// </summary>
public sealed class TimedWorld : IAsyncDisposable
{
    private readonly HelpdeskFixture _fixture;

    private TimedWorld(HelpdeskFixture fixture, Dept department, AdjustableClock clock, WebApplicationFactory<Program> factory, TestUser admin, TestUser lead, TestUser agent, TestUser viewer)
    {
        _fixture = fixture;
        Dept = department;
        Clock = clock;
        Factory = factory;
        Admin = admin;
        Lead = lead;
        Agent = agent;
        Viewer = viewer;
    }

    public Dept Dept { get; }

    public AdjustableClock Clock { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public TestUser Admin { get; private set; }

    public TestUser Lead { get; private set; }

    public TestUser Agent { get; private set; }

    public TestUser Viewer { get; private set; }

    /// <summary>Moves the clock and signs the four users in again: session cookies and CSRF tokens are valid only for a day of (fake) time.</summary>
    public async Task GoToAsync(string iso)
    {
        Clock.Set(iso);
        Admin = await _fixture.SignInAsync(Dept.Admin.Subject, factory: Factory);
        Lead = await _fixture.SignInAsync(Dept.Lead.Subject, factory: Factory);
        Agent = await _fixture.SignInAsync(Dept.Agent.Subject, factory: Factory);
        Viewer = await _fixture.SignInAsync(Dept.Viewer.Subject, factory: Factory);
    }

    public string Base => $"/api/departments/{Dept.Id}";

    public static async Task<TimedWorld> CreateAsync(HelpdeskFixture fixture, string startIso)
    {
        var dept = await fixture.NewDeptAsync();
        var clock = new AdjustableClock(DateTimeOffset.Parse(startIso, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime());
        var factory = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        return new TimedWorld(
            fixture, dept, clock, factory,
            await fixture.SignInAsync(dept.Admin.Subject, factory: factory),
            await fixture.SignInAsync(dept.Lead.Subject, factory: factory),
            await fixture.SignInAsync(dept.Agent.Subject, factory: factory),
            await fixture.SignInAsync(dept.Viewer.Subject, factory: factory));
    }

    public static object Weekdays(string start = "09:00", string end = "17:00") =>
        new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" }.Select(d => new { day = d, intervals = new[] { new { start, end } } }).ToArray();

    public async Task<Guid> CalendarAsync(string name = "Office", string zone = "Europe/London", object? exceptions = null)
    {
        var created = (await Admin.Client.PostAsync($"{Base}/business-hours", new { name, timeZone = zone, weekly = Weekdays(), exceptions = exceptions ?? Array.Empty<object>() })).Expect(HttpStatusCode.Created);
        return created.Id;
    }

    public async Task<ApiResponse> PolicyAsync(string name, Guid? calendarId, int? firstResponse, int? resolution, bool pause = false, object? conditions = null, int lead = 60) =>
        (await Admin.Client.PostAsync($"{Base}/sla-policies", new
        {
            name, calendarId, firstResponseMinutes = firstResponse, resolutionMinutes = resolution, pauseWhenPending = pause, approachingLeadMinutes = lead,
            conditions = conditions ?? Array.Empty<object>(),
        })).Expect(HttpStatusCode.Created);

    public async Task<ApiResponse> NewTicketAsync(string? subject = null, string? priority = null, object? extra = null) =>
        (await Lead.Client.PostAsync($"{Base}/tickets", TicketHelpers.NewTicketBody(subject, extra: priority is null ? extra : new { Priority = priority })))
            .Expect(HttpStatusCode.Created);

    public async Task<JsonElement> SlaAsync(Guid ticketId) => (await Agent.Client.GetAsync($"/api/tickets/{ticketId}/sla")).Expect(HttpStatusCode.OK).Json;

    public async Task SetStatusAsync(Guid ticketId, string status)
    {
        var current = (await Lead.Client.GetAsync($"/api/tickets/{ticketId}")).Expect(HttpStatusCode.OK);
        (await Lead.Client.PatchAsync($"/api/tickets/{ticketId}", new { version = current.Version, status })).Expect(HttpStatusCode.OK);
    }

    public async Task SetPriorityAsync(Guid ticketId, string priority)
    {
        var current = (await Lead.Client.GetAsync($"/api/tickets/{ticketId}")).Expect(HttpStatusCode.OK);
        (await Lead.Client.PatchAsync($"/api/tickets/{ticketId}", new { version = current.Version, priority })).Expect(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> TicketAsync(Guid ticketId) => (await Lead.Client.GetAsync($"/api/tickets/{ticketId}")).Expect(HttpStatusCode.OK);

    public async Task<int> RunMonitorAsync()
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISlaMonitor>().RunAsync(CancellationToken.None);
    }

    public Task<long> CountAsync(string sql, params Npgsql.NpgsqlParameter[] parameters) => _fixture.ScalarAsync<long>(sql, parameters);

    public static DateTimeOffset At(JsonElement element, params string[] path)
    {
        var node = element;
        foreach (var part in path)
        {
            node = node.GetProperty(part);
        }

        return node.GetDateTimeOffset().ToUniversalTime();
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}
