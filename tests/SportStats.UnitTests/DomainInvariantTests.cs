using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using Xunit;

namespace SportStats.UnitTests;

public sealed class DomainInvariantTests
{
    private static Season Season(string sportCode = "football") => new(new Competition(new Sport(sportCode, sportCode), "league", "League"), "2026");
    private static FootballSeasonTeam Participant(Season season, string name) => new(season, new FootballTeam(name));
    private static FootballStandingSnapshot Snapshot(Season season, DateTime? retrieved = null) =>
        new(season, "synthetic-provider", retrieved ?? DateTime.UtcNow, "opaque-table", "opaque-scope");

    [Fact]
    public void PlatformRejectsMissingIdentityTextAndReversedSeasonDates()
    {
        Assert.Throws<ArgumentException>(() => new Sport(Guid.Empty, "football", "Football"));
        Assert.Throws<ArgumentException>(() => new Sport(" ", "Football"));
        var sport = new Sport("football", "Football");
        Assert.Throws<ArgumentException>(() => new Competition(sport, "", "League"));
        var competition = new Competition(sport, "league", "League");
        Assert.Throws<ArgumentException>(() => new Season(competition, " "));
        Assert.Throws<ArgumentException>(() => new Season(competition, "2026", new DateOnly(2026, 12, 1), new DateOnly(2026, 1, 1)));
        var undated = new Season(competition, "unknown");
        Assert.Null(undated.StartDate);
        Assert.Null(undated.EndDate);
        Assert.False(typeof(Sport).GetProperty(nameof(Sport.Code))!.SetMethod!.IsPublic);
    }

    [Fact]
    public void FootballEntryPointsRejectSeasonsFromOtherSports()
    {
        var basketball = Season("basketball");
        var football = Season();
        var home = Participant(football, "Home");
        var away = Participant(football, "Away");
        Assert.Throws<ArgumentException>(() => new FootballSeasonTeam(basketball, home.Team));
        Assert.Throws<ArgumentException>(() => new FootballMatch(basketball, home, away));
        Assert.Throws<ArgumentException>(() => new FootballMatch(Guid.NewGuid(), basketball, home, away));
        Assert.Throws<ArgumentException>(() => Snapshot(basketball));
        Assert.Throws<ArgumentException>(() => new FootballStandingSnapshot(Guid.NewGuid(), basketball, "provider", DateTime.UtcNow, "table", "scope"));
    }

    [Fact]
    public void MatchRequiresDistinctParticipantsFromItsOwnSeason()
    {
        var season = Season();
        var home = Participant(season, "Home");
        var away = Participant(season, "Away");
        Assert.Throws<ArgumentException>(() => new FootballMatch(season, home, home));
        Assert.Throws<ArgumentException>(() => new FootballMatch(season, home, Participant(Season(), "Elsewhere")));
        Assert.Throws<ArgumentException>(() => new FootballMatch(season, Participant(Season(), "Elsewhere"), away));
        var match = new FootballMatch(season, home, away);
        Assert.Equal(home.TeamId, match.HomeTeamId);
        Assert.Equal(away.TeamId, match.AwayTeamId);
        Assert.Null(match.KickoffUtc);
        Assert.Null(match.HomeGoals);
        Assert.Null(match.AwayGoals);
        Assert.Equal(FootballMatchStatus.Unknown, match.Status);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, 0)]
    [InlineData(3, null)]
    [InlineData(0, 0)]
    [InlineData(9, 5)]
    public void ScoresPreserveMissingAndKnownValues(int? home, int? away)
    {
        var season = Season();
        var score = new FootballScore(home, away);
        var match = new FootballMatch(season, Participant(season, "Home"), Participant(season, "Away"), score: score);
        Assert.Equal(home, match.HomeGoals);
        Assert.Equal(away, match.AwayGoals);
        Assert.Equal(score, match.Score);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, -1)]
    [InlineData(-1, -2)]
    public void NegativeKnownScoresAreRejected(int? home, int? away) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FootballScore(home, away));

    [Theory]
    [InlineData(null)]
    [InlineData("unsupported-future-status")]
    [InlineData("999")]
    [InlineData("2")]
    [InlineData("Scheduled, Live")]
    public void UnsupportedStatusesAreUnknown(string? raw) => Assert.Equal(FootballMatchStatus.Unknown, FootballMatchStatusParser.Parse(raw));

    [Theory]
    [InlineData("scheduled", FootballMatchStatus.Scheduled)]
    [InlineData("FINISHED", FootballMatchStatus.Finished)]
    [InlineData("Live", FootballMatchStatus.Live)]
    public void NamedStatusesAreParsedCaseInsensitively(string raw, FootballMatchStatus expected) =>
        Assert.Equal(expected, FootballMatchStatusParser.Parse(raw));

    [Fact]
    public void InvalidEnumStatusFallsBackToUnknown()
    {
        var season = Season();
        var match = new FootballMatch(season, Participant(season, "Home"), Participant(season, "Away"), status: (FootballMatchStatus)999);
        Assert.Equal(FootballMatchStatus.Unknown, match.Status);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void TimestampsRequireExplicitUtc(DateTimeKind kind)
    {
        var season = Season();
        var timestamp = new DateTime(2026, 10, 12, 12, 0, 0, kind);
        Assert.Throws<ArgumentException>(() => new FootballMatch(season, Participant(season, "Home"), Participant(season, "Away"), kickoffUtc: timestamp));
        Assert.Throws<ArgumentException>(() => Snapshot(season, timestamp));
        var utc = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
        Assert.Equal(utc, Snapshot(season, utc).RetrievedAtUtc);
    }

    [Fact]
    public void StandingRowsRequireSnapshotSeasonMembershipButPermitTiedPositions()
    {
        var season = Season();
        var snapshot = Snapshot(season);
        Assert.Throws<ArgumentException>(() => new FootballStandingRow(snapshot, Participant(Season(), "Elsewhere"), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FootballStandingRow(snapshot, Participant(season, "Home"), 0));
        var first = new FootballStandingRow(snapshot, Participant(season, "Home"), 1);
        var second = new FootballStandingRow(snapshot, Participant(season, "Away"), 1);
        Assert.Equal(first.Position, second.Position);
        Assert.Equal(snapshot.SeasonId, first.SeasonId);
        Assert.Equal(snapshot.Id, first.SnapshotId);
    }
}
