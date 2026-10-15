using Xunit;

namespace SportStats.IntegrationTests;

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class PostgreSqlLockContentionTests
{
    [GuardedPostgreSqlFact]
    public async Task CoordinationLockBlocksSecondTransactionUntilCallerRollback()
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        var result = await fixture.RunLockContentionScenarioAsync();
        Assert.True(result.ServerObservedWait);
        Assert.True(result.AcquiredAfterCallerRollback);
        Assert.True(result.TrackingEmpty);
    }
}
