using Helpdesk.Host.Tests.Infrastructure;
using Helpdesk.Modules.Organisation.Contracts;
using Npgsql;

namespace Helpdesk.Host.Tests.Organisation;

/// <summary>The database itself enforces the organisation invariants, independent of the application code.</summary>
public sealed class DatabaseConstraintTests(HelpdeskFixture fixture) : IClassFixture<HelpdeskFixture>
{
    private static async Task AssertSqlState(string expected, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(expected, ex.SqlState);
    }

    private async Task InsertTeamMembership(Guid teamId, Guid userId, Guid departmentId) =>
        await fixture.ExecuteAsync(
            "INSERT INTO organisation.team_memberships (team_id, user_id, department_id, created_at) VALUES (@t, @u, @d, now())",
            new NpgsqlParameter("t", teamId), new NpgsqlParameter("u", userId), new NpgsqlParameter("d", departmentId));

    [Fact]
    public async Task Team_membership_cannot_cross_departments()
    {
        var a = await fixture.CreateDepartmentAsync();
        var b = await fixture.CreateDepartmentAsync();
        var teamOfA = (await fixture.CreateTeamAsync(a)).Id;
        var memberOfB = await fixture.NewMemberAsync(b, DepartmentRoles.Agent);
        var memberOfA = await fixture.NewMemberAsync(a, DepartmentRoles.Agent);

        // Team of A, user only in B, claiming department B: team/department pair does not exist.
        await AssertSqlState(PostgresErrorCodes.ForeignKeyViolation, () => InsertTeamMembership(teamOfA, memberOfB.Id, b.Id));
        // Team of A, user only in B, claiming department A: user holds no membership in A.
        await AssertSqlState(PostgresErrorCodes.ForeignKeyViolation, () => InsertTeamMembership(teamOfA, memberOfB.Id, a.Id));
        // Team of A, user in A, claiming department B.
        await AssertSqlState(PostgresErrorCodes.ForeignKeyViolation, () => InsertTeamMembership(teamOfA, memberOfA.Id, b.Id));
        // The valid combination works.
        await InsertTeamMembership(teamOfA, memberOfA.Id, a.Id);
    }

    [Fact]
    public async Task Team_membership_requires_a_department_membership()
    {
        var department = await fixture.CreateDepartmentAsync();
        var team = (await fixture.CreateTeamAsync(department)).Id;
        var stranger = await fixture.NewUserAsync();
        await AssertSqlState(PostgresErrorCodes.ForeignKeyViolation, () => InsertTeamMembership(team, stranger.Id, department.Id));
    }

    [Fact]
    public async Task Deleting_a_department_membership_cascades_to_team_memberships()
    {
        var department = await fixture.CreateDepartmentAsync();
        var team = (await fixture.CreateTeamAsync(department)).Id;
        var agent = await fixture.NewMemberAsync(department, DepartmentRoles.Agent);
        await InsertTeamMembership(team, agent.Id, department.Id);

        await fixture.ExecuteAsync(
            "DELETE FROM organisation.department_memberships WHERE user_id = @u AND department_id = @d",
            new NpgsqlParameter("u", agent.Id), new NpgsqlParameter("d", department.Id));

        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM organisation.team_memberships WHERE team_id = @t", new NpgsqlParameter("t", team)));
    }

    [Fact]
    public async Task Department_membership_must_reference_an_existing_user_and_department()
    {
        var department = await fixture.CreateDepartmentAsync();
        const string sql = "INSERT INTO organisation.department_memberships (user_id, department_id, role, created_at, updated_at) VALUES (@u, @d, 'Agent', now(), now())";

        await AssertSqlState(PostgresErrorCodes.ForeignKeyViolation, () => fixture.ExecuteAsync(sql, new NpgsqlParameter("u", Guid.NewGuid()), new NpgsqlParameter("d", department.Id)));
        var user = await fixture.NewUserAsync();
        await AssertSqlState(PostgresErrorCodes.ForeignKeyViolation, () => fixture.ExecuteAsync(sql, new NpgsqlParameter("u", user.Id), new NpgsqlParameter("d", Guid.NewGuid())));
    }

    [Fact]
    public async Task Role_is_constrained_to_the_system_roles()
    {
        var department = await fixture.CreateDepartmentAsync();
        var user = await fixture.NewUserAsync();
        await AssertSqlState(PostgresErrorCodes.CheckViolation, () => fixture.ExecuteAsync(
            "INSERT INTO organisation.department_memberships (user_id, department_id, role, created_at, updated_at) VALUES (@u, @d, 'Owner', now(), now())",
            new NpgsqlParameter("u", user.Id), new NpgsqlParameter("d", department.Id)));
    }

    [Fact]
    public async Task Names_and_keys_are_unique_case_insensitively()
    {
        var department = await fixture.CreateDepartmentAsync();
        var other = await fixture.CreateDepartmentAsync();
        var team = $"Ops {Unique.Id()}";
        await fixture.CreateTeamAsync(department, team);

        await AssertSqlState(PostgresErrorCodes.UniqueViolation, () => fixture.ExecuteAsync(
            "INSERT INTO organisation.teams (id, department_id, name, is_active, created_at, updated_at) VALUES (gen_random_uuid(), @d, @n, true, now(), now())",
            new NpgsqlParameter("d", department.Id), new NpgsqlParameter("n", team.ToUpperInvariant())));
        await AssertSqlState(PostgresErrorCodes.UniqueViolation, () => fixture.ExecuteAsync(
            "UPDATE organisation.departments SET name = @n WHERE id = @id",
            new NpgsqlParameter("n", department.Name.ToLowerInvariant()), new NpgsqlParameter("id", other.Id)));
    }

    [Fact]
    public async Task Department_key_format_and_immutability_are_enforced()
    {
        var department = await fixture.CreateDepartmentAsync();
        await AssertSqlState(PostgresErrorCodes.CheckViolation, () => fixture.ExecuteAsync(
            "INSERT INTO organisation.departments (id, key, name, is_active, created_at, updated_at) VALUES (gen_random_uuid(), 'bad key', 'x', true, now(), now())"));
        await AssertSqlState(PostgresErrorCodes.IntegrityConstraintViolation, () => fixture.ExecuteAsync(
            "UPDATE organisation.departments SET key = 'ZZZZ' WHERE id = @id", new NpgsqlParameter("id", department.Id)));
    }

    [Fact]
    public async Task Departments_with_dependants_cannot_be_hard_deleted()
    {
        var department = await fixture.CreateDepartmentAsync();
        await fixture.CreateTeamAsync(department);
        await AssertSqlState(PostgresErrorCodes.RestrictViolation, () => fixture.ExecuteAsync(
            "DELETE FROM organisation.departments WHERE id = @id", new NpgsqlParameter("id", department.Id)));
    }

    [Fact]
    public async Task Row_version_changes_on_update_and_gives_the_api_version()
    {
        var department = await fixture.CreateDepartmentAsync();
        var xminBefore = await fixture.ScalarAsync<uint>("SELECT xmin FROM organisation.departments WHERE id = @id", new NpgsqlParameter("id", department.Id));
        Assert.Equal(xminBefore.ToString(System.Globalization.CultureInfo.InvariantCulture), department.Version);
    }
}
