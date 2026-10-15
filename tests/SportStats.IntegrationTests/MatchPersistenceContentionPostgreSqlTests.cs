using Xunit;

namespace SportStats.IntegrationTests;

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class MatchPersistenceContentionPostgreSqlTests
{
    [GuardedPostgreSqlFact]
    public Task SameIdentityCreationWaitsForCallerRollbackAndRetriesWithOwnParents()
        => AssertRollbackContentionAsync(rollbackResolverSavepoint: false);

    [GuardedPostgreSqlFact]
    public Task MappingFailureRetainsLocksUntilCallerRollbackAndRetrySucceeds()
        => AssertRollbackContentionAsync(rollbackResolverSavepoint: true);

    private static async Task AssertRollbackContentionAsync(bool rollbackResolverSavepoint)
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        var result = await fixture.RunMatchContentionScenarioAsync(rollbackResolverSavepoint);
        Assert.True(result.ServerObservedWait);
        Assert.True(result.OwnerPersisted);
        Assert.True(result.RolledBackParentRejected);
        Assert.True(result.RetryPersisted);
        Assert.True(result.ReplayWithoutWrites);
        Assert.True(result.TrackingEmpty);
        Assert.True(result.AllFixtureRowsRolledBack);
    }
}
