using Xunit;

namespace SportStats.IntegrationTests;

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class TeamPersistencePostgreSqlTests
{
    private static async Task Run(TeamScenario scenario)
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        var result = await fixture.RunTeamScenarioAsync(scenario);
        Assert.True(result.BehaviorCorrect);
        Assert.True(result.CallerTransactionPreserved);
        Assert.True(result.CallerWorkPreserved);
        Assert.True(result.TrackingEmpty);
        Assert.True(result.AllFixtureRowsRolledBack);
    }

    [GuardedPostgreSqlFact]
    public Task CreatesMultipleTeamsWithInternalSeasonAndReplaysWithoutWritesOrMetadataChanges() => Run(TeamScenario.CreateInternal);
    [GuardedPostgreSqlFact]
    public Task CreatesMultipleTeamsWithExternalSeasonAndReplaysWithoutWritesOrMetadataChanges() => Run(TeamScenario.CreateExternal);
    [GuardedPostgreSqlFact]
    public Task ReusesCanonicalTeamAcrossSeasonsWithoutDuplicateTeamOrMapping() => Run(TeamScenario.ReuseAcrossSeasons);
    [GuardedPostgreSqlFact]
    public Task TrustedProviderAliasesShareCanonicalTeamAndOneMembership() => Run(TeamScenario.TrustedAliases);
    [GuardedPostgreSqlFact]
    public Task SameNamesRemainDistinctAcrossExternalIdsProvidersAndScopes() => Run(TeamScenario.DistinctIdentities);
    [GuardedPostgreSqlFact]
    public Task RejectsDuplicateMappingsWithoutWrites() => Run(TeamScenario.DuplicateMappings);
    [GuardedPostgreSqlFact]
    public Task RejectsAmbiguousMappingsWithoutWrites() => Run(TeamScenario.AmbiguousMappings);
    [GuardedPostgreSqlFact]
    public Task EnforcesMembershipUniquenessForeignKeysAndRejectsDanglingMapping() => Run(TeamScenario.Constraints);
    [GuardedPostgreSqlFact]
    public Task MappingFailureAfterTeamInsertRollsBackAndPreservesCallerWork() => Run(TeamScenario.FailureRollback);
    [GuardedPostgreSqlFact]
    public Task CancellationAfterTeamInsertRollsBackAndPreservesCallerWork() => Run(TeamScenario.CancellationRollback);
}
