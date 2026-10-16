using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;

namespace SportStats.Application.Football.MatchMetadata;

public sealed record MatchMetadataSnapshot(Guid MatchId, Guid SeasonId, Guid HomeTeamId, Guid AwayTeamId,
    DateTime? KickoffUtc, int? Matchday, FootballMatchStatus Status, int? HomeGoals, int? AwayGoals)
{
    public static MatchMetadataSnapshot FromMatch(FootballMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return new(match.Id, match.SeasonId, match.HomeTeamId, match.AwayTeamId,
            match.KickoffUtc, match.Matchday, match.Status, match.HomeGoals, match.AwayGoals);
    }
}

public enum MatchMetadataIdentityKind { Match, Season, Team }
public sealed record MatchMetadataIdentityMapping(MatchMetadataIdentityKind Kind, ProviderIdentity Identity, Guid EntityId);

/// <summary>
/// Authoritative existing mappings and entity snapshots supplied by a trusted caller, never by a provider payload.
/// Load separately using read-only queries. Preserve every mapping row, including duplicates. No natural-key fallback
/// or identity creation is permitted. This is a trust boundary, not a mechanism to authenticate arbitrary input.
/// </summary>
public sealed record MatchMetadataIdentitySnapshot(IReadOnlyList<MatchMetadataSnapshot> Matches,
    IReadOnlyList<Guid> SeasonIds, IReadOnlyList<Guid> TeamIds, IReadOnlyList<MatchMetadataIdentityMapping> Mappings);

public enum MatchMetadataEvaluationDisposition { Unchanged, ChangesDetected, Conflict, InvalidInput }
public enum MatchMetadataFreshness { FreshnessUnverified }
public enum MatchMetadataField { KickoffUtc, Matchday, Status, HomeGoals, AwayGoals }

public abstract record MatchMetadataChange(MatchMetadataField Field, MatchObservationFieldPresence Presence);
public sealed record MatchMetadataChange<T>(MatchMetadataField Field, MatchObservationFieldPresence Presence,
    T? Before, T? After) : MatchMetadataChange(Field, Presence) where T : struct;

/// <summary>Differences are proposals only. No result authorizes a write or establishes observation ordering.</summary>
public sealed record MatchMetadataEvaluation(MatchMetadataEvaluationDisposition Disposition, Guid? MatchId,
    IReadOnlyList<MatchMetadataChange> Changes, IdentityResolutionFailure? Failure)
{
    public MatchMetadataFreshness Freshness => MatchMetadataFreshness.FreshnessUnverified;
    public bool IsUpdateAuthorized => false;
}
