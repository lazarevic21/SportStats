using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Infrastructure.FootballData;
using SportStats.Infrastructure;
using Xunit;

namespace SportStats.UnitTests;

public sealed class FootballDataNormalizerTests
{
    private readonly IFootballPayloadNormalizer normalizer = new FootballDataNormalizer();
    private const string Competition = """{"id":2021,"code":"PL","name":"Premier League"}""";
    private const string Season = """{"id":2502,"startDate":"2026-08-21","endDate":"2027-05-30"}""";
    private static JsonObject CompetitionPayload() => JsonNode.Parse(Competition)!.AsObject();
    private static JsonObject TeamsPayload() => JsonNode.Parse($$$$"""
        {"count":2,"competition":{{{{Competition}}}},"season":{{{{Season}}}},"teams":[
          {"id":57,"name":"Arsenal FC","shortName":"Arsenal","crest":"https://example.test/57.svg"},
          {"id":65,"name":"Manchester City FC"}]}
        """)!.AsObject();
    private static JsonObject MatchesPayload() => JsonNode.Parse($$$$"""
        {"competition":{{{{Competition}}}},"resultSet":{"count":1},"matches":[{
          "id":560542,"competition":{{{{Competition}}}},"season":{{{{Season}}}},
          "homeTeam":{"id":57,"name":"Arsenal FC"},"awayTeam":{"id":65,"name":"Manchester City FC"},
          "utcDate":"2026-08-21T19:00:00Z","status":"TIMED","matchday":1,
          "score":{"fullTime":{"home":null,"away":null}}}]}
        """)!.AsObject();
    private static JsonObject Match(JsonObject payload) => payload["matches"]![0]!.AsObject();

    [Fact]
    public void CompetitionAndTeamsKeepProviderIdentitySeparateFromQueryYear()
    {
        var competition = CompetitionPayload();
        competition["currentSeason"] = JsonNode.Parse(Season);
        var result = normalizer.NormalizeCompetition(competition.ToJsonString(), 2026);
        Assert.Equal(new ProviderIdentity("football-data.org", "competition:PL", "2021"), result.Competition.Identity);
        Assert.Equal(new ProviderIdentity("football-data.org", "competition:PL/season:2026", "2502"), result.Season.Identity);
        Assert.Equal(2026, result.Season.QueryYear);
        Assert.Equal("2026/27", result.Season.Key);
        Assert.Equal(new DateOnly(2026, 8, 21), result.Season.StartDate);
        var teams = normalizer.NormalizeTeams(TeamsPayload().ToJsonString(), 2026);
        Assert.Equal(result.Season, teams.Season);
        Assert.Equal(new ProviderIdentity("football-data.org", "competition:PL", "57"), teams.Teams[0].Identity);
        Assert.Equal("Arsenal FC", teams.Teams[0].Name);
        Assert.Null(teams.Teams[1].ShortName);
        Assert.Null(teams.Teams[1].CrestUrl);
    }

    [Theory]
    [InlineData("TIMED", FootballMatchStatus.Scheduled)]
    [InlineData("FINISHED", FootballMatchStatus.Finished)]
    [InlineData("IN_PLAY", FootballMatchStatus.Unknown)]
    [InlineData("FUTURE_PROVIDER_STATUS", FootballMatchStatus.Unknown)]
    [InlineData(" TIMED", FootballMatchStatus.Scheduled)]
    [InlineData("TIMED ", FootballMatchStatus.Scheduled)]
    [InlineData(" FINISHED ", FootballMatchStatus.Finished)]
    [InlineData(" FUTURE_PROVIDER_STATUS ", FootballMatchStatus.Unknown)]
    [InlineData("timed", FootballMatchStatus.Unknown)]
    public void MatchesTranslateStatusesWithoutDiscardingProviderValue(string status, FootballMatchStatus expected)
    {
        var payload = MatchesPayload();
        Match(payload)["status"] = status;
        var match = Assert.Single(normalizer.NormalizeMatches(payload.ToJsonString(), 2026).Matches);
        Assert.Equal(expected, match.Status);
        Assert.Equal(status, match.ProviderStatus);
        Assert.Equal(new ProviderIdentity("football-data.org", "competition:PL/season:2026", "560542"), match.Identity);
        Assert.Equal("57", match.HomeTeam.ExternalId);
        Assert.Equal("65", match.AwayTeam.ExternalId);
        Assert.Equal("competition:PL", match.HomeTeam.IdentityScope);
    }

    [Fact]
    public void ScoresDistinguishMissingNullAndZeroIndependently()
    {
        var payload = MatchesPayload();
        var scores = Match(payload)["score"]!["fullTime"]!.AsObject();
        scores.Remove("home");
        var missing = Assert.Single(normalizer.NormalizeMatches(payload.ToJsonString(), 2026).Matches);
        Assert.Equal(new NormalizedScoreField(ScoreFieldPresence.Missing, null), missing.HomeGoals);
        Assert.Equal(new NormalizedScoreField(ScoreFieldPresence.Null, null), missing.AwayGoals);
        scores["home"] = 0;
        scores["away"] = 3;
        var values = Assert.Single(normalizer.NormalizeMatches(payload.ToJsonString(), 2026).Matches);
        Assert.Equal(new NormalizedScoreField(ScoreFieldPresence.Value, 0), values.HomeGoals);
        Assert.Equal(new NormalizedScoreField(ScoreFieldPresence.Value, 3), values.AwayGoals);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("TIME\nD")]
    [InlineData("TIMED\0")]
    public void BlankOrControlCharacterStatusesAreRejected(string status)
    {
        var payload = MatchesPayload();
        Match(payload)["status"] = status;
        var error = Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(payload.ToJsonString(), 2026));
        Assert.Equal("Invalid provider field: match.status.", error.Message);
    }

    [Theory]
    [InlineData("2026-08-21T19:00:00Z")]
    [InlineData("2026-08-21T21:00:00+02:00")]
    public void KickoffsNormalizeExplicitOffsetsToUtc(string timestamp)
    {
        var payload = MatchesPayload();
        Match(payload)["utcDate"] = timestamp;
        var actual = Assert.Single(normalizer.NormalizeMatches(payload.ToJsonString(), 2026).Matches).KickoffUtc;
        Assert.Equal(new DateTime(2026, 8, 21, 19, 0, 0, DateTimeKind.Utc), actual);
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
    }

    [Theory]
    [InlineData("2026-08-21T19:00:00.1Z", 1000000)]
    [InlineData("2026-08-21T19:00:00.1234567Z", 1234567)]
    [InlineData("2026-08-21T21:30:00.1234567+02:30", 1234567)]
    [InlineData("2026-08-21T14:00:00.0000001-05:00", 1)]
    public void FractionalKickoffsPreservePrecisionWhenNormalizingToUtc(string timestamp, int fractionalTicks)
    {
        var payload = MatchesPayload();
        Match(payload)["utcDate"] = timestamp;
        var actual = Assert.Single(normalizer.NormalizeMatches(payload.ToJsonString(), 2026).Matches).KickoffUtc;
        var expected = new DateTime(2026, 8, 21, 19, 0, 0, DateTimeKind.Utc).AddTicks(fractionalTicks);
        Assert.Equal(expected, actual);
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
    }

    [Fact]
    public void ConflictingProviderSeasonIdentitiesAcrossMatchesAreRejected()
    {
        var payload = MatchesPayload();
        var second = Match(payload).DeepClone().AsObject();
        second["id"] = 560543;
        second["season"]!["id"] = 2503;
        payload["matches"]!.AsArray().Add(second);
        payload["resultSet"]!["count"] = 2;

        var error = Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(payload.ToJsonString(), 2026));
        Assert.Equal("Invalid provider field: matches.season.", error.Message);

        second["season"]!["id"] = 2502;
        Assert.Equal(2, normalizer.NormalizeMatches(payload.ToJsonString(), 2026).Matches.Count);
    }

    [Theory]
    [InlineData("2026-08-21T19:00:00")]
    [InlineData("2026-08-21")]
    [InlineData("invalid")]
    [InlineData("2026-02-30T19:00:00Z")]
    public void InvalidOrUnzonedKickoffsAreRejected(string timestamp)
    {
        var payload = MatchesPayload();
        Match(payload)["utcDate"] = timestamp;
        Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(payload.ToJsonString(), 2026));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"0\"")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("2147483648")]
    public void MalformedScoresAreRejected(string score)
    {
        var payload = MatchesPayload();
        Match(payload)["score"]!["fullTime"]!["home"] = JsonNode.Parse(score);
        Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(payload.ToJsonString(), 2026));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("season")]
    [InlineData("competition")]
    [InlineData("homeTeam")]
    [InlineData("awayTeam")]
    [InlineData("status")]
    [InlineData("utcDate")]
    [InlineData("score")]
    public void RequiredMatchFieldsRejectMissingAndNull(string field)
    {
        var payload = MatchesPayload();
        Match(payload).Remove(field);
        Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(payload.ToJsonString(), 2026));
        Match(payload)[field] = null;
        Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(payload.ToJsonString(), 2026));
    }

    [Fact]
    public void MatchReferencesContainersAndMetadataRejectInvalidValues()
    {
        Action<JsonObject>[] mutations = [
            p => Match(p)["competition"]!["id"] = 2000,
            p => Match(p)["homeTeam"]!["id"] = 0,
            p => Match(p)["awayTeam"]!["id"] = 57,
            p => Match(p)["status"] = " ",
            p => Match(p)["matchday"] = 0,
            p => Match(p)["score"]!.AsObject().Remove("fullTime"),
            p => Match(p)["score"]!["fullTime"] = null,
            p => p["resultSet"]!["count"] = 2,
            p => p["matches"]!.AsArray().Add(null),
            p => { p["matches"]!.AsArray().Add(Match(p).DeepClone()); p["resultSet"]!["count"] = 2; }
        ];
        foreach (var mutate in mutations)
        {
            var payload = MatchesPayload();
            mutate(payload);
            Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(payload.ToJsonString(), 2026));
        }
    }

    [Fact]
    public void TeamIdentitiesNamesCollectionsAndCountAreValidated()
    {
        Action<JsonObject>[] mutations = [
            p => p["teams"]![0]!["id"] = -1,
            p => p["teams"]![0]!.AsObject().Remove("id"),
            p => p["teams"]![0]!["name"] = " ",
            p => p["teams"]![0]!["name"] = null,
            p => p["teams"]![1]!["id"] = 57,
            p => p["teams"]![0] = null,
            p => p["teams"] = null,
            p => p["count"] = 3,
            p => p["competition"]!["code"] = "BL1"
        ];
        foreach (var mutate in mutations)
        {
            var payload = TeamsPayload();
            mutate(payload);
            Assert.Throws<FormatException>(() => normalizer.NormalizeTeams(payload.ToJsonString(), 2026));
        }
    }

    [Theory]
    [InlineData("2026-8-21", "2027-05-30")]
    [InlineData("2026-02-30", "2027-05-30")]
    [InlineData("2027-08-21", "2028-05-30")]
    [InlineData("2026-08-21", "2026-08-20")]
    public void SeasonsRejectMalformedDatesWrongQueryYearAndReversedRange(string start, string end)
    {
        var payload = TeamsPayload();
        payload["season"]!["startDate"] = start;
        payload["season"]!["endDate"] = end;
        Assert.Throws<FormatException>(() => normalizer.NormalizeTeams(payload.ToJsonString(), 2026));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10000)]
    public void InvalidQueryYearsAreRejected(int year)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => normalizer.NormalizeCompetition("{}", year));
        Assert.Throws<ArgumentOutOfRangeException>(() => normalizer.NormalizeTeams("{}", year));
        Assert.Throws<ArgumentOutOfRangeException>(() => normalizer.NormalizeMatches("{}", year));
    }

    [Theory]
    [InlineData("{ invalid")]
    [InlineData("null")]
    [InlineData("[]")]
    public void InvalidRootPayloadsProduceSanitizedFailures(string json)
    {
        Assert.Throws<FormatException>(() => normalizer.NormalizeCompetition(json, 2026));
        Assert.Throws<FormatException>(() => normalizer.NormalizeTeams(json, 2026));
        Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(json, 2026));
    }

    [Fact]
    public void RepeatedNormalizationProducesStableIdentitiesAndReadOnlyCollections()
    {
        var json = MatchesPayload().ToJsonString();
        var first = normalizer.NormalizeMatches(json, 2026);
        var second = normalizer.NormalizeMatches(json, 2026);
        Assert.Equal(first.Matches[0], second.Matches[0]);
        var collection = Assert.IsAssignableFrom<IList<NormalizedMatch>>(first.Matches);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
    }

    [Fact]
    public void ErrorsDoNotEchoUntrustedFieldValuesOrJson()
    {
        const string untrusted = "synthetic-private-marker";
        var payload = MatchesPayload();
        Match(payload)["utcDate"] = untrusted;
        var error = Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(payload.ToJsonString(), 2026));
        Assert.DoesNotContain(untrusted, error.ToString());
        Assert.Null(error.InnerException);
        var malformed = Assert.Throws<FormatException>(() => normalizer.NormalizeMatches("{\"id\":\"" + untrusted, 2026));
        Assert.DoesNotContain(untrusted, malformed.ToString());
        Assert.Null(malformed.InnerException);
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejectedRatherThanOverwritingIdentity()
    {
        var payload = MatchesPayload().ToJsonString().Replace("\"id\":560542", "\"id\":560542,\"id\":560543", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(payload, 2026));
    }

    [Fact]
    public void NormalizerCanBeResolvedWithoutDatabaseConfiguration()
    {
        using var services = new ServiceCollection()
            .AddInfrastructure(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        Assert.IsType<FootballDataNormalizer>(services.GetRequiredService<IFootballPayloadNormalizer>());
    }
}


