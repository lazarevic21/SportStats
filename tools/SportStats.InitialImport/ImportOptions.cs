using System.Globalization;
namespace SportStats.InitialImport;
public sealed record ImportOptions(string Competition, int Year, bool Apply)
{
    public static bool TryParse(string[] args, out ImportOptions options)
    {
        options = new("PL", 0, false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var live = false;
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!seen.Add(argument)) return false;
            switch (argument)
            {
                case "--live": live = true; break;
                case "--apply": options = options with { Apply = true }; break;
                case "--competition" when i + 1 < args.Length:
                    options = options with { Competition = args[++i] }; break;
                case "--year" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var year)) return false;
                    options = options with { Year = year }; break;
                default: return false;
            }
        }
        return live && seen.Contains("--competition") && seen.Contains("--year") &&
            options.Competition == "PL" && options.Year is >= 1 and <= 9999;
    }
}

