namespace SportStats.Domain.Football;

/// <summary>Known goals without unverified period, extra-time, or penalty semantics.</summary>
public sealed record FootballScore
{
    public FootballScore(int? homeGoals, int? awayGoals)
    {
        if (homeGoals < 0) throw new ArgumentOutOfRangeException(nameof(homeGoals));
        if (awayGoals < 0) throw new ArgumentOutOfRangeException(nameof(awayGoals));
        HomeGoals = homeGoals;
        AwayGoals = awayGoals;
    }
    public int? HomeGoals { get; }
    public int? AwayGoals { get; }
}
