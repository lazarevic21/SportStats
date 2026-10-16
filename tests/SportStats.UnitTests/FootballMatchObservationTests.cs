using System.Text.Json.Nodes;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Infrastructure.FootballData;
using Xunit;

namespace SportStats.UnitTests;

public sealed class FootballMatchObservationTests
{
    private readonly FootballDataNormalizer normalizer = new();
    private static readonly DateTime Retrieved = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static JsonObject Payload() => JsonNode.Parse("""
        {"competition":{"id":2021,"code":"PL","name":"Premier League"},"resultSet":{"count":1},"matches":[{
        "id":560542,"competition":{"id":2021,"code":"PL","name":"Premier League"},
        "season":{"id":2502,"startDate":"2026-08-21","endDate":"2027-05-30"},
        "homeTeam":{"id":57,"name":"Arsenal"},"awayTeam":{"id":65,"name":"Manchester City"},
        "utcDate":"2026-08-21T19:00:00Z","matchday":1,"status":"TIMED",
        "score":{"fullTime":{"home":0,"away":2}}}]}
        """)!.AsObject();
    private static JsonObject Match(JsonObject payload) => payload["matches"]![0]!.AsObject();
    private MatchProviderObservation Observe(JsonObject payload) => Assert.Single(
        normalizer.NormalizeMatchObservations(payload.ToJsonString(), 2026, Retrieved).Matches);

    [Theory]
    [InlineData("utcDate")]
    [InlineData("matchday")]
    [InlineData("status")]
    [InlineData("lastUpdated")]
    public void MetadataPreservesMissingNullAndValue(string member)
    {
        var payload = Payload();
        Match(payload).Remove(member);
        AssertPresence(Observe(payload), member, MatchObservationFieldPresence.Missing);
        Match(payload)[member] = null;
        AssertPresence(Observe(payload), member, MatchObservationFieldPresence.Null);
        Match(payload)[member] = member switch
        {
            "matchday" => JsonValue.Create(2),
            "status" => JsonValue.Create("FINISHED"),
            _ => JsonValue.Create("2026-10-16T11:00:00Z")
        };
        AssertPresence(Observe(payload), member, MatchObservationFieldPresence.Value);
    }

    private static void AssertPresence(MatchProviderObservation observation, string member, MatchObservationFieldPresence expected)
    {
        var actual = member switch
        {
            "utcDate" => observation.KickoffUtc.Presence,
            "matchday" => observation.Matchday.Presence,
            "status" => observation.Status.Presence,
            _ => observation.ProviderLastUpdatedUtc.Presence
        };
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("score", false)]
    [InlineData("score", true)]
    [InlineData("fullTime", false)]
    [InlineData("fullTime", true)]
    public void ScoreContainerPresenceIsPreserved(string container, bool explicitNull)
    {
        var payload = Payload();
        var parent = container == "score" ? Match(payload) : Match(payload)["score"]!.AsObject();
        if (explicitNull) parent[container] = null;
        else parent.Remove(container);
        if (explicitNull)
        {
            Assert.Throws<FormatException>(() => Observe(payload));
            return;
        }
        var result = Observe(payload);
        var expected = MatchObservationFieldPresence.Missing;
        Assert.Equal(expected, result.HomeGoals.Presence);
        Assert.Equal(expected, result.AwayGoals.Presence);
    }

    [Fact]
    public void GoalsRemainIndependentAndZeroIsAnExplicitValue()
    {
        var payload = Payload();
        var score = Match(payload)["score"]!["fullTime"]!.AsObject();
        score.Remove("away");
        var result = Observe(payload);
        Assert.Equal(MatchObservationFieldPresence.Value, result.HomeGoals.Presence);
        Assert.Equal(0, result.HomeGoals.Value);
        Assert.Equal(MatchObservationFieldPresence.Missing, result.AwayGoals.Presence);
        score["home"] = null;
        score["away"] = 0;
        result = Observe(payload);
        Assert.Equal(MatchObservationFieldPresence.Null, result.HomeGoals.Presence);
        Assert.Equal(0, result.AwayGoals.Value);
    }

    [Theory]
    [InlineData("SCHEDULED", FootballMatchStatus.Scheduled)]
    [InlineData("TIMED", FootballMatchStatus.Scheduled)]
    [InlineData("IN_PLAY", FootballMatchStatus.Live)]
    [InlineData("PAUSED", FootballMatchStatus.Live)]
    [InlineData("FINISHED", FootballMatchStatus.Finished)]
    [InlineData("POSTPONED", FootballMatchStatus.Postponed)]
    [InlineData("CANCELLED", FootballMatchStatus.Cancelled)]
    [InlineData("SUSPENDED", FootballMatchStatus.Suspended)]
    [InlineData("AWARDED", FootballMatchStatus.Unknown)]
    [InlineData("LIVE", FootballMatchStatus.Unknown)]
    [InlineData("FUTURE_STATUS", FootballMatchStatus.Unknown)]
    [InlineData("timed", FootballMatchStatus.Unknown)]
    public void StatusMappingPreservesRawTokens(string token, FootballMatchStatus expected)
    {
        var payload = Payload();
        Match(payload)["status"] = token;
        var result = Observe(payload);
        Assert.Equal(expected, result.Status.Value);
        Assert.Equal(token, result.RawProviderStatus);
    }

    [Fact]
    public void IdentitySourceAndTimesAreSeparateFromUntrustedPayloadMetadata()
    {
        var payload = Payload();
        Match(payload)["lastUpdated"] = "2026-10-15T11:00:00Z";
        Match(payload)["providerKey"] = "spoof";
        Match(payload)["source"] = "spoof";
        Match(payload)["retrievedAtUtc"] = "2000-01-01T00:00:00Z";
        var result = Observe(payload);
        Assert.Equal(new ProviderIdentity("football-data.org", "competition:PL/season:2026", "560542"), result.Identity);
        Assert.Equal("football-data.org", result.Source.ProviderKey);
        Assert.Equal("/v4/competitions/PL/matches", result.Source.Endpoint);
        Assert.Equal("57", result.HomeTeam.ExternalId);
        Assert.Equal("65", result.AwayTeam.ExternalId);
        Assert.Equal(Retrieved, result.RetrievedAtUtc);
        Assert.Equal(new DateTime(2026, 10, 15, 11, 0, 0, DateTimeKind.Utc), result.ProviderLastUpdatedUtc.Value);
    }

    [Theory]
    [InlineData("utcDate", "not-a-date")]
    [InlineData("utcDate", "2026-10-16T11:00:00")]
    [InlineData("utcDate", "2026-10-16T11:00:00+02:00")]
    [InlineData("lastUpdated", "not-a-date")]
    [InlineData("lastUpdated", "2026-10-16T11:00:00")]
    [InlineData("lastUpdated", "2026-10-16T11:00:00-01:00")]
    [InlineData("status", " ")]
    public void InvalidTextMetadataIsRejected(string field, string value)
    {
        var payload = Payload();
        Match(payload)[field] = value;
        Assert.Throws<FormatException>(() => Observe(payload));
    }

    [Theory]
    [InlineData("utcDate", "42")]
    [InlineData("lastUpdated", "42")]
    [InlineData("status", "42")]
    [InlineData("matchday", "0")]
    [InlineData("matchday", "-1")]
    [InlineData("matchday", "\"1\"")]
    [InlineData("score", "[]")]
    [InlineData("homeTeam", "null")]
    [InlineData("id", "0")]
    public void InvalidTypesAndValuesAreRejected(string field, string json)
    {
        var payload = Payload();
        Match(payload)[field] = JsonNode.Parse(json);
        Assert.Throws<FormatException>(() => Observe(payload));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"0\"")]
    [InlineData("true")]
    public void InvalidGoalsAreRejected(string json)
    {
        var payload = Payload();
        Match(payload)["score"]!["fullTime"]!["home"] = JsonNode.Parse(json);
        Assert.Throws<FormatException>(() => Observe(payload));
    }

    [Fact]
    public void NonUtcLocalRetrievalTimeIsRejected()
    {
        foreach (var kind in new[] { DateTimeKind.Local, DateTimeKind.Unspecified })
            Assert.Throws<ArgumentException>(() => normalizer.NormalizeMatchObservations(
                Payload().ToJsonString(), 2026, DateTime.SpecifyKind(Retrieved, kind)));
    }

    [Theory]
    [InlineData("2026-10-16T11:00:00Z")]
    [InlineData("2026-10-16T11:00:00+00:00")]
    [InlineData("2026-10-16T11:00:00.1234567Z")]
    public void ExplicitUtcTimestampsRemainUtc(string timestamp)
    {
        var payload = Payload();
        Match(payload)["utcDate"] = timestamp;
        Match(payload)["lastUpdated"] = timestamp;
        var result = Observe(payload);
        Assert.Equal(DateTimeKind.Utc, result.KickoffUtc.Value!.Value.Kind);
        Assert.Equal(result.KickoffUtc, result.ProviderLastUpdatedUtc);
    }

    [Theory]
    [InlineData(MatchObservationFieldPresence.Missing, 1)]
    [InlineData(MatchObservationFieldPresence.Null, 0)]
    [InlineData(MatchObservationFieldPresence.Value, null)]
    [InlineData((MatchObservationFieldPresence)99, null)]
    public void FieldContractRejectsContradictoryPresence(MatchObservationFieldPresence presence, int? value) =>
        Assert.Throws<ArgumentException>(() => new MatchObservationField<int>(presence, value));

    [Fact]
    public void DuplicateIdentitiesAndContradictoryRelationshipsAreRejected()
    {
        var payload = Payload();
        Match(payload)["awayTeam"]!["id"] = 57;
        Assert.Throws<FormatException>(() => Observe(payload));
        payload = Payload();
        Match(payload)["competition"]!["id"] = 9999;
        Assert.Throws<FormatException>(() => Observe(payload));
        payload = Payload();
        payload["matches"]!.AsArray().Add(Match(payload).DeepClone());
        payload["resultSet"]!["count"] = 2;
        Assert.Throws<FormatException>(() => Observe(payload));
    }

    [Fact]
    public void ReturnedBatchCannotBeMutatedThroughCollectionInterface()
    {
        var result = normalizer.NormalizeMatchObservations(Payload().ToJsonString(), 2026, Retrieved);
        var collection = Assert.IsAssignableFrom<IList<MatchProviderObservation>>(result.Matches);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"matches\":[],\"matches\":[]}")]
    public void MalformedPayloadsAreRejected(string json) => Assert.Throws<FormatException>(
        () => normalizer.NormalizeMatchObservations(json, 2026, Retrieved));

    [Fact]
    public void ObservationSupportDoesNotRelaxLegacyRequiredMetadata()
    {
        var payload = Payload();
        Match(payload).Remove("utcDate");
        Assert.Equal(MatchObservationFieldPresence.Missing, Observe(payload).KickoffUtc.Presence);
        Assert.Throws<FormatException>(() => normalizer.NormalizeMatches(payload.ToJsonString(), 2026));
        payload = Payload();
        Match(payload)["status"] = "IN_PLAY";
        Assert.Equal(FootballMatchStatus.Live, Observe(payload).Status.Value);
        Assert.Equal(FootballMatchStatus.Unknown, Assert.Single(normalizer.NormalizeMatches(payload.ToJsonString(), 2026).Matches).Status);
    }
}
