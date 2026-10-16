using SportStats.Domain.Football;

namespace SportStats.Application.Football.Ingestion;

public enum MatchObservationFieldPresence { Missing, Null, Value }

/// <summary>A missing member is not an instruction to clear its value. Zero is a value.</summary>
public readonly record struct MatchObservationField<T> where T : struct
{
    public MatchObservationField(MatchObservationFieldPresence presence, T? value)
    {
        if (!Enum.IsDefined(presence) || (presence == MatchObservationFieldPresence.Value) != value.HasValue)
            throw new ArgumentException("Presence and value must agree.");
        Presence = presence;
        Value = value;
    }

    public MatchObservationFieldPresence Presence { get; }
    public T? Value { get; }
    public static MatchObservationField<T> Missing => default;
    public static MatchObservationField<T> Null => new(MatchObservationFieldPresence.Null, null);
    public static MatchObservationField<T> FromValue(T value) => new(MatchObservationFieldPresence.Value, value);
}

/// <summary>Adapter-supplied provenance, not authorization or a provider precedence rule.</summary>
public sealed record MatchObservationSource(string ProviderKey, string Endpoint);

/// <summary>
/// An observation only: neither provider lastUpdated nor local retrieval time guarantees ordering.
/// The normalizer validates UTC timestamps, matchday and goals at the adapter boundary.
/// No persistence or canonical metadata mutation is implied.
/// </summary>
public sealed record MatchProviderObservation(
    ProviderIdentity Identity, MatchObservationSource Source, NormalizedSeason Season,
    ProviderIdentity HomeTeam, ProviderIdentity AwayTeam,
    MatchObservationField<DateTime> KickoffUtc, MatchObservationField<int> Matchday,
    MatchObservationField<FootballMatchStatus> Status, string? RawProviderStatus,
    MatchObservationField<int> HomeGoals, MatchObservationField<int> AwayGoals,
    MatchObservationField<DateTime> ProviderLastUpdatedUtc, DateTime RetrievedAtUtc);
public sealed record MatchProviderObservations(
    NormalizedCompetition Competition, int QueryYear, IReadOnlyList<MatchProviderObservation> Matches);

/// <summary>Pure observation normalization; performs no HTTP, freshness comparison or persistence.</summary>
public interface IFootballMatchObservationNormalizer
{
    MatchProviderObservations NormalizeMatchObservations(string json, int queryYear, DateTime retrievedAtUtc);
}
