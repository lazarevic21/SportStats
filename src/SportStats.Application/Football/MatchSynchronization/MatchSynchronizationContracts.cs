using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;

namespace SportStats.Application.Football.MatchSynchronization;

public enum MatchSynchronizationOutcome { NoChange, Approved, RequiresReview, Rejected, InsufficientEvidence }
/// <summary>Identities must come from the trusted read boundary; this record does not authenticate its caller.</summary>
public sealed record MatchSynchronizationPolicyInput(IReadOnlyList<MatchProviderObservation> Observations,
    MatchMetadataIdentitySnapshot Identities);
public enum MatchSynchronizationReasonCode
{
    InputInvalid, IdentityFailure, ImmutableRelationshipConflict, ContradictoryObservations,
    NoEffectiveChanges, OrderingUnverified, AuthorityUnverified, ScoreSemanticsUnverified,
    NullableClearRequiresReview, FinishedCorrectionRequiresReview, ExceptionalStateRequiresReview,
    TransitionRequiresReview, InPlayMetadataCorrectionRequiresReview, ScheduledScoreRequiresReview,
    ExplicitScorePairRequired, ExplicitFinalScorePairRequired
}

/// <summary>No independently verified ordering or authority contract exists in this phase.</summary>
public sealed record MatchSynchronizationEvidence
{
    public MatchMetadataFreshness Freshness => MatchMetadataFreshness.FreshnessUnverified;
    public bool HasVerifiedAuthority => false;
}

/// <summary>Reasons describe the entire observation's atomic blockers, not blame assigned to this individual field.</summary>
public abstract record MatchSynchronizationFieldAssessment(int ObservationIndex, MatchMetadataField Field,
    MatchObservationFieldPresence Presence, IReadOnlyList<MatchSynchronizationReasonCode> ReasonCodes);
public sealed record MatchSynchronizationFieldAssessment<T>(int ObservationIndex, MatchMetadataField Field,
    MatchObservationFieldPresence Presence, T? Before, T? After,
    IReadOnlyList<MatchSynchronizationReasonCode> ReasonCodes)
    : MatchSynchronizationFieldAssessment(ObservationIndex, Field, Presence, ReasonCodes) where T : struct;

/// <summary>A single observation overlaid on the baseline; complementary observations are never spliced.</summary>
public sealed record MatchSynchronizationCandidate(int ObservationIndex, MatchMetadataSnapshot ResultingMetadata,
    IReadOnlyList<MatchMetadataChange> Changes);

/// <summary>
/// Atomic advisory assessment, never a persistence grant. Approved is reserved and currently unreachable.
/// Original observations retain all presences and provenance. ObservationIndex addresses this decision's observations.
/// PolicyVersion identifies rules, not provider authority. Decision time/actor belong to the caller's audit boundary.
/// </summary>
public sealed record MatchSynchronizationDecision(Guid? MatchId, MatchMetadataSnapshot? Baseline,
    IReadOnlyList<MatchProviderObservation> Observations, IReadOnlyList<MatchMetadataEvaluation> EvaluatorResults,
    IReadOnlyList<MatchSynchronizationCandidate> Candidates,
    IReadOnlyList<MatchSynchronizationFieldAssessment> FieldAssessments,
    MatchSynchronizationOutcome Outcome, IReadOnlyList<MatchSynchronizationReasonCode> ReasonCodes)
{
    public string PolicyVersion => "2.9B.1";
    public MatchSynchronizationEvidence Evidence { get; } = new();
    public bool IsUpdateAuthorized => false;
}
