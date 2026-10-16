using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;

namespace SportStats.Infrastructure.FootballData;

public sealed partial class FootballDataNormalizer
{
    private static readonly MatchObservationSource ObservationSource = new(ProviderKey, "/v4/competitions/PL/matches");

    public MatchProviderObservations NormalizeMatchObservations(string json, int queryYear, DateTime retrievedAtUtc)
    {
        ValidateYear(queryYear);
        if (retrievedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Retrieval time must be UTC.", nameof(retrievedAtUtc));
        var dto = Deserialize<ObservationMatchesDto>(json);
        var competition = Competition(dto.Competition);
        var matches = Required(dto.Matches, "matches").Select(value =>
        {
            var match = Required(value, "matches[]");
            if (Competition(match.Competition).Identity != competition.Identity) Fail("match.competition.id");
            var season = Season(match.Season, queryYear);
            var home = Identity(Required(match.HomeTeam, "match.homeTeam").Id, CompetitionScope, "match.homeTeam.id");
            var away = Identity(Required(match.AwayTeam, "match.awayTeam").Id, CompetitionScope, "match.awayTeam.id");
            if (home == away) Fail("match.teamIds");
            var status = ObservationField(match.Status, "match.status", element =>
            {
                var raw = ObservationText(element, "match.status");
                // https://docs.football-data.org/general/v4/match.html#_status_workflow_explained
                // The documented LIVE filter combines IN_PLAY and PAUSED.
                // AWARDED has no equivalent domain meaning; LIVE is only a query filter.
                return raw.Trim() switch
                {
                    "SCHEDULED" or "TIMED" => FootballMatchStatus.Scheduled,
                    "IN_PLAY" or "PAUSED" => FootballMatchStatus.Live,
                    "FINISHED" => FootballMatchStatus.Finished,
                    "SUSPENDED" => FootballMatchStatus.Suspended,
                    "POSTPONED" => FootballMatchStatus.Postponed,
                    "CANCELLED" => FootballMatchStatus.Cancelled,
                    _ => FootballMatchStatus.Unknown
                };
            });
            var fullTime = ObservationObjectMember(match.Score, "fullTime", "match.score");
            return new MatchProviderObservation(Identity(match.Id, SeasonScope(queryYear), "match.id"),
                ObservationSource, season, home, away,
                ObservationTimestamp(match.UtcDate, "match.utcDate"),
                ObservationInteger(match.Matchday, "match.matchday", 1), status,
                match.Status.ValueKind == JsonValueKind.String ? match.Status.GetString() : null,
                ObservationInteger(ObservationObjectMember(fullTime, "home", "match.score.fullTime"), "match.score.fullTime.home", 0),
                ObservationInteger(ObservationObjectMember(fullTime, "away", "match.score.fullTime"), "match.score.fullTime.away", 0),
                ObservationTimestamp(match.LastUpdated, "match.lastUpdated"), retrievedAtUtc);
        }).ToArray();
        Count(Required(dto.ResultSet, "resultSet").Count, matches.Length, "resultSet.count");
        Unique(matches.Select(x => x.Identity.ExternalId), "matches.id");
        if (matches.Select(x => x.Season).Distinct().Skip(1).Any()) Fail("matches.season");
        return new(competition, queryYear, Array.AsReadOnly(matches));
    }

    private static MatchObservationField<T> ObservationField<T>(JsonElement value, string field, Func<JsonElement, T> parse) where T : struct =>
        value.ValueKind switch
        {
            JsonValueKind.Undefined => MatchObservationField<T>.Missing,
            JsonValueKind.Null => MatchObservationField<T>.Null,
            _ => MatchObservationField<T>.FromValue(parse(value))
        };

    private static MatchObservationField<int> ObservationInteger(JsonElement value, string field, int minimum) =>
        ObservationField(value, field, element =>
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number) || number < minimum)
                throw new FormatException($"Invalid provider field: {field}.");
            return number;
        });

    private static string ObservationText(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.String) Fail(field);
        var text = value.GetString();
        Text(text, field);
        return text!;
    }

    private static MatchObservationField<DateTime> ObservationTimestamp(JsonElement value, string field) =>
        ObservationField(value, field, element =>
        {
            var text = ObservationText(element, field);
            if (!DateTimeOffset.TryParseExact(text, KickoffFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp) || timestamp.Offset != TimeSpan.Zero)
                throw new FormatException($"Invalid provider field: {field}.");
            return timestamp.UtcDateTime;
        });

    // Omitted containers provide no child observation. Null containers cannot establish
    // whether either individual goal was explicitly cleared, so reject them.
    private static JsonElement ObservationObjectMember(JsonElement parent, string name, string field)
    {
        if (parent.ValueKind == JsonValueKind.Undefined) return default;
        if (parent.ValueKind != JsonValueKind.Object) Fail(field);
        return parent.TryGetProperty(name, out var member) ? member : default;
    }
}

internal sealed class ObservationMatchesDto
{
    [JsonPropertyName("competition")] public CompetitionDto? Competition { get; init; }
    [JsonPropertyName("matches")] public List<ObservationMatchDto?>? Matches { get; init; }
    [JsonPropertyName("resultSet")] public ResultSetDto? ResultSet { get; init; }
}

internal sealed class ObservationMatchDto
{
    [JsonPropertyName("id")] public int? Id { get; init; }
    [JsonPropertyName("competition")] public CompetitionDto? Competition { get; init; }
    [JsonPropertyName("season")] public SeasonDto? Season { get; init; }
    [JsonPropertyName("homeTeam")] public TeamDto? HomeTeam { get; init; }
    [JsonPropertyName("awayTeam")] public TeamDto? AwayTeam { get; init; }
    [JsonPropertyName("utcDate")] public JsonElement UtcDate { get; init; }
    [JsonPropertyName("status")] public JsonElement Status { get; init; }
    [JsonPropertyName("matchday")] public JsonElement Matchday { get; init; }
    [JsonPropertyName("score")] public JsonElement Score { get; init; }
    [JsonPropertyName("lastUpdated")] public JsonElement LastUpdated { get; init; }
}
