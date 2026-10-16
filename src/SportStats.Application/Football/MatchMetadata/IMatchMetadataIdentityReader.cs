using SportStats.Application.Football.Ingestion;

namespace SportStats.Application.Football.MatchMetadata;

/// <summary>
/// Reads trusted persisted identity data without mutations. Multiple database reads provide a
/// best-effort view, not a transactionally consistent snapshot or update authorization.
/// </summary>
public interface IMatchMetadataIdentityReader
{
    Task<MatchMetadataIdentitySnapshot> LoadAsync(IReadOnlyList<MatchProviderObservation> observations,
        CancellationToken cancellationToken = default);
}
