using Xunit;

namespace SportStats.IntegrationTests;

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class SeasonPersistencePostgreSqlTests
{
    private static async Task<SeasonScenarioObservation> Run(SeasonScenario scenario)
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        return await fixture.RunSeasonScenarioAsync(scenario);
    }

    private static void AssertCleanup(SeasonScenarioObservation result)
    {
        Assert.True(result.CallerTransactionPreserved);
        Assert.True(result.CallerWorkPreserved);
        Assert.True(result.TrackingEmpty);
        Assert.True(result.AllFixtureRowsRolledBack);
    }

    [GuardedPostgreSqlFact]
    public Task CreatesWithInternalParentReplaysWithoutWritesPreservesMetadataAndCallerRollbackRemovesRows()
        => Creation(SeasonScenario.CreateInternal);

    [GuardedPostgreSqlFact]
    public Task CreatesWithExternalParentReplaysWithoutWritesPreservesMetadataAndCallerRollbackRemovesRows()
        => Creation(SeasonScenario.CreateExternal);

    private static async Task Creation(SeasonScenario scenario)
    {
        var result = await Run(scenario);
        Assert.True(result.IdentityAndMetadataCorrect);
        Assert.True(result.ReplayWithoutWrites);
        Assert.True(result.EarlierInsertObserved);
        AssertCleanup(result);
    }

    [GuardedPostgreSqlFact]
    public Task RejectsOccupiedNaturalKeyWithoutExactMapping() => Conflict(SeasonScenario.OccupiedNaturalKey);

    [GuardedPostgreSqlFact]
    public Task RejectsMappingOwnedByAnotherCompetition() => Conflict(SeasonScenario.IncorrectParent);

    [GuardedPostgreSqlFact]
    public Task RejectsMappedSeasonWithInconsistentKey() => Conflict(SeasonScenario.InconsistentKey);

    private static async Task Conflict(SeasonScenario scenario)
    {
        var result = await Run(scenario);
        Assert.True(result.ExpectedConflictRejected);
        Assert.False(result.EarlierInsertObserved);
        AssertCleanup(result);
    }

    [GuardedPostgreSqlFact]
    public Task MappingFailureAfterSeasonInsertRollsBackResolverAndPreservesCallerWork() => Failure(SeasonScenario.FailureRollback);

    [GuardedPostgreSqlFact]
    public Task CancellationAfterSeasonInsertRollsBackResolverAndPreservesCallerWork() => Failure(SeasonScenario.CancellationRollback);

    private static async Task Failure(SeasonScenario scenario)
    {
        var result = await Run(scenario);
        Assert.True(result.EarlierInsertObserved);
        AssertCleanup(result);
    }
}
