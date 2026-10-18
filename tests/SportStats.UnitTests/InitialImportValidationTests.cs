using System.Text.Json.Nodes;
using SportStats.Application.Football.InitialImport;
using SportStats.Infrastructure.FootballData;
using Xunit;

namespace SportStats.UnitTests;

public sealed class InitialImportValidationTests
{
    private const string Competition = """{"id":2021,"code":"PL","name":"Premier League"}""";
    private const string Season = """{"id":2502,"startDate":"2026-08-21","endDate":"2027-05-30"}""";
    private static string CompetitionJson => $$$$"""{"id":2021,"code":"PL","name":"Premier League","currentSeason":{{{{Season}}}}}""";
    private static string TeamsJson => $$$$"""{"count":2,"competition":{{{{Competition}}}},"season":{{{{Season}}}},"teams":[{"id":57,"name":"Home"},{"id":65,"name":"Away"}]}""";
    private static JsonObject Matches() => JsonNode.Parse($$$$"""{"competition":{{{{Competition}}}},"resultSet":{"count":1},"matches":[{"id":560542,"competition":{{{{Competition}}}},"season":{{{{Season}}}},"homeTeam":{"id":57,"name":"Home"},"awayTeam":{"id":65,"name":"Away"},"utcDate":"2026-08-21T19:00:00Z","status":"TIMED","matchday":1,"score":{"duration":"REGULAR","fullTime":{"home":null,"away":null}}}]}""")!.AsObject();
    private static InitialImportBundle Normalize(JsonObject matches, string? teams = null, string? competition = null)
    {
        var normalizer = new FootballDataNormalizer();
        return new InitialImportValidator(normalizer, normalizer).Normalize(competition ?? CompetitionJson,
            teams ?? TeamsJson, matches.ToJsonString(), 2026, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ValidBundleKeepsExactProviderIdentitiesAndSymbolicParents()
    {
        var bundle = Normalize(Matches());
        var batch = bundle.ToBatch(Guid.NewGuid());
        Assert.Equal("football-data.org", bundle.Competition.Identity.ProviderKey);
        Assert.Equal("competition:PL", bundle.Teams[0].Identity.IdentityScope);
        Assert.Equal("competition:PL/season:2026", bundle.Matches[0].Identity.IdentityScope);
        Assert.Equal("560542", Assert.Single(batch.Matches).Identity.ExternalId);
        Assert.Equal(2, batch.Teams.Count);
    }

    [Theory]
    [InlineData("SCHEDULED")]
    [InlineData("IN_PLAY")]
    [InlineData("AWARDED")]
    [InlineData("FUTURE_STATUS")]
    [InlineData(" timed ")]
    public void UnsupportedStatusIsRejected(string status)
    {
        var payload = Matches(); payload["matches"]![0]!["status"] = status;
        Assert.Throws<FormatException>(() => Normalize(payload));
    }

    [Theory]
    [InlineData("homeTeam", 999)]
    [InlineData("awayTeam", 57)]
    public void MissingOrSelfParticipantIsRejected(string team, int id)
    {
        var payload = Matches(); payload["matches"]![0]![team]!["id"] = id;
        Assert.Throws<FormatException>(() => Normalize(payload));
    }

    [Fact]
    public void DuplicateTeamIdentityIsRejected()
    {
        var teams = JsonNode.Parse(TeamsJson)!; teams["teams"]![1]!["id"] = 57;
        Assert.Throws<FormatException>(() => Normalize(Matches(), teams.ToJsonString()));
    }

    [Fact]
    public void CrossResponseSeasonDriftIsRejected()
    {
        var teams = JsonNode.Parse(TeamsJson)!; teams["season"]!["id"] = 999;
        Assert.Throws<FormatException>(() => Normalize(Matches(), teams.ToJsonString()));
    }

    [Theory]
    [InlineData("PD")]
    [InlineData("BL1")]
    [InlineData("SA")]
    [InlineData("pl")]
    [InlineData(" PL")]
    public void UnreviewedLeagueConfigurationFailsClosed(string code) =>
        Assert.Throws<ArgumentException>(() => InitialImportProfile.Select(code));

    [Fact]
    public void FinishedZeroScoreIsPreserved()
    {
        var payload = Matches(); var match = payload["matches"]![0]!;
        match["status"] = "FINISHED"; match["score"]!["fullTime"]!["home"] = 0; match["score"]!["fullTime"]!["away"] = 2;
        var result = Assert.Single(Normalize(payload).Matches);
        Assert.Equal(0, result.HomeGoals.Value); Assert.Equal(2, result.AwayGoals.Value);
    }

    [Fact]
    public void MissingScoreNeverBecomesZero()
    {
        var payload = Matches(); var match = payload["matches"]![0]!;
        match["status"] = "FINISHED"; match["score"]!["fullTime"]!.AsObject().Remove("home");
        match["score"]!["fullTime"]!["away"] = 2;
        Assert.Throws<FormatException>(() => Normalize(payload));
    }
}

