using System.Text.Json;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;

namespace SportStats.Application.Football.InitialImport;

/// <summary>Validates the complete fetched snapshot before resolving any database identity.</summary>
public sealed class InitialImportValidator(IFootballPayloadNormalizer normalizer,
    IFootballMatchObservationNormalizer observations)
{
    public InitialImportBundle Normalize(string competitionJson, string teamsJson, string matchesJson,
        int year, DateTime retrievedAtUtc)
    {
        var competition = normalizer.NormalizeCompetition(competitionJson, year);
        var teams = normalizer.NormalizeTeams(teamsJson, year);
        var matches = normalizer.NormalizeMatches(matchesJson, year);
        var observed = observations.NormalizeMatchObservations(matchesJson, year, retrievedAtUtc);
        if (competition.Competition.Code != InitialImportProfile.PremierLeague.CompetitionCode || competition.Competition != teams.Competition ||
            competition.Competition != matches.Competition || competition.Competition != observed.Competition ||
            competition.Season != teams.Season || matches.QueryYear != year ||
            teams.Teams.Count is < 2 or > 100 || matches.Matches.Count is < 1 or > 5000)
            Fail("snapshot.selection");
        var teamIds = teams.Teams.Select(team => team.Identity).ToHashSet();
        if (teamIds.Count != teams.Teams.Count || observed.Matches.Count != matches.Matches.Count)
            Fail("snapshot.identities");
        using var document = JsonDocument.Parse(matchesJson, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var rawMatches = document.RootElement.GetProperty("matches").EnumerateArray().ToArray();
        for (var index = 0; index < matches.Matches.Count; index++)
        {
            var match = matches.Matches[index];
            var observation = observed.Matches[index];
            if (match.Season != competition.Season || !teamIds.Contains(match.HomeTeam) ||
                !teamIds.Contains(match.AwayTeam) || match.HomeTeam == match.AwayTeam ||
                match.Identity != observation.Identity || match.Matchday is null or <= 0 ||
                observation.KickoffUtc.Presence != MatchObservationFieldPresence.Value ||
                match.KickoffUtc.Kind != DateTimeKind.Utc ||
                DateOnly.FromDateTime(match.KickoffUtc) < competition.Season.StartDate ||
                DateOnly.FromDateTime(match.KickoffUtc) > competition.Season.EndDate)
                Fail("match.relationships");
            var scheduled = match.ProviderStatus == "TIMED" && match.Status == FootballMatchStatus.Scheduled &&
                match.HomeGoals.Presence == ScoreFieldPresence.Null && match.AwayGoals.Presence == ScoreFieldPresence.Null;
            var finished = match.ProviderStatus == "FINISHED" && match.Status == FootballMatchStatus.Finished &&
                match.HomeGoals.Presence == ScoreFieldPresence.Value && match.HomeGoals.Value >= 0 &&
                match.AwayGoals.Presence == ScoreFieldPresence.Value && match.AwayGoals.Value >= 0;
            if (!scheduled && !finished) Fail("match.statusScoreContract");
            // Full-time goals only have the intended meaning for regular-duration league matches.
            if (!rawMatches[index].GetProperty("score").TryGetProperty("duration", out var duration) ||
                duration.ValueKind != JsonValueKind.String || duration.GetString() != "REGULAR")
                Fail("match.score.duration");
        }
        return new(competition.Competition, competition.Season,
            Array.AsReadOnly(teams.Teams.ToArray()), Array.AsReadOnly(matches.Matches.ToArray()));
    }

    private static void Fail(string field) => throw new FormatException($"Invalid import field: {field}.");
}
