namespace SportStats.Domain;

internal static class DomainGuard
{
    internal static Guid Id(Guid value, string name) => value != Guid.Empty ? value : throw new ArgumentException("An identifier is required.", name);
    internal static string Text(string value, string name) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException("A value is required.", name);
    internal static void Football(Platform.Season season)
    {
        ArgumentNullException.ThrowIfNull(season);
        if (season.Competition.Sport.Code != "football") throw new ArgumentException("The season must belong to a football competition.", nameof(season));
    }
    internal static DateTime Utc(DateTime value, string name) => value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException("The timestamp must have UTC kind.", name);
}
