using Xunit;

namespace SportStats.IntegrationTests;

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class InitialImportPostgreSqlTests
{
    private static async Task Verify(InitialImportScenario scenario)
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        var result = await fixture.RunInitialImportScenarioAsync(scenario);
        Assert.True(result.BehaviorCorrect); Assert.True(result.TrackingEmpty); Assert.True(result.AllRowsRolledBack);
    }
    [GuardedPostgreSqlFact] public Task PreviewRejectsWritesAndAdvisoryLocks() => Verify(InitialImportScenario.Preview);
    [GuardedPostgreSqlFact] public Task FullGraphCreationAndIdenticalReplayPreserveIds() => Verify(InitialImportScenario.CreationReplay);
    [GuardedPostgreSqlFact] public Task ConflictingReplayPreservesExistingMatchMetadata() => Verify(InitialImportScenario.Conflict);
    [GuardedPostgreSqlFact] public Task FinalStageFailureRollsBackFullImport() => Verify(InitialImportScenario.FailureRollback);
    [GuardedPostgreSqlFact] public Task FinalStageCancellationRollsBackFullImport() => Verify(InitialImportScenario.CancellationRollback);
    [GuardedPostgreSqlFact] public Task DuplicateMappingsFailClosed() => Verify(InitialImportScenario.DuplicateMappings);
    [GuardedPostgreSqlFact] public Task AmbiguousMappingsFailClosed() => Verify(InitialImportScenario.AmbiguousMappings);
    [GuardedPostgreSqlFact] public Task UnmappedNaturalKeyOwnerFailsClosed() => Verify(InitialImportScenario.MissingMapping);
    [GuardedPostgreSqlFact] public Task DatabaseRejectsDuplicateCompetitionAndDanglingMatchMapping() => Verify(InitialImportScenario.Constraints);
    [GuardedPostgreSqlFact] public Task ConcurrentFullImportWaitsForOwnerRollbackThenCreatesAndReplaysSafely() => Verify(InitialImportScenario.Concurrent);
}
