using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;

namespace SportStats.Infrastructure.Persistence.MatchMetadata;

/// <summary>
/// Loads authoritative read-only identity snapshots, then delegates the entire batch to the pure
/// evaluator. Reads are best effort across queries, without a transaction or advisory write locks.
/// </summary>
public sealed class MatchMetadataEvaluationService(
    IMatchMetadataIdentityReader reader, MatchMetadataChangeEvaluator evaluator) : IMatchMetadataEvaluationService
{
    private readonly IMatchMetadataIdentityReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly MatchMetadataChangeEvaluator _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    public async Task<IReadOnlyList<MatchMetadataEvaluation>> EvaluateBatchAsync(
        IReadOnlyList<MatchProviderObservation> observations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observations);
        cancellationToken.ThrowIfCancellationRequested();
        // Snapshot the caller's collection before the first await. Observation records are immutable.
        var input = Array.AsReadOnly(observations.ToArray());
        if (input.Count == 0) return Array.Empty<MatchMetadataEvaluation>();

        var identities = await _reader.LoadAsync(input, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // One batch call preserves alias contradiction detection and request ordering.
        return _evaluator.EvaluateBatch(input, identities);
    }
}
