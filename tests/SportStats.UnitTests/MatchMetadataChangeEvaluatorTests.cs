using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.FootballData;
using Xunit;

namespace SportStats.UnitTests;

public sealed class MatchMetadataChangeEvaluatorTests
{
    private static readonly Guid MatchId = Guid.NewGuid(), SeasonId = Guid.NewGuid(), HomeId = Guid.NewGuid(), AwayId = Guid.NewGuid();
    private static readonly DateTime Utc = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static ProviderIdentity Identity(string id) => new("provider", "scope", id);
    private static MatchMetadataSnapshot Canonical => new(MatchId, SeasonId, HomeId, AwayId, Utc, 1, FootballMatchStatus.Scheduled, 1, 2);
    private static MatchProviderObservation Observation => new(Identity("match"), new("provider", "/matches"),
        new(Identity("season"), 2026, "2026", new(2026, 8, 1), new(2027, 6, 1)), Identity("home"), Identity("away"),
        MatchObservationField<DateTime>.FromValue(Utc), MatchObservationField<int>.FromValue(1),
        MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Scheduled), "TIMED",
        MatchObservationField<int>.FromValue(1), MatchObservationField<int>.FromValue(2), default, Utc);
    private static MatchMetadataIdentitySnapshot Identities => new([Canonical], [SeasonId], [HomeId, AwayId],
        [new(MatchMetadataIdentityKind.Match, Identity("match"), MatchId),
         new(MatchMetadataIdentityKind.Season, Identity("season"), SeasonId),
         new(MatchMetadataIdentityKind.Team, Identity("home"), HomeId),
         new(MatchMetadataIdentityKind.Team, Identity("away"), AwayId)]);
    private static MatchMetadataEvaluation Evaluate(MatchProviderObservation input, MatchMetadataIdentitySnapshot? identities = null) =>
        new MatchMetadataChangeEvaluator().Evaluate(input, identities ?? Identities);

    [Fact]
    public void IdenticalObservationIsUnchangedButFreshnessRemainsUnverified()
    {
        var result = Evaluate(Observation);
        Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, result.Disposition);
        Assert.Empty(result.Changes);
        Assert.Equal(MatchId, result.MatchId);
        Assert.Equal(MatchMetadataFreshness.FreshnessUnverified, result.Freshness);
    }

    [Theory]
    [InlineData("kickoff")]
    [InlineData("matchday")]
    [InlineData("status")]
    [InlineData("home")]
    [InlineData("away")]
    public void EachFieldChangesIndependently(string field)
    {
        var input = field switch
        {
            "kickoff" => Observation with { KickoffUtc = MatchObservationField<DateTime>.FromValue(Utc.AddDays(1)) },
            "matchday" => Observation with { Matchday = MatchObservationField<int>.FromValue(2) },
            "status" => Observation with { Status = MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Live), RawProviderStatus = "IN_PLAY" },
            "home" => Observation with { HomeGoals = MatchObservationField<int>.FromValue(0) },
            _ => Observation with { AwayGoals = MatchObservationField<int>.FromValue(0) }
        };
        var result = Evaluate(input);
        Assert.Equal(MatchMetadataEvaluationDisposition.ChangesDetected, result.Disposition);
        Assert.Single(result.Changes);
        Assert.Equal(MatchMetadataFreshness.FreshnessUnverified, result.Freshness);
        Assert.False(result.IsUpdateAuthorized);
        var change = Assert.Single(result.Changes);
        Assert.Equal(MatchObservationFieldPresence.Value, change.Presence);
        if (field == "home" || field == "away")
        {
            var goals = Assert.IsType<MatchMetadataChange<int>>(change);
            Assert.Equal(field == "home" ? 1 : 2, goals.Before);
            Assert.Equal(0, goals.After);
        }
        else if (field == "kickoff")
        {
            var kickoff = Assert.IsType<MatchMetadataChange<DateTime>>(change);
            Assert.Equal(Utc, kickoff.Before);
            Assert.Equal(Utc.AddDays(1), kickoff.After);
        }
        else if (field == "matchday")
        {
            var matchday = Assert.IsType<MatchMetadataChange<int>>(change);
            Assert.Equal(1, matchday.Before);
            Assert.Equal(2, matchday.After);
        }
        else
        {
            var status = Assert.IsType<MatchMetadataChange<FootballMatchStatus>>(change);
            Assert.Equal(FootballMatchStatus.Scheduled, status.Before);
            Assert.Equal(FootballMatchStatus.Live, status.After);
        }
    }

    [Fact]
    public void MissingPreservesAllMetadata()
    {
        var result = Evaluate(Observation with { KickoffUtc = default, Matchday = default, Status = default, RawProviderStatus = null, HomeGoals = default, AwayGoals = default });
        Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, result.Disposition);
        Assert.Empty(result.Changes);
    }

    [Theory]
    [InlineData("FUTURE_STATUS")]
    [InlineData("AWARDED")]
    [InlineData("LIVE")]
    [InlineData("timed")]
    public void UnsupportedProviderTokensFromNormalizerAreRejected(string token)
    {
        var payload = """
            {"competition":{"id":2021,"code":"PL","name":"Premier League"},"resultSet":{"count":1},"matches":[{
            "id":560542,"competition":{"id":2021,"code":"PL","name":"Premier League"},
            "season":{"id":2502,"startDate":"2026-08-21","endDate":"2027-05-30"},
            "homeTeam":{"id":57,"name":"Arsenal"},"awayTeam":{"id":65,"name":"Manchester City"},
            "utcDate":"2026-10-16T12:00:00Z","matchday":1,"status":"TOKEN",
            "score":{"fullTime":{"home":1,"away":2}}}]}
            """.Replace("TOKEN", token, StringComparison.Ordinal);
        var observed = Assert.Single(new FootballDataNormalizer().NormalizeMatchObservations(payload, 2026, Utc).Matches);
        var ids = Identities with { Mappings = [
            new(MatchMetadataIdentityKind.Match, observed.Identity, MatchId),
            new(MatchMetadataIdentityKind.Season, observed.Season.Identity, SeasonId),
            new(MatchMetadataIdentityKind.Team, observed.HomeTeam, HomeId),
            new(MatchMetadataIdentityKind.Team, observed.AwayTeam, AwayId)] };
        Assert.Equal(token, observed.RawProviderStatus);
        Assert.Equal(FootballMatchStatus.Unknown, observed.Status.Value);
        var result = Evaluate(observed, ids);
        Assert.Equal(MatchMetadataEvaluationDisposition.InvalidInput, result.Disposition);
        Assert.Empty(result.Changes);
        Assert.False(result.IsUpdateAuthorized);
    }

    [Fact]
    public void NullClearsAllNullableFieldsIndependently()
    {
        var result = Evaluate(Observation with { KickoffUtc = MatchObservationField<DateTime>.Null, Matchday = MatchObservationField<int>.Null, HomeGoals = MatchObservationField<int>.Null, AwayGoals = MatchObservationField<int>.Null });
        Assert.Equal(MatchMetadataEvaluationDisposition.ChangesDetected, result.Disposition);
        Assert.Equal(4, result.Changes.Count);
        Assert.All(result.Changes, x => Assert.Equal(MatchObservationFieldPresence.Null, x.Presence));
        Assert.All(result.Changes.OfType<MatchMetadataChange<int>>(), x => Assert.Null(x.After));
        Assert.Null(Assert.Single(result.Changes.OfType<MatchMetadataChange<DateTime>>()).After);
    }

    [Theory]
    [InlineData("null-status")]
    [InlineData("unknown-status")]
    [InlineData("undefined-status")]
    [InlineData("nonutc-kickoff")]
    [InlineData("nonutc-lastupdated")]
    [InlineData("nonutc-retrieved")]
    [InlineData("zero-matchday")]
    [InlineData("negative-matchday")]
    [InlineData("negative-home")]
    [InlineData("negative-away")]
    public void InvalidMetadataIsRejectedAtEvaluatorBoundary(string scenario)
    {
        var input = scenario switch
        {
            "null-status" => Observation with { Status = MatchObservationField<FootballMatchStatus>.Null, RawProviderStatus = null },
            "unknown-status" => Observation with { Status = MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Unknown), RawProviderStatus = "FUTURE_STATUS" },
            "undefined-status" => Observation with { Status = MatchObservationField<FootballMatchStatus>.FromValue((FootballMatchStatus)999) },
            "nonutc-kickoff" => Observation with { KickoffUtc = MatchObservationField<DateTime>.FromValue(DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified)) },
            "nonutc-lastupdated" => Observation with { ProviderLastUpdatedUtc = MatchObservationField<DateTime>.FromValue(DateTime.SpecifyKind(Utc, DateTimeKind.Local)) },
            "nonutc-retrieved" => Observation with { RetrievedAtUtc = DateTime.SpecifyKind(Utc, DateTimeKind.Unspecified) },
            "zero-matchday" => Observation with { Matchday = MatchObservationField<int>.FromValue(0) },
            "negative-matchday" => Observation with { Matchday = MatchObservationField<int>.FromValue(-1) },
            "negative-home" => Observation with { HomeGoals = MatchObservationField<int>.FromValue(-1) },
            _ => Observation with { AwayGoals = MatchObservationField<int>.FromValue(-1) }
        };
        Assert.Equal(MatchMetadataEvaluationDisposition.InvalidInput, Evaluate(input).Disposition);
    }

    [Theory]
    [InlineData("missing", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("duplicate", IdentityResolutionFailureCode.DuplicateMappings)]
    [InlineData("ambiguous", IdentityResolutionFailureCode.AmbiguousMappings)]
    [InlineData("dangling", IdentityResolutionFailureCode.MissingReference)]
    public void MatchMappingMustResolveStrictly(string scenario, IdentityResolutionFailureCode expected)
    {
        var ids = Identities;
        ids = scenario switch
        {
            "missing" => ids with { Mappings = ids.Mappings.Where(x => x.Kind != MatchMetadataIdentityKind.Match).ToArray() },
            "duplicate" => ids with { Mappings = [.. ids.Mappings, ids.Mappings[0]] },
            "ambiguous" => ids with { Mappings = [.. ids.Mappings, new(MatchMetadataIdentityKind.Match, Identity("match"), Guid.NewGuid())] },
            _ => ids with { Matches = [] }
        };
        var result = Evaluate(Observation, ids);
        Assert.Equal(MatchMetadataEvaluationDisposition.Conflict, result.Disposition);
        Assert.Equal(expected, result.Failure!.Code);
        Assert.Empty(result.Changes);
    }

    [Theory]
    [InlineData("season", "missing", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("season", "duplicate", IdentityResolutionFailureCode.DuplicateMappings)]
    [InlineData("season", "ambiguous", IdentityResolutionFailureCode.AmbiguousMappings)]
    [InlineData("season", "dangling", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("home", "missing", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("home", "duplicate", IdentityResolutionFailureCode.DuplicateMappings)]
    [InlineData("home", "ambiguous", IdentityResolutionFailureCode.AmbiguousMappings)]
    [InlineData("home", "dangling", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("away", "missing", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("away", "duplicate", IdentityResolutionFailureCode.DuplicateMappings)]
    [InlineData("away", "ambiguous", IdentityResolutionFailureCode.AmbiguousMappings)]
    [InlineData("away", "dangling", IdentityResolutionFailureCode.MissingReference)]
    public void RelationshipMappingsMustResolveStrictly(string field, string scenario, IdentityResolutionFailureCode code)
    {
        var ids = Identities;
        var mapping = ids.Mappings.Single(x => x.Identity == Identity(field));
        ids = scenario switch
        {
            "missing" => ids with { Mappings = ids.Mappings.Where(x => x != mapping).ToArray() },
            "duplicate" => ids with { Mappings = [.. ids.Mappings, mapping] },
            "ambiguous" => ids with { Mappings = [.. ids.Mappings, mapping with { EntityId = Guid.NewGuid() }] },
            "dangling" when field == "season" => ids with { SeasonIds = [] },
            _ => ids with { TeamIds = ids.TeamIds.Where(x => x != mapping.EntityId).ToArray() }
        };
        var result = Evaluate(Observation, ids);
        Assert.Equal(MatchMetadataEvaluationDisposition.Conflict, result.Disposition);
        Assert.Equal(code, result.Failure!.Code);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void NullOnAlreadyNullNullableFieldsIsUnchanged()
    {
        var ids = Identities with { Matches = [Canonical with { KickoffUtc = null, Matchday = null, HomeGoals = null, AwayGoals = null }] };
        var result = Evaluate(Observation with { KickoffUtc = MatchObservationField<DateTime>.Null,
            Matchday = MatchObservationField<int>.Null, HomeGoals = MatchObservationField<int>.Null, AwayGoals = MatchObservationField<int>.Null }, ids);
        Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, result.Disposition);
    }

    [Fact]
    public void NullAndValueProposalsConflictInEitherArrivalOrder()
    {
        var clear = Observation with { HomeGoals = MatchObservationField<int>.Null };
        foreach (var batch in new[] { new[] { clear, Observation }, new[] { Observation, clear } })
        {
            var result = new MatchMetadataChangeEvaluator().EvaluateBatch(batch, Identities);
            Assert.All(result, x => Assert.Equal(MatchMetadataEvaluationDisposition.Conflict, x.Disposition));
        }
    }

    [Theory]
    [InlineData("season")]
    [InlineData("home")]
    [InlineData("away")]
    public void ImmutableRelationshipsCannotBeReplaced(string field)
    {
        var ids = Identities;
        ids = ids with { Matches = [Canonical with { SeasonId = field == "season" ? Guid.NewGuid() : SeasonId,
            HomeTeamId = field == "home" ? AwayId : HomeId, AwayTeamId = field == "away" ? HomeId : AwayId }] };
        Assert.Equal(MatchMetadataEvaluationDisposition.Conflict, Evaluate(Observation, ids).Disposition);
    }

    [Fact]
    public void RepeatedObservationsAreDeterministicAndCompatible()
    {
        var results = new MatchMetadataChangeEvaluator().EvaluateBatch([Observation, Observation], Identities);
        Assert.Equal(2, results.Count);
        Assert.All(results, x => Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, x.Disposition));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContradictionsAreDetectedIncludingTrustedAliases(bool alias)
    {
        var ids = Identities;
        var other = Observation with { HomeGoals = MatchObservationField<int>.FromValue(2) };
        if (alias)
        {
            other = other with { Identity = Identity("alias") };
            ids = ids with { Mappings = [.. ids.Mappings, new(MatchMetadataIdentityKind.Match, other.Identity, MatchId)] };
        }
        var results = new MatchMetadataChangeEvaluator().EvaluateBatch([Observation, other], ids);
        Assert.All(results, x => Assert.Equal(MatchMetadataEvaluationDisposition.Conflict, x.Disposition));
    }

    [Fact]
    public void MissingProposalDoesNotContradictExplicitValue()
    {
        var results = new MatchMetadataChangeEvaluator().EvaluateBatch([
            Observation with { HomeGoals = default }, Observation with { HomeGoals = MatchObservationField<int>.FromValue(9) }], Identities);
        Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, results[0].Disposition);
        Assert.Equal(MatchMetadataEvaluationDisposition.ChangesDetected, results[1].Disposition);
    }

    [Fact]
    public void ProviderTimesAndApparentProgressNeverAuthorizeFreshness()
    {
        var result = Evaluate(Observation with { RetrievedAtUtc = Utc.AddDays(30), ProviderLastUpdatedUtc = MatchObservationField<DateTime>.FromValue(Utc.AddDays(29)),
            Status = MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Finished), RawProviderStatus = "FINISHED", HomeGoals = MatchObservationField<int>.FromValue(9) });
        Assert.Equal(MatchMetadataEvaluationDisposition.ChangesDetected, result.Disposition);
        Assert.Equal(MatchMetadataFreshness.FreshnessUnverified, result.Freshness);
    }

    [Fact]
    public void SnapshotEvaluationDoesNotMutateDomainEntity()
    {
        var season = new Season(SeasonId, new Competition(new Sport("football", "Football"), "PL", "Premier League"), "2026");
        var home = new FootballSeasonTeam(season, new FootballTeam(HomeId, "Home"));
        var away = new FootballSeasonTeam(season, new FootballTeam(AwayId, "Away"));
        var entity = new FootballMatch(MatchId, season, home, away, Utc, FootballMatchStatus.Scheduled, 1, new(1, 2));
        var before = MatchMetadataSnapshot.FromMatch(entity);
        Evaluate(Observation with { HomeGoals = MatchObservationField<int>.FromValue(9) }, Identities with { Matches = [before] });
        Assert.Equal(before, MatchMetadataSnapshot.FromMatch(entity));
    }
}
