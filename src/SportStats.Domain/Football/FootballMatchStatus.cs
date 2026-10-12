namespace SportStats.Domain.Football;

public enum FootballMatchStatus { Unknown, Scheduled, Live, Finished, Postponed, Cancelled, Suspended }

public static class FootballMatchStatusParser
{
    public static FootballMatchStatus Parse(string? value)
    {
        foreach (var status in Enum.GetValues<FootballMatchStatus>())
        {
            if (string.Equals(value, status.ToString(), StringComparison.OrdinalIgnoreCase))
                return status;
        }
        return FootballMatchStatus.Unknown;
    }
}
