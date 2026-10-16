using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;

namespace SportStats.Application.Football.MatchMetadata;

/// <summary>
/// Pure read-only evaluation. Identity is established solely by exact trusted external mappings.
/// Status is the adapter-normalized authority; RawProviderStatus is diagnostic provenance only.
/// Adapters must represent unsupported provider statuses as Unknown, which evaluation rejects.
/// </summary>
public sealed class MatchMetadataChangeEvaluator
{
    public MatchMetadataEvaluation Evaluate(MatchProviderObservation observation, MatchMetadataIdentitySnapshot identities)
        => EvaluateBatch([observation], identities)[0];

    public IReadOnlyList<MatchMetadataEvaluation> EvaluateBatch(IReadOnlyList<MatchProviderObservation> observations,
        MatchMetadataIdentitySnapshot identities)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(identities);
        // Detach collections before processing; scalar snapshots and observation records are immutable.
        var input = observations.ToArray();
        var trusted = identities with { Matches = identities.Matches.ToArray(), SeasonIds = identities.SeasonIds.ToArray(),
            TeamIds = identities.TeamIds.ToArray(), Mappings = identities.Mappings.ToArray() };
        var results = input.Select(x => EvaluateOne(x, trusted)).ToArray();
        foreach (var group in Enumerable.Range(0, input.Length)
            .Where(i => results[i].MatchId.HasValue && results[i].Failure is null).GroupBy(i => results[i].MatchId))
        {
            var entries = group.ToArray();
            var contradiction = entries.SelectMany((left, index) => entries.Skip(index + 1)
                .Select(right => Contradiction(input[left], input[right]))).FirstOrDefault(x => x is not null);
            if (contradiction is null) continue;
            foreach (var index in entries)
                results[index] = Failure(MatchMetadataEvaluationDisposition.Conflict, IdentityResolutionFailureCode.RelationshipConflict,
                    contradiction, "Contradictory explicit metadata observations for one canonical match.", group.Key);
        }
        return Array.AsReadOnly(results);
    }

    private static MatchMetadataEvaluation EvaluateOne(MatchProviderObservation? observation, MatchMetadataIdentitySnapshot identities)
    {
        if (Validate(observation) is { } invalid)
            return Failure(MatchMetadataEvaluationDisposition.InvalidInput, IdentityResolutionFailureCode.InvalidInput,
                invalid, "Invalid observation contract value.");
        var value = observation!;
        var matchId = Resolve(MatchMetadataIdentityKind.Match, value.Identity, identities, "Identity", out var failure);
        if (failure is not null) return failure;
        var candidates = identities.Matches.Where(x => x.MatchId == matchId).ToArray();
        if (candidates.Length != 1)
            return Failure(MatchMetadataEvaluationDisposition.Conflict, IdentityResolutionFailureCode.MissingReference,
                "Identity", "Mapped canonical match snapshot must exist exactly once.", matchId);
        var match = candidates[0];
        var seasonId = Resolve(MatchMetadataIdentityKind.Season, value.Season.Identity, identities, "Season.Identity", out failure);
        if (failure is not null) return failure;
        var homeId = Resolve(MatchMetadataIdentityKind.Team, value.HomeTeam, identities, "HomeTeam", out failure);
        if (failure is not null) return failure;
        var awayId = Resolve(MatchMetadataIdentityKind.Team, value.AwayTeam, identities, "AwayTeam", out failure);
        if (failure is not null) return failure;
        if (match.MatchId == Guid.Empty || match.SeasonId != seasonId || match.HomeTeamId != homeId ||
            match.AwayTeamId != awayId || homeId == awayId)
            return Failure(MatchMetadataEvaluationDisposition.Conflict, IdentityResolutionFailureCode.RelationshipConflict,
                "Match", "Canonical match season and ordered teams are immutable.", matchId);
        var changes = new List<MatchMetadataChange>();
        Compare(MatchMetadataField.KickoffUtc, match.KickoffUtc, value.KickoffUtc, changes);
        Compare(MatchMetadataField.Matchday, match.Matchday, value.Matchday, changes);
        Compare(MatchMetadataField.Status, (FootballMatchStatus?)match.Status, value.Status, changes);
        Compare(MatchMetadataField.HomeGoals, match.HomeGoals, value.HomeGoals, changes);
        Compare(MatchMetadataField.AwayGoals, match.AwayGoals, value.AwayGoals, changes);
        return new(changes.Count == 0 ? MatchMetadataEvaluationDisposition.Unchanged : MatchMetadataEvaluationDisposition.ChangesDetected,
            matchId, changes.AsReadOnly(), null);
    }

    private static Guid Resolve(MatchMetadataIdentityKind kind, ProviderIdentity identity, MatchMetadataIdentitySnapshot snapshot,
        string field, out MatchMetadataEvaluation? failure)
    {
        failure = null;
        var rows = snapshot.Mappings.Where(x => x.Kind == kind && x.Identity == identity).ToArray();
        var targets = rows.Select(x => x.EntityId).Distinct().ToArray();
        var code = targets.Length > 1 ? IdentityResolutionFailureCode.AmbiguousMappings :
            rows.Length > 1 ? IdentityResolutionFailureCode.DuplicateMappings : IdentityResolutionFailureCode.MissingReference;
        if (rows.Length != 1)
        {
            failure = Failure(MatchMetadataEvaluationDisposition.Conflict, code, field,
                "Exactly one existing external identity mapping is required.");
            return Guid.Empty;
        }
        var id = targets[0];
        var exists = id != Guid.Empty && kind switch
        {
            MatchMetadataIdentityKind.Match => snapshot.Matches.Any(x => x.MatchId == id),
            MatchMetadataIdentityKind.Season => snapshot.SeasonIds.Contains(id),
            MatchMetadataIdentityKind.Team => snapshot.TeamIds.Contains(id),
            _ => false
        };
        if (!exists) failure = Failure(MatchMetadataEvaluationDisposition.Conflict,
            IdentityResolutionFailureCode.MissingReference, field, "External identity mapping has no existing canonical target.");
        return id;
    }

    private static void Compare<T>(MatchMetadataField field, T? before, MatchObservationField<T> proposed,
        List<MatchMetadataChange> changes) where T : struct
    {
        if (proposed.Presence != MatchObservationFieldPresence.Missing && !EqualityComparer<T?>.Default.Equals(before, proposed.Value))
            changes.Add(new MatchMetadataChange<T>(field, proposed.Presence, before, proposed.Value));
    }

    private static string? Validate(MatchProviderObservation? observation)
    {
        static bool Identity(ProviderIdentity? x) => x is not null && !string.IsNullOrWhiteSpace(x.ProviderKey) &&
            !string.IsNullOrWhiteSpace(x.IdentityScope) && !string.IsNullOrWhiteSpace(x.ExternalId);
        if (observation is null) return "Observation";
        if (!Identity(observation.Identity)) return "Identity";
        if (observation.Source is null || string.IsNullOrWhiteSpace(observation.Source.Endpoint) ||
            observation.Source.ProviderKey != observation.Identity.ProviderKey) return "Source";
        if (observation.Season is null || !Identity(observation.Season.Identity) ||
            string.IsNullOrWhiteSpace(observation.Season.Key) || observation.Season.QueryYear is < 1 or > 9999 ||
            observation.Season.StartDate > observation.Season.EndDate) return "Season";
        if (!Identity(observation.HomeTeam)) return "HomeTeam";
        if (!Identity(observation.AwayTeam)) return "AwayTeam";
        if (observation.KickoffUtc.Value is { Kind: not DateTimeKind.Utc }) return "KickoffUtc";
        if (observation.ProviderLastUpdatedUtc.Value is { Kind: not DateTimeKind.Utc }) return "ProviderLastUpdatedUtc";
        if (observation.RetrievedAtUtc.Kind != DateTimeKind.Utc) return "RetrievedAtUtc";
        if (observation.Matchday.Value is < 1) return "Matchday";
        if (observation.HomeGoals.Value is < 0) return "HomeGoals";
        if (observation.AwayGoals.Value is < 0) return "AwayGoals";
        if (observation.Status.Presence == MatchObservationFieldPresence.Null ||
            observation.Status.Value is { } status && (!Enum.IsDefined(status) || status == FootballMatchStatus.Unknown)) return "Status";
        return null;
    }

    private static string? Contradiction(MatchProviderObservation left, MatchProviderObservation right)
    {
        static bool Different<T>(MatchObservationField<T> a, MatchObservationField<T> b) where T : struct
            => a.Presence != MatchObservationFieldPresence.Missing && b.Presence != MatchObservationFieldPresence.Missing &&
                !EqualityComparer<T?>.Default.Equals(a.Value, b.Value);
        if (Different(left.KickoffUtc, right.KickoffUtc)) return "KickoffUtc";
        if (Different(left.Matchday, right.Matchday)) return "Matchday";
        if (Different(left.Status, right.Status)) return "Status";
        if (Different(left.HomeGoals, right.HomeGoals)) return "HomeGoals";
        if (Different(left.AwayGoals, right.AwayGoals)) return "AwayGoals";
        return null;
    }

    private static MatchMetadataEvaluation Failure(MatchMetadataEvaluationDisposition disposition,
        IdentityResolutionFailureCode code, string field, string detail, Guid? matchId = null)
        => new(disposition, matchId, Array.Empty<MatchMetadataChange>(), new(code, field, detail));
}
