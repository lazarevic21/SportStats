using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;
using SportStats.Application.Football.MatchSynchronization;
using SportStats.Domain.Football;
using Xunit;

namespace SportStats.UnitTests;

public sealed class MatchSynchronizationPolicyTests
{
    private static readonly Guid MatchId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SeasonId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid HomeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AwayId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTime Utc = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static ProviderIdentity Identity(string id, string provider = "provider") => new(provider, "scope", id);
    private static MatchMetadataSnapshot Baseline(FootballMatchStatus status = FootballMatchStatus.Scheduled) =>
        new(MatchId, SeasonId, HomeId, AwayId, Utc, 1, status, 0, 0);
    private static MatchProviderObservation Observation(FootballMatchStatus status = FootballMatchStatus.Scheduled) =>
        new(Identity("match"), new("provider", "/matches"),
            new(Identity("season"), 2026, "2026", new(2026, 8, 1), new(2027, 6, 1)), Identity("home"), Identity("away"),
            MatchObservationField<DateTime>.FromValue(Utc), MatchObservationField<int>.FromValue(1),
            MatchObservationField<FootballMatchStatus>.FromValue(status), status.ToString(),
            MatchObservationField<int>.FromValue(0), MatchObservationField<int>.FromValue(0), default, Utc);
    private static MatchMetadataIdentitySnapshot Identities(MatchMetadataSnapshot baseline) => new([baseline], [SeasonId], [HomeId, AwayId],
        [new(MatchMetadataIdentityKind.Match, Identity("match"), MatchId),
         new(MatchMetadataIdentityKind.Season, Identity("season"), SeasonId),
         new(MatchMetadataIdentityKind.Team, Identity("home"), HomeId),
         new(MatchMetadataIdentityKind.Team, Identity("away"), AwayId)]);
    private static MatchSynchronizationDecision Evaluate(MatchProviderObservation observation, MatchMetadataSnapshot? baseline = null) =>
        new MatchSynchronizationPolicyEngine().Evaluate(observation, Identities(baseline ?? Baseline()));

    public static IEnumerable<object[]> StatusPairs()
    {
        foreach (var before in Enum.GetValues<FootballMatchStatus>())
        foreach (var after in Enum.GetValues<FootballMatchStatus>())
        {
            var outcome = after == FootballMatchStatus.Unknown ? MatchSynchronizationOutcome.Rejected :
                before == after ? MatchSynchronizationOutcome.NoChange :
                (before == FootballMatchStatus.Scheduled && after is FootballMatchStatus.Live or FootballMatchStatus.Finished) ||
                (before == FootballMatchStatus.Live && after == FootballMatchStatus.Finished)
                    ? MatchSynchronizationOutcome.InsufficientEvidence : MatchSynchronizationOutcome.RequiresReview;
            yield return [before, after, outcome];
        }
    }

    [Theory]
    [MemberData(nameof(StatusPairs))]
    public void EveryCanonicalStatusPairHasConservativeOutcome(FootballMatchStatus before, FootballMatchStatus after,
        MatchSynchronizationOutcome expected)
    {
        var decision = Evaluate(Observation(after), Baseline(before));
        Assert.Equal(expected, decision.Outcome);
        Assert.False(decision.IsUpdateAuthorized);
        Assert.NotEqual(MatchSynchronizationOutcome.Approved, decision.Outcome);
    }

    [Fact]
    public void ZeroGoalReplayAndAllMissingAreNoChange()
    {
        var observation = Observation();
        Assert.Equal(MatchSynchronizationOutcome.NoChange, Evaluate(observation).Outcome);
        Assert.Equal(MatchSynchronizationOutcome.NoChange, Evaluate(observation with
        { KickoffUtc = default, Matchday = default, Status = default, HomeGoals = default, AwayGoals = default }).Outcome);
        var batch = new MatchSynchronizationPolicyEngine().EvaluateBatch([observation, observation], Identities(Baseline()));
        Assert.All(batch, d => { Assert.Equal(MatchSynchronizationOutcome.NoChange, d.Outcome); Assert.False(d.IsUpdateAuthorized); });
    }

    [Theory]
    [InlineData("kickoff")]
    [InlineData("matchday")]
    [InlineData("home")]
    [InlineData("away")]
    public void ExplicitNullableClearRequiresReviewButAlreadyNullDoesNot(string field)
    {
        var observation = field switch
        {
            "kickoff" => Observation() with { KickoffUtc = MatchObservationField<DateTime>.Null },
            "matchday" => Observation() with { Matchday = MatchObservationField<int>.Null },
            "home" => Observation() with { HomeGoals = MatchObservationField<int>.Null },
            _ => Observation() with { AwayGoals = MatchObservationField<int>.Null }
        };
        Assert.Equal(MatchSynchronizationOutcome.RequiresReview, Evaluate(observation).Outcome);
        var baseline = field switch
        {
            "kickoff" => Baseline() with { KickoffUtc = null },
            "matchday" => Baseline() with { Matchday = null },
            "home" => Baseline() with { HomeGoals = null },
            _ => Baseline() with { AwayGoals = null }
        };
        Assert.Equal(MatchSynchronizationOutcome.NoChange, Evaluate(observation, baseline).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalizationCannotRetainAnUnobservedFinalGoal(bool explicitNull)
    {
        var baseline = Baseline(FootballMatchStatus.Live) with { AwayGoals = null };
        var observation = Observation(FootballMatchStatus.Finished) with
        { AwayGoals = explicitNull ? MatchObservationField<int>.Null : default };
        Assert.Equal(MatchSynchronizationOutcome.InsufficientEvidence, Evaluate(observation, baseline).Outcome);
        Assert.Equal(MatchSynchronizationOutcome.RequiresReview, Evaluate(observation with
        { AwayGoals = MatchObservationField<int>.Null }, Baseline(FootballMatchStatus.Live)).Outcome);
    }

    [Theory]
    [InlineData(FootballMatchStatus.Scheduled, MatchSynchronizationOutcome.RequiresReview)]
    [InlineData(FootballMatchStatus.Live, MatchSynchronizationOutcome.InsufficientEvidence)]
    [InlineData(FootballMatchStatus.Finished, MatchSynchronizationOutcome.RequiresReview)]
    [InlineData(FootballMatchStatus.Postponed, MatchSynchronizationOutcome.RequiresReview)]
    [InlineData(FootballMatchStatus.Suspended, MatchSynchronizationOutcome.RequiresReview)]
    [InlineData(FootballMatchStatus.Cancelled, MatchSynchronizationOutcome.RequiresReview)]
    public void ScoreChangesRespectCanonicalStateEvenWithStatusMissing(FootballMatchStatus status, MatchSynchronizationOutcome expected)
    {
        var observation = Observation(status) with { Status = default, HomeGoals = MatchObservationField<int>.FromValue(1) };
        Assert.Equal(expected, Evaluate(observation, Baseline(status)).Outcome);
    }

    [Theory]
    [InlineData(FootballMatchStatus.Scheduled, MatchSynchronizationOutcome.InsufficientEvidence)]
    [InlineData(FootballMatchStatus.Live, MatchSynchronizationOutcome.RequiresReview)]
    [InlineData(FootballMatchStatus.Finished, MatchSynchronizationOutcome.RequiresReview)]
    public void ReschedulingAndMatchdayChangesAreStateSensitive(FootballMatchStatus status, MatchSynchronizationOutcome expected)
    {
        Assert.Equal(expected, Evaluate(Observation(status) with { KickoffUtc = MatchObservationField<DateTime>.FromValue(Utc.AddDays(7)) }, Baseline(status)).Outcome);
        Assert.Equal(expected, Evaluate(Observation(status) with { Matchday = MatchObservationField<int>.FromValue(2) }, Baseline(status)).Outcome);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(0)]
    [InlineData(30)]
    public void ProviderAndRetrievalTimesCannotPromoteLiveScoreChange(int days)
    {
        var observation = Observation(FootballMatchStatus.Live) with { HomeGoals = MatchObservationField<int>.FromValue(1),
            ProviderLastUpdatedUtc = MatchObservationField<DateTime>.FromValue(Utc.AddDays(days)), RetrievedAtUtc = Utc.AddDays(days + 40) };
        Assert.Equal(MatchSynchronizationOutcome.InsufficientEvidence, Evaluate(observation, Baseline(FootballMatchStatus.Live)).Outcome);
        Assert.Equal(MatchSynchronizationOutcome.InsufficientEvidence, Evaluate(observation with { HomeGoals = MatchObservationField<int>.FromValue(0) },
            Baseline(FootballMatchStatus.Live) with { HomeGoals = 1 }).Outcome);
    }

    [Theory]
    [InlineData("AWARDED")]
    [InlineData("FUTURE_STATUS")]
    public void UnsupportedNormalizedStatusesRejectWholeMatch(string raw)
    {
        Assert.Equal(MatchSynchronizationOutcome.Rejected, Evaluate(Observation(FootballMatchStatus.Unknown) with
        { RawProviderStatus = raw, Matchday = MatchObservationField<int>.FromValue(2) }).Outcome);
    }

    [Theory]
    [InlineData("null-status")]
    [InlineData("undefined-status")]
    [InlineData("negative-goal")]
    [InlineData("zero-matchday")]
    [InlineData("nonutc")]
    public void InvalidInputCannotSalvageValidFields(string scenario)
    {
        var observation = scenario switch
        {
            "null-status" => Observation() with { Status = MatchObservationField<FootballMatchStatus>.Null },
            "undefined-status" => Observation() with { Status = MatchObservationField<FootballMatchStatus>.FromValue((FootballMatchStatus)999) },
            "negative-goal" => Observation() with { HomeGoals = MatchObservationField<int>.FromValue(-1) },
            "zero-matchday" => Observation() with { Matchday = MatchObservationField<int>.FromValue(0) },
            _ => Observation() with { RetrievedAtUtc = DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified) }
        };
        Assert.Equal(MatchSynchronizationOutcome.Rejected, Evaluate(observation with
        { KickoffUtc = MatchObservationField<DateTime>.FromValue(Utc.AddDays(1)) }).Outcome);
    }

    [Theory]
    [InlineData("season")]
    [InlineData("home")]
    [InlineData("away")]
    public void ImmutableRelationshipChangesAreRejected(string field)
    {
        var baseline = field switch
        {
            "season" => Baseline() with { SeasonId = Guid.NewGuid() },
            "home" => Baseline() with { HomeTeamId = AwayId },
            _ => Baseline() with { AwayTeamId = HomeId }
        };
        Assert.Equal(MatchSynchronizationOutcome.Rejected, Evaluate(Observation(), baseline).Outcome);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("ambiguous")]
    public void FailedExactIdentityIsRejected(string scenario)
    {
        var identities = Identities(Baseline());
        identities = identities with { Mappings = scenario switch
        {
            "missing" => identities.Mappings.Skip(1).ToArray(),
            "duplicate" => [.. identities.Mappings, identities.Mappings[0]],
            _ => [.. identities.Mappings, identities.Mappings[0] with { EntityId = Guid.NewGuid() }]
        }};
        Assert.Equal(MatchSynchronizationOutcome.Rejected, new MatchSynchronizationPolicyEngine().Evaluate(Observation(), identities).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AliasAndProviderConflictsIncludeBaselineEqualObservation(bool otherProvider)
    {
        var first = Observation(FootballMatchStatus.Live);
        var second = first with { Identity = Identity("alias", otherProvider ? "other" : "provider"),
            HomeGoals = MatchObservationField<int>.FromValue(1) };
        if (otherProvider)
            second = second with { Source = new("other", "/matches"), Season = second.Season with { Identity = Identity("season", "other") },
                HomeTeam = Identity("home", "other"), AwayTeam = Identity("away", "other") };
        var identities = Identities(Baseline(FootballMatchStatus.Live));
        identities = identities with { Mappings = [.. identities.Mappings,
            new(MatchMetadataIdentityKind.Match, second.Identity, MatchId),
            .. (otherProvider ? new MatchMetadataIdentityMapping[] {
                new(MatchMetadataIdentityKind.Season, second.Season.Identity, SeasonId),
                new(MatchMetadataIdentityKind.Team, second.HomeTeam, HomeId), new(MatchMetadataIdentityKind.Team, second.AwayTeam, AwayId) } : [])] };
        foreach (var batch in new[] { new[] { first, second }, new[] { second, first } })
            Assert.All(new MatchSynchronizationPolicyEngine().EvaluateBatch(batch, identities), decision =>
            { Assert.Equal(MatchSynchronizationOutcome.RequiresReview, decision.Outcome); Assert.False(decision.IsUpdateAuthorized); });
    }

    [Fact]
    public void TypedInputEmptyBatchAndCallerCollectionMutationAreSafe()
    {
        var engine = new MatchSynchronizationPolicyEngine();
        Assert.Empty(engine.Evaluate(new MatchSynchronizationPolicyInput([], Identities(Baseline()))));
        var observations = new List<MatchProviderObservation> { Observation(FootballMatchStatus.Live) };
        var mappings = Identities(Baseline()).Mappings.ToList();
        var identities = Identities(Baseline()) with { Mappings = mappings };
        var decision = Assert.Single(engine.Evaluate(new MatchSynchronizationPolicyInput(observations, identities)));
        observations.Clear();
        mappings.Clear();
        Assert.Single(decision.Observations);
        Assert.Single(decision.EvaluatorResults);
        Assert.Single(decision.Candidates);
        Assert.Equal(5, decision.FieldAssessments.Count);
        Assert.Single(Assert.Single(decision.EvaluatorResults).Changes);
        Assert.Single(Assert.Single(decision.Candidates).Changes);
        Assert.Throws<NotSupportedException>(() => ((IList<MatchProviderObservation>)decision.Observations).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<MatchMetadataChange>)Assert.Single(decision.Candidates).Changes).Clear());
    }

    [Fact]
    public void KnownMatchRelationshipFailureBlocksValidRowForSameMatch()
    {
        var valid = Observation();
        var swapped = valid with { HomeTeam = valid.AwayTeam, AwayTeam = valid.HomeTeam };
        var decision = Assert.Single(new MatchSynchronizationPolicyEngine().EvaluateBatch([valid, swapped], Identities(Baseline())));
        Assert.Equal(MatchSynchronizationOutcome.Rejected, decision.Outcome);
        Assert.Contains(MatchSynchronizationReasonCode.ImmutableRelationshipConflict, decision.ReasonCodes);
        Assert.Equal(2, decision.EvaluatorResults.Count);
        Assert.False(decision.IsUpdateAuthorized);
    }

    [Fact]
    public void ExplicitUnchangedScoreCounterpartSatisfiesPairPresenceButNotTrust()
    {
        var observation = Observation(FootballMatchStatus.Live) with { HomeGoals = MatchObservationField<int>.FromValue(1) };
        var decision = Evaluate(observation, Baseline(FootballMatchStatus.Live));
        Assert.Equal(MatchSynchronizationOutcome.InsufficientEvidence, decision.Outcome);
        Assert.DoesNotContain(MatchSynchronizationReasonCode.ExplicitScorePairRequired, decision.ReasonCodes);
        Assert.Contains(MatchSynchronizationReasonCode.OrderingUnverified, decision.ReasonCodes);
        Assert.Contains(MatchSynchronizationReasonCode.AuthorityUnverified, decision.ReasonCodes);
        var final = Evaluate(observation with { Status = MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Finished) }, Baseline(FootballMatchStatus.Live));
        Assert.DoesNotContain(MatchSynchronizationReasonCode.ExplicitFinalScorePairRequired, final.ReasonCodes);
        Assert.Equal(MatchSynchronizationOutcome.InsufficientEvidence, final.Outcome);
    }

    [Fact]
    public void EngineHasNoInjectedDatabaseNetworkClockOrTrustDependencies()
    {
        var constructor = Assert.Single(typeof(MatchSynchronizationPolicyEngine).GetConstructors());
        Assert.Empty(constructor.GetParameters());
        Assert.Empty(typeof(MatchSynchronizationPolicyEngine).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public));
        Assert.All(typeof(MatchSynchronizationPolicyEngine).GetMethods().Where(m => m.DeclaringType == typeof(MatchSynchronizationPolicyEngine)),
            method => Assert.All(method.GetParameters(), parameter => Assert.Contains(parameter.ParameterType,
                new[] { typeof(MatchSynchronizationPolicyInput), typeof(MatchProviderObservation),
                    typeof(MatchMetadataIdentitySnapshot), typeof(IReadOnlyList<MatchProviderObservation>) })));
    }

    [Fact]
    public void PartialLiveScoreRequiresPairAndPreservesMissingCounterpart()
    {
        var baseline = Baseline(FootballMatchStatus.Live) with { AwayGoals = 2 };
        var observation = Observation(FootballMatchStatus.Live) with
        { HomeGoals = MatchObservationField<int>.FromValue(1), AwayGoals = default };
        var decision = Evaluate(observation, baseline);
        Assert.Equal(MatchSynchronizationOutcome.InsufficientEvidence, decision.Outcome);
        Assert.Contains(MatchSynchronizationReasonCode.ExplicitScorePairRequired, decision.ReasonCodes);
        var candidate = Assert.Single(decision.Candidates).ResultingMetadata;
        Assert.Equal(1, candidate.HomeGoals);
        Assert.Equal(2, candidate.AwayGoals);
        Assert.Equal(baseline.MatchId, candidate.MatchId);
        Assert.Equal(baseline.SeasonId, candidate.SeasonId);
        Assert.Equal(baseline.HomeTeamId, candidate.HomeTeamId);
        Assert.Equal(baseline.AwayTeamId, candidate.AwayTeamId);
        var away = Assert.IsType<MatchSynchronizationFieldAssessment<int>>(
            Assert.Single(decision.FieldAssessments, f => f.Field == MatchMetadataField.AwayGoals));
        Assert.Equal(MatchObservationFieldPresence.Missing, away.Presence);
        Assert.Equal(2, away.Before);
        Assert.Equal(2, away.After);
        Assert.False(decision.Evidence.HasVerifiedAuthority);
        Assert.Equal(MatchMetadataFreshness.FreshnessUnverified, decision.Evidence.Freshness);
    }

    [Fact]
    public void CompatiblePartialAliasesCannotManufactureCoherentFinalPair()
    {
        var first = Observation(FootballMatchStatus.Finished) with { AwayGoals = default };
        var second = first with { Identity = Identity("alias"), HomeGoals = default,
            AwayGoals = MatchObservationField<int>.FromValue(0) };
        var identities = Identities(Baseline(FootballMatchStatus.Live));
        identities = identities with { Mappings = [.. identities.Mappings,
            new(MatchMetadataIdentityKind.Match, second.Identity, MatchId)] };
        var decision = Assert.Single(new MatchSynchronizationPolicyEngine().EvaluateBatch([first, second], identities));
        Assert.Equal(MatchSynchronizationOutcome.InsufficientEvidence, decision.Outcome);
        Assert.Contains(MatchSynchronizationReasonCode.ExplicitFinalScorePairRequired, decision.ReasonCodes);
        Assert.Equal(2, decision.Candidates.Count);
        Assert.All(decision.Observations, observation => Assert.True(
            observation.HomeGoals.Presence == MatchObservationFieldPresence.Missing ||
            observation.AwayGoals.Presence == MatchObservationFieldPresence.Missing));
    }

    [Fact]
    public void NullCandidatePreservesClearAndExplicitUnchangedZeroDiagnostics()
    {
        var decision = Evaluate(Observation() with { KickoffUtc = MatchObservationField<DateTime>.Null });
        Assert.Null(Assert.Single(decision.Candidates).ResultingMetadata.KickoffUtc);
        var kickoff = Assert.IsType<MatchSynchronizationFieldAssessment<DateTime>>(
            Assert.Single(decision.FieldAssessments, f => f.Field == MatchMetadataField.KickoffUtc));
        Assert.Equal(MatchObservationFieldPresence.Null, kickoff.Presence);
        Assert.Equal(Utc, kickoff.Before);
        Assert.Null(kickoff.After);
        var home = Assert.IsType<MatchSynchronizationFieldAssessment<int>>(
            Assert.Single(decision.FieldAssessments, f => f.Field == MatchMetadataField.HomeGoals));
        Assert.Equal(MatchObservationFieldPresence.Value, home.Presence);
        Assert.Equal(0, home.After);
    }

    [Fact]
    public void ReviewOfScoreBlocksOrdinaryMetadataChangesAtomicallyAndDeterministically()
    {
        var baseline = Baseline();
        var observation = Observation() with { KickoffUtc = MatchObservationField<DateTime>.FromValue(Utc.AddDays(1)),
            HomeGoals = MatchObservationField<int>.FromValue(1) };
        var first = Evaluate(observation, baseline);
        Assert.Equal(MatchSynchronizationOutcome.RequiresReview, first.Outcome);
        Assert.False(first.IsUpdateAuthorized);
        Assert.Equal(2, Assert.Single(first.EvaluatorResults).Changes.Count);
        Assert.NotEmpty(first.ReasonCodes);
        for (var i = 0; i < 5; i++)
        {
            var repeated = Evaluate(observation, baseline);
            Assert.Equal(first.Outcome, repeated.Outcome);
            Assert.Equal(first.ReasonCodes, repeated.ReasonCodes);
        }
        Assert.Equal(Utc, baseline.KickoffUtc);
        Assert.Equal(0, baseline.HomeGoals);
    }
}
