using System.Data;
using System.Reflection;
using Npgsql;
using Xunit;

namespace SportStats.IntegrationTests;

// These checks exercise the new entry point without opening a database connection.
public sealed class MatchMetadataEvaluationFixtureGuardTests
{
    public static IEnumerable<object[]> Scenarios => Enum.GetValues<MetadataEvaluationScenario>()
        .Select(scenario => new object[] { scenario });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task ClosedFixtureCannotStartMetadataEvaluation(MetadataEvaluationScenario scenario)
    {
        var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        var constructor = typeof(PostgreSqlTestDatabaseFixture).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(NpgsqlConnection)], null)!;
        await using var fixture = (PostgreSqlTestDatabaseFixture)constructor.Invoke([connection]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunMetadataEvaluationScenarioAsync(scenario));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }
}
