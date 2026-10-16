using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;
using SportStats.Domain.Football;
using Xunit;

namespace SportStats.IntegrationTests;

[Collection("Rollback-only PostgreSQL")]
[Trait("Category", "PostgreSQLLive")]
public sealed class MatchMetadataEvaluationPostgreSqlTests
{
    private static async Task Run(MetadataEvaluationScenario scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var fixture = await PostgreSqlTestDatabaseFixture.OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"), timeout.Token);
        var result = await fixture.RunMetadataEvaluationScenarioAsync(scenario, timeout.Token);
        Assert.True(result.BehaviorCorrect);
        Assert.True(result.TrackingEmpty);
        Assert.True(result.NoWrites);
        Assert.True(result.NoSaveChanges);
        Assert.True(result.CallerTransactionPreserved);
        Assert.True(result.AllFixtureRowsRolledBack);
        Assert.InRange(result.ReadQueryCount, 0, 6);
        Assert.All(result.Evaluations, evaluation =>
        {
            Assert.Equal(MatchMetadataFreshness.FreshnessUnverified, evaluation.Freshness);
            Assert.False(evaluation.IsUpdateAuthorized);
        });
        if (scenario is MetadataEvaluationScenario.Cancellation or MetadataEvaluationScenario.QueryCancellation or
            MetadataEvaluationScenario.DatabaseFailure or MetadataEvaluationScenario.DanglingMatchConstraint or
            MetadataEvaluationScenario.MissingRelationshipConstraint)
        {
            Assert.Empty(result.Evaluations);
            Assert.Equal(scenario is MetadataEvaluationScenario.QueryCancellation or MetadataEvaluationScenario.DatabaseFailure
                ? 1 : 0, result.ReadQueryCount);
        }

        var expectedFailure = scenario switch
        {
            MetadataEvaluationScenario.DuplicateMappings => IdentityResolutionFailureCode.DuplicateMappings,
            MetadataEvaluationScenario.AmbiguousMappings => IdentityResolutionFailureCode.AmbiguousMappings,
            MetadataEvaluationScenario.MissingMatchMapping or MetadataEvaluationScenario.MissingSeasonMapping or
                MetadataEvaluationScenario.MissingTeamMapping => IdentityResolutionFailureCode.MissingReference,
            MetadataEvaluationScenario.WrongSeason or MetadataEvaluationScenario.WrongHome or
                MetadataEvaluationScenario.WrongAway or MetadataEvaluationScenario.ConflictingAliases => IdentityResolutionFailureCode.RelationshipConflict,
            MetadataEvaluationScenario.UnsupportedStatus => IdentityResolutionFailureCode.InvalidInput,
            _ => (IdentityResolutionFailureCode?)null
        };
        if (expectedFailure.HasValue)
        {
            Assert.NotEmpty(result.Evaluations);
            Assert.All(result.Evaluations, evaluation =>
            {
                Assert.Equal(expectedFailure, evaluation.Failure?.Code);
                Assert.Equal(scenario == MetadataEvaluationScenario.UnsupportedStatus
                    ? MatchMetadataEvaluationDisposition.InvalidInput : MatchMetadataEvaluationDisposition.Conflict,
                    evaluation.Disposition);
                Assert.Empty(evaluation.Changes);
            });
        }
        if (scenario is MetadataEvaluationScenario.Unchanged or MetadataEvaluationScenario.Missing or
            MetadataEvaluationScenario.ZeroGoals or MetadataEvaluationScenario.TrustedAliases)
        {
            Assert.NotEmpty(result.Evaluations);
            Assert.All(result.Evaluations, evaluation =>
            {
                Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, evaluation.Disposition);
                Assert.NotNull(evaluation.MatchId);
                Assert.Empty(evaluation.Changes);
                Assert.Null(evaluation.Failure);
            });
        }
        if (scenario == MetadataEvaluationScenario.Changes)
        {
            var evaluation = Assert.Single(result.Evaluations);
            Assert.Equal(MatchMetadataEvaluationDisposition.ChangesDetected, evaluation.Disposition);
            Assert.Equal(Enum.GetValues<MatchMetadataField>().Order(), evaluation.Changes.Select(c => c.Field).Order());
            var kickoff = new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc);
            AssertChange(evaluation, MatchMetadataField.KickoffUtc, kickoff, kickoff.AddHours(1));
            AssertChange(evaluation, MatchMetadataField.Matchday, 3, 4);
            AssertChange(evaluation, MatchMetadataField.Status, FootballMatchStatus.Finished, FootballMatchStatus.Scheduled);
            AssertChange(evaluation, MatchMetadataField.HomeGoals, 0, 1);
            AssertChange(evaluation, MatchMetadataField.AwayGoals, 2, 3);
        }
        if (scenario == MetadataEvaluationScenario.Null)
        {
            var evaluation = Assert.Single(result.Evaluations);
            Assert.Equal(MatchMetadataEvaluationDisposition.ChangesDetected, evaluation.Disposition);
            Assert.Equal(4, evaluation.Changes.Count);
            Assert.DoesNotContain(evaluation.Changes, c => c.Field == MatchMetadataField.Status);
            Assert.All(evaluation.Changes, change => Assert.Equal(MatchObservationFieldPresence.Null, change.Presence));
            var kickoffChange = Assert.IsType<MatchMetadataChange<DateTime>>(evaluation.Changes.Single(c => c.Field == MatchMetadataField.KickoffUtc));
            Assert.Equal(new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc), kickoffChange.Before);
            Assert.Null(kickoffChange.After);
            foreach (var (field, before) in new[] { (MatchMetadataField.Matchday, 3), (MatchMetadataField.HomeGoals, 0), (MatchMetadataField.AwayGoals, 2) })
            {
                var change = Assert.IsType<MatchMetadataChange<int>>(evaluation.Changes.Single(c => c.Field == field));
                Assert.Equal(before, change.Before);
                Assert.Null(change.After);
            }
        }
        if (scenario == MetadataEvaluationScenario.OrderedBatch)
        {
            Assert.Equal(3, result.Evaluations.Count);
            Assert.Equal(MatchMetadataEvaluationDisposition.Conflict, result.Evaluations[0].Disposition);
            Assert.Equal(IdentityResolutionFailureCode.MissingReference, result.Evaluations[0].Failure?.Code);
            Assert.Null(result.Evaluations[0].MatchId);
            Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, result.Evaluations[1].Disposition);
            Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, result.Evaluations[2].Disposition);
            Assert.NotNull(result.Evaluations[1].MatchId);
            Assert.Equal(result.Evaluations[1].MatchId, result.Evaluations[2].MatchId);
        }
        if (scenario is MetadataEvaluationScenario.TrustedAliases or MetadataEvaluationScenario.HeterogeneousBatch)
        {
            Assert.Equal(3, result.Evaluations.Count);
            Assert.Single(result.Evaluations.Select(evaluation => evaluation.MatchId).Distinct());
            Assert.All(result.Evaluations, evaluation => Assert.Equal(
                MatchMetadataEvaluationDisposition.Unchanged, evaluation.Disposition));
        }
        if (scenario == MetadataEvaluationScenario.HeterogeneousBatch)
        {
            Assert.Equal(3, result.MatchMappingCandidateRows);
            Assert.Equal(2, result.ExactMatchMappingRows);
        }
    }

    private static void AssertChange<T>(MatchMetadataEvaluation evaluation, MatchMetadataField field, T before, T after) where T : struct
    {
        var change = Assert.IsType<MatchMetadataChange<T>>(evaluation.Changes.Single(c => c.Field == field));
        Assert.Equal(MatchObservationFieldPresence.Value, change.Presence);
        Assert.Equal(before, change.Before);
        Assert.Equal(after, change.After);
    }

    [GuardedPostgreSqlFact] public Task ExactIdentityLoadsPersistedRelationshipsAndUnchangedMetadata() => Run(MetadataEvaluationScenario.Unchanged);
    [GuardedPostgreSqlFact] public Task DetectsKickoffMatchdayStatusAndBothGoalChanges() => Run(MetadataEvaluationScenario.Changes);
    [GuardedPostgreSqlFact] public Task MissingPreservesPersistedMetadata() => Run(MetadataEvaluationScenario.Missing);
    [GuardedPostgreSqlFact] public Task ExplicitNullProposesClearingNullablePersistedMetadata() => Run(MetadataEvaluationScenario.Null);
    [GuardedPostgreSqlFact] public Task ExplicitZeroGoalsRoundTripUnchanged() => Run(MetadataEvaluationScenario.ZeroGoals);
    [GuardedPostgreSqlFact] public Task TrustedAliasesResolveOnePersistedMatch() => Run(MetadataEvaluationScenario.TrustedAliases);
    [GuardedPostgreSqlFact] public Task ContradictoryAliasesConflict() => Run(MetadataEvaluationScenario.ConflictingAliases);
    [GuardedPostgreSqlFact] public Task DuplicateMappingsAreRejected() => Run(MetadataEvaluationScenario.DuplicateMappings);
    [GuardedPostgreSqlFact] public Task AmbiguousMappingsAreRejected() => Run(MetadataEvaluationScenario.AmbiguousMappings);
    [GuardedPostgreSqlFact] public Task MissingMatchMappingIsRejected() => Run(MetadataEvaluationScenario.MissingMatchMapping);
    [GuardedPostgreSqlFact] public Task MissingSeasonMappingIsRejected() => Run(MetadataEvaluationScenario.MissingSeasonMapping);
    [GuardedPostgreSqlFact] public Task MissingTeamMappingIsRejected() => Run(MetadataEvaluationScenario.MissingTeamMapping);
    [GuardedPostgreSqlFact] public Task MismatchedSeasonAssertionIsRejected() => Run(MetadataEvaluationScenario.WrongSeason);
    [GuardedPostgreSqlFact] public Task MismatchedHomeAssertionIsRejected() => Run(MetadataEvaluationScenario.WrongHome);
    [GuardedPostgreSqlFact] public Task MismatchedAwayAssertionIsRejected() => Run(MetadataEvaluationScenario.WrongAway);
    [GuardedPostgreSqlFact] public Task BatchResultsPreserveRequestOrder() => Run(MetadataEvaluationScenario.OrderedBatch);
    [GuardedPostgreSqlFact] public Task HeterogeneousBatchIgnoresOverfetchedIdentityCombinations() => Run(MetadataEvaluationScenario.HeterogeneousBatch);
    [GuardedPostgreSqlFact] public Task CancellationPropagatesWithoutTrackingOrWrites() => Run(MetadataEvaluationScenario.Cancellation);
    [GuardedPostgreSqlFact] public Task QueryBoundaryCancellationPropagatesWithoutTrackingOrWrites() => Run(MetadataEvaluationScenario.QueryCancellation);
    [GuardedPostgreSqlFact] public Task PostgreSqlErrorPropagatesWithoutTrackingOrWrites() => Run(MetadataEvaluationScenario.DatabaseFailure);
    [GuardedPostgreSqlFact] public Task ForeignKeyRejectsDanglingMatchMappingWithoutRelaxingGuards() => Run(MetadataEvaluationScenario.DanglingMatchConstraint);
    [GuardedPostgreSqlFact] public Task ForeignKeyRejectsMissingReferencedRelationshipWithoutRelaxingGuards() => Run(MetadataEvaluationScenario.MissingRelationshipConstraint);
    [GuardedPostgreSqlFact] public Task UnsupportedStatusIsRejected() => Run(MetadataEvaluationScenario.UnsupportedStatus);
}
