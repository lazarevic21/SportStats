namespace SportStats.Infrastructure.FootballData;

/// <summary>Supply ApiToken through runtime secret configuration, for example FootballData__ApiToken.</summary>
public sealed class FootballDataOptions
{
    public const string SectionName = "FootballData";
    public string ApiToken { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;
}
