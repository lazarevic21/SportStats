using Xunit;

namespace SportStats.IntegrationTests;

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class TeamPersistenceContentionPostgreSqlTests
{
    [GuardedPostgreSqlFact]
    public Task SameIdentityCreationWaitsForCallerRollbackAndRetriesWithOwnParents()
        => AssertRollbackContentionAsync(existingMappedTeam: false);

    [GuardedPostgreSqlFact]
    public Task MissingMembershipWaitsForCallerRollbackAndRetriesWithoutRecreatingMappedTeam()
        => AssertRollbackContentionAsync(existingMappedTeam: true);

    private static async Task AssertRollbackContentionAsync(bool existingMappedTeam)
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        var result = await fixture.RunTeamContentionScenarioAsync(existingMappedTeam);
        Assert.True(result.ServerObservedWait);
        Assert.True(result.OwnerPersisted);
        Assert.True(result.RolledBackParentRejected);
        Assert.True(result.RetryPersisted);
        Assert.True(result.ReplayWithoutWrites);
        Assert.True(result.TrackingEmpty);
        Assert.True(result.AllFixtureRowsRolledBack);
    }
}
