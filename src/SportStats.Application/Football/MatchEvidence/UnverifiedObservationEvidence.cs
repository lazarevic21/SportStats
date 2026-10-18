using SportStats.Application.Football.Ingestion;

namespace SportStats.Application.Football.MatchEvidence;

/// <summary>
/// A structurally validated observation and its content fingerprint. Every source, timestamp
/// and provider claim remains unverified. Construction does not attest capture or confer
/// ordering, policy eligibility or persistence authorization.
/// </summary>
public sealed class UnverifiedObservationEvidence
{
    public UnverifiedObservationEvidence(MatchProviderObservation observation)
    {
        Fingerprint = MatchObservationFingerprintSerializer.Compute(observation);
        Observation = observation;
    }

    // The observation's nested records contain only immutable scalar values and records.
    public MatchProviderObservation Observation { get; }
    public ObservationFingerprint Fingerprint { get; }
}
