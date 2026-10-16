using SportStats.Application.Football.Ingestion;

namespace SportStats.Application.Football.MatchMetadata;

/// <summary>
/// Read-only evaluation against existing persisted identities. Results retain request order,
/// do not establish freshness, and never authorize updates.
/// </summary>
public interface IMatchMetadataEvaluationService
{
    Task<IReadOnlyList<MatchMetadataEvaluation>> EvaluateBatchAsync(
        IReadOnlyList<MatchProviderObservation> observations, CancellationToken cancellationToken = default);
}
