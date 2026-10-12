using SportStats.Domain.Platform;

namespace SportStats.Domain.Football;

public sealed class FootballSeasonTeam
{
    private FootballSeasonTeam() { }
    public FootballSeasonTeam(Season season, FootballTeam team)
    {
        DomainGuard.Football(season);
        ArgumentNullException.ThrowIfNull(team);
        Season = season;
        SeasonId = season.Id;
        Team = team;
        TeamId = team.Id;
    }
    public Guid SeasonId { get; private set; }
    public Guid TeamId { get; private set; }
    public Season Season { get; private set; } = null!;
    public FootballTeam Team { get; private set; } = null!;
}
