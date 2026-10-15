using System.Data;
using System.Reflection;
using Npgsql;
using Xunit;

namespace SportStats.IntegrationTests;

// Offline checks: never open a database connection.
public sealed class SeasonFixtureGuardTests
{
    [Theory]
    [InlineData("competitions", false, true)]
    [InlineData("competition_external_ids", false, true)]
    [InlineData("seasons", false, false)]
    [InlineData("season_external_ids", false, false)]
    [InlineData("seasons", true, true)]
    [InlineData("season_external_ids", true, true)]
    [InlineData("sports", true, false)]
    [InlineData("teams", true, false)]
    [InlineData("seasons_extra", true, false)]
    public void SeasonWritesRequireExplicitSeasonScenario(string table, bool seasonMode, bool allowed)
        => Assert.Equal(allowed, PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert($"INSERT INTO platform.{table} (\"Id\") VALUES (@p0)", seasonMode));

    [Theory]
    [InlineData("UPDATE platform.seasons SET Key = 'changed'")]
    [InlineData("DELETE FROM platform.seasons")]
    [InlineData("INSERT INTO football.seasons (Id) VALUES (@p0)")]
    [InlineData("INSERT INTO platform.season_external_ids_extra (Id) VALUES (@p0)")]
    [InlineData("CREATE TABLE platform.seasons_copy (Id uuid)")]
    public void SeasonScenarioRejectsUnapprovedWrites(string sql)
        => Assert.False(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert(sql, true));

    [Fact]
    public void QuotedEfSeasonInsertIsRecognized()
        => Assert.True(PostgreSqlTestDatabaseFixture.IsApprovedScenarioInsert("INSERT INTO platform.\"seasons\" (\"Id\") VALUES (@p0)", true));

    [Theory]
    [InlineData(SeasonScenario.CreateInternal)]
    [InlineData(SeasonScenario.CreateExternal)]
    [InlineData(SeasonScenario.OccupiedNaturalKey)]
    [InlineData(SeasonScenario.IncorrectParent)]
    [InlineData(SeasonScenario.InconsistentKey)]
    [InlineData(SeasonScenario.FailureRollback)]
    [InlineData(SeasonScenario.CancellationRollback)]
    public async Task ClosedFixtureCannotStartSeasonScenario(SeasonScenario scenario)
    {
        var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        var constructor = typeof(PostgreSqlTestDatabaseFixture).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(NpgsqlConnection)], null)!;
        await using var fixture = (PostgreSqlTestDatabaseFixture)constructor.Invoke([connection]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunSeasonScenarioAsync(scenario));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ClosedFixtureCannotStartContentionScenario()
    {
        var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        var constructor = typeof(PostgreSqlTestDatabaseFixture).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(NpgsqlConnection)], null)!;
        await using var fixture = (PostgreSqlTestDatabaseFixture)constructor.Invoke([connection]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunLockContentionScenarioAsync());
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
