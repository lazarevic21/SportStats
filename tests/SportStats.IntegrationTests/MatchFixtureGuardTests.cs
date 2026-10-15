using System.Data;
using System.Reflection;
using Npgsql;
using Xunit;

namespace SportStats.IntegrationTests;

// These safety checks never open a database connection.
public sealed class MatchFixtureGuardTests
{
    [Theory]
    [InlineData("matches")]
    [InlineData("match_external_ids")]
    public void MatchInsertsRequireExplicitMatchMode(string table)
    {
        foreach (var qualified in new[] { $"football.{table}", $"football.\"{table}\"", $"\"football\".\"{table}\"" })
        {
            var sql = $"INSERT INTO {qualified} (\"Id\") VALUES (@p0)";
            Assert.False(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(sql, false));
            Assert.False(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(sql, true));
            Assert.False(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(sql, true, true));
            Assert.True(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(sql, true, true, true));
        }
    }

    [Theory]
    [InlineData("UPDATE football.matches SET \"HomeGoals\" = 1")]
    [InlineData("DELETE FROM football.matches")]
    [InlineData("TRUNCATE football.matches")]
    [InlineData("INSERT INTO platform.matches (\"Id\") VALUES (@p0)")]
    [InlineData("INSERT INTO football.matches_extra (\"Id\") VALUES (@p0)")]
    [InlineData("INSERT INTO football.match_external_ids_extra (\"Id\") VALUES (@p0)")]
    [InlineData("INSERT INTO football.standing_rows (\"TeamId\") VALUES (@p0)")]
    [InlineData("INSERT INTO platform.sports (\"Id\") VALUES (@p0)")]
    [InlineData("CREATE TABLE football.matches_copy (id uuid)")]
    [InlineData("COMMIT")]
    public void MatchModeDoesNotAllowOtherWrites(string sql)
        => Assert.False(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(sql, true, true, true));

    [Theory]
    [InlineData("platform", "competitions")]
    [InlineData("platform", "competition_external_ids")]
    [InlineData("platform", "seasons")]
    [InlineData("platform", "season_external_ids")]
    [InlineData("football", "teams")]
    [InlineData("football", "team_external_ids")]
    [InlineData("football", "season_teams")]
    public void MatchSetupRetainsOnlyApprovedParentInserts(string schema, string table)
        => Assert.True(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(
            $"INSERT INTO {schema}.{table} (\"Id\") VALUES (@p0)", true, true, true));

    public static IEnumerable<object[]> Scenarios => Enum.GetValues<MatchScenario>().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task ClosedFixtureCannotStartMatchScenario(MatchScenario scenario)
    {
        var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        await using var fixture = ClosedFixture(connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunMatchScenarioAsync(scenario));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedFixtureCannotStartMatchContention(bool rollbackResolverSavepoint)
    {
        var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        await using var fixture = ClosedFixture(connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunMatchContentionScenarioAsync(rollbackResolverSavepoint));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    private static PostgreSqlTestDatabaseFixture ClosedFixture(NpgsqlConnection connection)
    {
        var constructor = typeof(PostgreSqlTestDatabaseFixture).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(NpgsqlConnection)], null)!;
        return (PostgreSqlTestDatabaseFixture)constructor.Invoke([connection]);
    }
}
