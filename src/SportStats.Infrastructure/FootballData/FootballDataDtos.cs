using System.Text.Json;
using System.Text.Json.Serialization;

namespace SportStats.Infrastructure.FootballData;

internal sealed class CompetitionDto
{
    [JsonPropertyName("id")] public int? Id { get; init; }
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("currentSeason")] public SeasonDto? CurrentSeason { get; init; }
}
internal sealed class SeasonDto
{
    [JsonPropertyName("id")] public int? Id { get; init; }
    [JsonPropertyName("startDate")] public string? StartDate { get; init; }
    [JsonPropertyName("endDate")] public string? EndDate { get; init; }
}
internal sealed class TeamDto
{
    [JsonPropertyName("id")] public int? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("shortName")] public string? ShortName { get; init; }
    [JsonPropertyName("crest")] public string? Crest { get; init; }
}
internal sealed class TeamsDto
{
    [JsonPropertyName("count")] public int? Count { get; init; }
    [JsonPropertyName("competition")] public CompetitionDto? Competition { get; init; }
    [JsonPropertyName("season")] public SeasonDto? Season { get; init; }
    [JsonPropertyName("teams")] public List<TeamDto?>? Teams { get; init; }
}
internal sealed class MatchesDto
{
    [JsonPropertyName("competition")] public CompetitionDto? Competition { get; init; }
    [JsonPropertyName("matches")] public List<MatchDto?>? Matches { get; init; }
    [JsonPropertyName("resultSet")] public ResultSetDto? ResultSet { get; init; }
}
internal sealed class ResultSetDto
{
    [JsonPropertyName("count")] public int? Count { get; init; }
}
internal sealed class MatchDto
{
    [JsonPropertyName("id")] public int? Id { get; init; }
    [JsonPropertyName("competition")] public CompetitionDto? Competition { get; init; }
    [JsonPropertyName("season")] public SeasonDto? Season { get; init; }
    [JsonPropertyName("homeTeam")] public TeamDto? HomeTeam { get; init; }
    [JsonPropertyName("awayTeam")] public TeamDto? AwayTeam { get; init; }
    [JsonPropertyName("utcDate")] public string? UtcDate { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("matchday")] public int? Matchday { get; init; }
    [JsonPropertyName("score")] public ScoreDto? Score { get; init; }
}
internal sealed class ScoreDto
{
    [JsonPropertyName("fullTime")] public FullTimeDto? FullTime { get; init; }
}
internal sealed class FullTimeDto
{
    [JsonPropertyName("home")] public JsonElement Home { get; init; }
    [JsonPropertyName("away")] public JsonElement Away { get; init; }
}
