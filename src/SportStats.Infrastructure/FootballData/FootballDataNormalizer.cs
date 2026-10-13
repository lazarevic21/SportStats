using System.Globalization;
using System.Text.Json;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;

namespace SportStats.Infrastructure.FootballData;

/// <summary>
/// Normalizes the observed Premier League contract without generating internal entity IDs.
/// Scopes describe this importer's boundary, not globally verified provider uniqueness.
/// </summary>
public sealed class FootballDataNormalizer : IFootballPayloadNormalizer
{
    public const string ProviderKey = "football-data.org";
    private const string CompetitionScope = "competition:PL";
    private static readonly JsonSerializerOptions JsonOptions = new() { AllowDuplicateProperties = false };
    private static readonly string[] KickoffFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    public NormalizedCompetitionSeason NormalizeCompetition(string json, int queryYear)
    {
        ValidateYear(queryYear);
        var dto = Deserialize<CompetitionDto>(json);
        return new(Competition(dto), Season(dto.CurrentSeason, queryYear));
    }

    public NormalizedTeams NormalizeTeams(string json, int queryYear)
    {
        ValidateYear(queryYear);
        var dto = Deserialize<TeamsDto>(json);
        var competition = Competition(dto.Competition);
        var season = Season(dto.Season, queryYear);
        var teams = Required(dto.Teams, "teams").Select(value =>
        {
            var team = Required(value, "teams[]");
            return new NormalizedTeam(Identity(team.Id, CompetitionScope, "team.id"),
                Text(team.Name, "team.name"), team.ShortName, team.Crest);
        }).ToArray();
        Count(dto.Count, teams.Length, "count");
        Unique(teams.Select(x => x.Identity.ExternalId), "teams.id");
        return new(competition, season, Array.AsReadOnly(teams));
    }

    public NormalizedMatches NormalizeMatches(string json, int queryYear)
    {
        ValidateYear(queryYear);
        var dto = Deserialize<MatchesDto>(json);
        var competition = Competition(dto.Competition);
        var matches = Required(dto.Matches, "matches").Select(value =>
        {
            var match = Required(value, "matches[]");
            var nestedCompetition = Competition(match.Competition);
            if (nestedCompetition.Identity != competition.Identity) Fail("match.competition.id");
            var season = Season(match.Season, queryYear);
            var home = Required(match.HomeTeam, "match.homeTeam");
            var away = Required(match.AwayTeam, "match.awayTeam");
            var homeId = Identity(home.Id, CompetitionScope, "match.homeTeam.id");
            var awayId = Identity(away.Id, CompetitionScope, "match.awayTeam.id");
            Text(home.Name, "match.homeTeam.name");
            Text(away.Name, "match.awayTeam.name");
            if (homeId == awayId) Fail("match.teamIds");
            Text(match.Status, "match.status");
            var rawStatus = match.Status!;
            // Trim only for case-sensitive token matching; preserve the original text separately.
            var status = rawStatus.Trim() switch
            {
                "TIMED" => FootballMatchStatus.Scheduled,
                "FINISHED" => FootballMatchStatus.Finished,
                _ => FootballMatchStatus.Unknown
            };
            if (match.Matchday is <= 0) Fail("match.matchday");
            var fullTime = Required(Required(match.Score, "match.score").FullTime, "match.score.fullTime");
            return new NormalizedMatch(Identity(match.Id, SeasonScope(queryYear), "match.id"),
                season, homeId, awayId, Kickoff(match.UtcDate), status, rawStatus, match.Matchday,
                Score(fullTime.Home, "match.score.fullTime.home"), Score(fullTime.Away, "match.score.fullTime.away"));
        }).ToArray();
        Count(Required(dto.ResultSet, "resultSet").Count, matches.Length, "resultSet.count");
        Unique(matches.Select(x => x.Identity.ExternalId), "matches.id");
        if (matches.Select(x => x.Season).Distinct().Skip(1).Any()) Fail("matches.season");
        return new(competition, queryYear, Array.AsReadOnly(matches));
    }

    private static T Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Provider payload must be a JSON object.");
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? throw new FormatException("Provider payload must be a JSON object."); }
        catch (JsonException) { throw new FormatException("Provider payload contains malformed JSON or field types."); }
    }

    private static NormalizedCompetition Competition(CompetitionDto? value)
    {
        var dto = Required(value, "competition");
        var code = Text(dto.Code, "competition.code");
        if (code != "PL") Fail("competition.code");
        return new(Identity(dto.Id, CompetitionScope, "competition.id"), code, Text(dto.Name, "competition.name"));
    }

    private static NormalizedSeason Season(SeasonDto? value, int year)
    {
        var dto = Required(value, "season");
        var start = Date(dto.StartDate, "season.startDate");
        var end = Date(dto.EndDate, "season.endDate");
        if (start.Year != year || end < start) Fail("season.dateRange");
        var key = string.Create(CultureInfo.InvariantCulture, $"{start.Year:D4}/{end.Year % 100:D2}");
        return new(Identity(dto.Id, SeasonScope(year), "season.id"), year, key, start, end);
    }

    private static ProviderIdentity Identity(int? id, string scope, string field)
    {
        if (id is null or <= 0) Fail(field);
        return new(ProviderKey, scope, id!.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static string SeasonScope(int year) =>
        string.Create(CultureInfo.InvariantCulture, $"{CompetitionScope}/season:{year}");

    private static NormalizedScoreField Score(JsonElement value, string field)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return new(ScoreFieldPresence.Missing, null);
        if (value.ValueKind == JsonValueKind.Null) return new(ScoreFieldPresence.Null, null);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var goals) || goals < 0)
            throw new FormatException($"Invalid provider field: {field}.");
        return new(ScoreFieldPresence.Value, goals);
    }

    private static DateOnly Date(string? value, string field)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new FormatException($"Invalid provider field: {field}.");
        return date;
    }

    private static DateTime Kickoff(string? value)
    {
        if (!DateTimeOffset.TryParseExact(value, KickoffFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            throw new FormatException("Invalid provider field: match.utcDate.");
        return date.UtcDateTime;
    }

    private static string Text(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) Fail(field);
        return value!.Trim();
    }

    private static T Required<T>(T? value, string field) where T : class =>
        value ?? throw new FormatException($"Invalid provider field: {field}.");

    private static void Count(int? value, int actual, string field)
    {
        if (value is null || value != actual) Fail(field);
    }

    private static void Unique(IEnumerable<string> ids, string field)
    {
        if (ids.GroupBy(x => x, StringComparer.Ordinal).Any(x => x.Count() > 1)) Fail(field);
    }

    private static void ValidateYear(int year)
    {
        if (year is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(year));
    }

    private static void Fail(string field) => throw new FormatException($"Invalid provider field: {field}.");
}
