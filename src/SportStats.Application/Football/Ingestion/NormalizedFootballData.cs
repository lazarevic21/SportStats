using SportStats.Domain.Football;

namespace SportStats.Application.Football.Ingestion;

public sealed record ProviderIdentity(string ProviderKey, string IdentityScope, string ExternalId);
public enum ScoreFieldPresence { Missing, Null, Value }
/// <summary>Missing is an omitted JSON member; Null is an explicit null; Value includes zero.</summary>
public sealed record NormalizedScoreField(ScoreFieldPresence Presence, int? Value);
public sealed record NormalizedCompetition(ProviderIdentity Identity, string Code, string Name);
public sealed record NormalizedSeason(ProviderIdentity Identity, int QueryYear, string Key, DateOnly StartDate, DateOnly EndDate);
public sealed record NormalizedCompetitionSeason(NormalizedCompetition Competition, NormalizedSeason Season);
public sealed record NormalizedTeam(ProviderIdentity Identity, string Name, string? ShortName, string? CrestUrl);
public sealed record NormalizedTeams(NormalizedCompetition Competition, NormalizedSeason Season, IReadOnlyList<NormalizedTeam> Teams);
public sealed record NormalizedMatch(ProviderIdentity Identity, NormalizedSeason Season,
    ProviderIdentity HomeTeam, ProviderIdentity AwayTeam, DateTime KickoffUtc,
    FootballMatchStatus Status, string ProviderStatus, int? Matchday,
    NormalizedScoreField HomeGoals, NormalizedScoreField AwayGoals);
public sealed record NormalizedMatches(NormalizedCompetition Competition, int QueryYear, IReadOnlyList<NormalizedMatch> Matches);

/// <summary>Pure JSON normalization; implementations perform no HTTP or persistence operations.</summary>
public interface IFootballPayloadNormalizer
{
    NormalizedCompetitionSeason NormalizeCompetition(string json, int queryYear);
    NormalizedTeams NormalizeTeams(string json, int queryYear);
    NormalizedMatches NormalizeMatches(string json, int queryYear);
}
