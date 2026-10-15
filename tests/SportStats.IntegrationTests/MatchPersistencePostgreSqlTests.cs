using Xunit;

namespace SportStats.IntegrationTests;

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class MatchPersistencePostgreSqlTests
{
    private static async Task Run(MatchScenario scenario)
    {
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"));
        var result = await fixture.RunMatchScenarioAsync(scenario);
        Assert.True(result.BehaviorCorrect);
        Assert.True(result.CallerTransactionPreserved);
        Assert.True(result.CallerWorkPreserved);
        Assert.True(result.TrackingEmpty);
        Assert.True(result.AllFixtureRowsRolledBack);
    }
    [GuardedPostgreSqlFact] public Task CreatesOrderedMatchesAndMappingsReplaysWithoutWritesAndPreservesMetadata() => Run(MatchScenario.Create);
    [GuardedPostgreSqlFact] public Task NullableMetadataAndZeroGoalsRoundTrip() => Run(MatchScenario.NullMetadata);
    [GuardedPostgreSqlFact] public Task TrustedAliasesReuseOneCanonicalMatchInRequestOrder() => Run(MatchScenario.TrustedAliases);
    [GuardedPostgreSqlFact] public Task IdenticalFixturesRemainDistinctAcrossExternalIdsProvidersAndScopes() => Run(MatchScenario.DistinctIdentities);
    [GuardedPostgreSqlFact] public Task RejectsConflictingSeason() => Run(MatchScenario.WrongSeason);
    [GuardedPostgreSqlFact] public Task RejectsConflictingHomeTeam() => Run(MatchScenario.WrongHome);
    [GuardedPostgreSqlFact] public Task RejectsConflictingAwayTeam() => Run(MatchScenario.WrongAway);
    [GuardedPostgreSqlFact] public Task RejectsMissingSeasonMembership() => Run(MatchScenario.MissingMembership);
    [GuardedPostgreSqlFact] public Task RejectsIdenticalCanonicalParticipants() => Run(MatchScenario.SameTeam);
    [GuardedPostgreSqlFact] public Task RejectsUnresolvedImplicitSeason() => Run(MatchScenario.MissingSeason);
    [GuardedPostgreSqlFact] public Task RejectsUnresolvedTeam() => Run(MatchScenario.MissingTeam);
    [GuardedPostgreSqlFact] public Task RejectsDuplicateMappingsWithoutWrites() => Run(MatchScenario.DuplicateMappings);
    [GuardedPostgreSqlFact] public Task RejectsAmbiguousMappingsWithoutWrites() => Run(MatchScenario.AmbiguousMappings);
    [GuardedPostgreSqlFact] public Task EnforcesMembershipForeignKeysDistinctParticipantsNonnegativeScoresAndMappingForeignKey() => Run(MatchScenario.Constraints);
    [GuardedPostgreSqlFact] public Task MappingFailureAfterMatchInsertRollsBackAndPreservesCallerWork() => Run(MatchScenario.FailureRollback);
    [GuardedPostgreSqlFact] public Task CancellationAfterMatchInsertRollsBackAndPreservesCallerWork() => Run(MatchScenario.CancellationRollback);
}
