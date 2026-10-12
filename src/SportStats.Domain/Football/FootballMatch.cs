using SportStats.Domain.Platform;

namespace SportStats.Domain.Football;

public sealed class FootballMatch
{
    private FootballMatch() { }
    public FootballMatch(Guid id, Season season, FootballSeasonTeam home, FootballSeasonTeam away,
        DateTime? kickoffUtc = null, FootballMatchStatus status = FootballMatchStatus.Unknown, int? matchday = null, FootballScore? score = null)
    {
        DomainGuard.Football(season);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(away);
        if (home.SeasonId != season.Id || away.SeasonId != season.Id) throw new ArgumentException("Both teams must participate in the match season.");
        if (home.TeamId == away.TeamId) throw new ArgumentException("Home and away teams must differ.");
        Id = DomainGuard.Id(id, nameof(id));
        Season = season;
        SeasonId = season.Id;
        HomeTeam = home.Team;
        HomeTeamId = home.TeamId;
        AwayTeam = away.Team;
        AwayTeamId = away.TeamId;
        KickoffUtc = kickoffUtc.HasValue ? DomainGuard.Utc(kickoffUtc.Value, nameof(kickoffUtc)) : null;
        Status = Enum.IsDefined(status) ? status : FootballMatchStatus.Unknown;
        Matchday = matchday;
        HomeGoals = score?.HomeGoals;
        AwayGoals = score?.AwayGoals;
    }
    public FootballMatch(Season season, FootballSeasonTeam home, FootballSeasonTeam away, DateTime? kickoffUtc = null,
        FootballMatchStatus status = FootballMatchStatus.Unknown, int? matchday = null, FootballScore? score = null)
        : this(Guid.NewGuid(), season, home, away, kickoffUtc, status, matchday, score) { }
    public Guid Id { get; private set; }
    public Guid SeasonId { get; private set; }
    public Season Season { get; private set; } = null!;
    public Guid HomeTeamId { get; private set; }
    public Guid AwayTeamId { get; private set; }
    public FootballTeam HomeTeam { get; private set; } = null!;
    public FootballTeam AwayTeam { get; private set; } = null!;
    public DateTime? KickoffUtc { get; private set; }
    public FootballMatchStatus Status { get; private set; }
    public int? Matchday { get; private set; }
    public int? HomeGoals { get; private set; }
    public int? AwayGoals { get; private set; }
    public FootballScore Score => new(HomeGoals, AwayGoals);
}
