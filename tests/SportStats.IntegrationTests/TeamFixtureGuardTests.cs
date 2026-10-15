using System.Data;
using System.Reflection;
using Npgsql;
using Xunit;

namespace SportStats.IntegrationTests;

// Offline safety checks: no connection is opened.
public sealed class TeamFixtureGuardTests
{
    [Theory]
    [InlineData("teams")]
    [InlineData("team_external_ids")]
    [InlineData("season_teams")]
    public void FootballTeamInsertsRequireExplicitTeamMode(string table)
    {
        var sql = $"INSERT INTO football.\"{table}\" (\"Id\") VALUES (@p0)";
        Assert.False(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(sql, false));
        Assert.False(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(sql, true));
        Assert.True(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(sql, true, true));
    }

    [Theory]
    [InlineData("UPDATE football.teams SET \"Name\" = 'changed'")]
    [InlineData("DELETE FROM football.season_teams")]
    [InlineData("TRUNCATE football.teams")]
    [InlineData("INSERT INTO platform.teams (\"Id\") VALUES (@p0)")]
    [InlineData("INSERT INTO football.teams_extra (\"Id\") VALUES (@p0)")]
    [InlineData("INSERT INTO football.matches (\"Id\") VALUES (@p0)")]
    [InlineData("INSERT INTO football.standing_rows (\"TeamId\") VALUES (@p0)")]
    [InlineData("INSERT INTO platform.sports (\"Id\") VALUES (@p0)")]
    [InlineData("CREATE TABLE football.teams_copy (id uuid)")]
    [InlineData("COMMIT")]
    public void TeamModeDoesNotAllowOtherWrites(string sql)
        => Assert.False(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(sql, true, true));

    [Theory]
    [InlineData("competitions")]
    [InlineData("competition_external_ids")]
    [InlineData("seasons")]
    [InlineData("season_external_ids")]
    public void TeamSetupRetainsApprovedPlatformInserts(string table)
        => Assert.True(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(
            $"INSERT INTO platform.{table} (\"Id\") VALUES (@p0)", true, true));

    [Theory]
    [InlineData(TeamScenario.CreateInternal)]
    [InlineData(TeamScenario.CreateExternal)]
    [InlineData(TeamScenario.ReuseAcrossSeasons)]
    [InlineData(TeamScenario.TrustedAliases)]
    [InlineData(TeamScenario.DistinctIdentities)]
    [InlineData(TeamScenario.DuplicateMappings)]
    [InlineData(TeamScenario.AmbiguousMappings)]
    [InlineData(TeamScenario.Constraints)]
    [InlineData(TeamScenario.FailureRollback)]
    [InlineData(TeamScenario.CancellationRollback)]
    public async Task ClosedFixtureCannotStartTeamScenario(TeamScenario scenario)
    {
        var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        var constructor = typeof(PostgreSqlTestDatabaseFixture).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(NpgsqlConnection)], null)!;
        await using var fixture = (PostgreSqlTestDatabaseFixture)constructor.Invoke([connection]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunTeamScenarioAsync(scenario));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedFixtureCannotStartTeamContention(bool existingMappedTeam)
    {
        var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        var constructor = typeof(PostgreSqlTestDatabaseFixture).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(NpgsqlConnection)], null)!;
        await using var fixture = (PostgreSqlTestDatabaseFixture)constructor.Invoke([connection]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunTeamContentionScenarioAsync(existingMappedTeam));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
