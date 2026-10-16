namespace SportStats.ProviderValidation;

public sealed record ValidationOptions(bool Live, string Competition = "PL", int? Year = null,
    bool Compare = false, int SpacingSeconds = 7)
{
    public static bool TryParse(string[] args, out ValidationOptions options)
    {
        options = new(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!seen.Add(arg)) return false;
            if (arg == "--live") options = options with { Live = true };
            else if (arg == "--compare") options = options with { Compare = true };
            else if (arg is "--competition" or "--year" or "--spacing-seconds")
            {
                if (++i >= args.Length) return false;
                var value = args[i];
                if (arg == "--competition") options = options with { Competition = value };
                else if (int.TryParse(value, out var number))
                    options = arg == "--year" ? options with { Year = number } : options with { SpacingSeconds = number };
                else return false;
            }
            else return false;
        }
        return options.IsValid;
    }

    public bool IsValid => Competition is { Length: > 0 and <= 64 } &&
        Competition.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') &&
        (Year is null or >= 2000 and <= 2100) && SpacingSeconds is >= 7 and <= 300;
}
