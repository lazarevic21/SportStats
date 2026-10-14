using Xunit;

namespace SportStats.IntegrationTests;

[CollectionDefinition("Rollback-only PostgreSQL", DisableParallelization = true)]
public sealed class RollbackOnlyPostgreSqlCollection { }

// Never participates in offline verification. Both explicit opt-in and a separately supplied
// connection string are required; the fixture then verifies the actual server identity/role.
public sealed class GuardedPostgreSqlFactAttribute : FactAttribute
{
    public GuardedPostgreSqlFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS") != "1")
            Skip = "Live PostgreSQL writes require explicit approval and opt-in.";
    }
}

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class CompetitionPersistencePostgreSqlTests
{
    private static async Task<CompetitionScenarioObservation> Run(CompetitionScenario scenario)
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        return await fixture.RunCompetitionScenarioAsync(scenario);
    }

    [GuardedPostgreSqlFact]
    public async Task CreatesCompetitionAndMappingReplaysAndFullCallerRollbackRemovesRows()
    {
        var result = await Run(CompetitionScenario.CreationReplay);
        Assert.True(result.IdsAndRelationshipsCorrect);
        Assert.True(result.ExactIdentityPreserved);
        Assert.True(result.ReusedWithoutDuplicates);
        Assert.True(result.EarlierInsertObserved);
        Assert.True(result.CallerTransactionPreserved);
        Assert.True(result.CallerWorkPreserved);
        Assert.True(result.TrackingEmpty);
        Assert.True(result.AllFixtureRowsRolledBack);
    }

    [GuardedPostgreSqlFact]
    public async Task ReusesSeededCompetitionPreservesMetadataAndRejectsUnmappedNaturalKey()
    {
        var result = await Run(CompetitionScenario.ExistingReuseConflict);
        Assert.True(result.IdsAndRelationshipsCorrect);
        Assert.True(result.ExactIdentityPreserved);
        Assert.True(result.ReusedWithoutDuplicates);
        Assert.True(result.StrictConflictRejected);
        Assert.False(result.EarlierInsertObserved);
        Assert.True(result.CallerTransactionPreserved);
        Assert.True(result.CallerWorkPreserved);
        Assert.True(result.TrackingEmpty);
        Assert.True(result.AllFixtureRowsRolledBack);
    }

    [GuardedPostgreSqlFact]
    public async Task MappingFailureAfterCompetitionInsertRollsBackResolverAndPreservesCallerWork()
    {
        var result = await Run(CompetitionScenario.FailureRollback);
        Assert.True(result.EarlierInsertObserved);
        Assert.True(result.CallerTransactionPreserved);
        Assert.True(result.CallerWorkPreserved);
        Assert.True(result.TrackingEmpty);
        Assert.True(result.AllFixtureRowsRolledBack);
    }
}
