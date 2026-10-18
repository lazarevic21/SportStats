using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;
using SportStats.Domain.Football;

namespace SportStats.Application.Football.MatchSynchronization;

/// <summary>Pure detection-based policy. No evidence supplied by callers can promote a proposal to Approved.</summary>
public sealed class MatchSynchronizationPolicyEngine
{
    public IReadOnlyList<MatchSynchronizationDecision> Evaluate(MatchSynchronizationPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return EvaluateBatch(input.Observations, input.Identities);
    }

    public MatchSynchronizationDecision Evaluate(MatchProviderObservation observation, MatchMetadataIdentitySnapshot identities)
        => EvaluateBatch([observation], identities)[0];

    public IReadOnlyList<MatchSynchronizationDecision> EvaluateBatch(IReadOnlyList<MatchProviderObservation> observations,
        MatchMetadataIdentitySnapshot identities)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(identities);
        var input = observations.ToArray();
        var trusted = identities with { Matches = identities.Matches.ToArray(), SeasonIds = identities.SeasonIds.ToArray(),
            TeamIds = identities.TeamIds.ToArray(), Mappings = identities.Mappings.ToArray() };
        var evaluations = new MatchMetadataChangeEvaluator().EvaluateBatch(input, trusted);
        // Unresolved failures cannot be assigned to a canonical match without repeating identity resolution.
        var groups = Enumerable.Range(0, input.Length).GroupBy(i => evaluations[i].MatchId is { } id
            ? (MatchId: (Guid?)id, UnresolvedIndex: -1) : (MatchId: (Guid?)null, UnresolvedIndex: i));
        return Array.AsReadOnly(groups.Select(group => Decide(group.Select(i => input[i]).ToArray(),
            group.Select(i => evaluations[i]).ToArray(), trusted)).ToArray());
    }

    private static MatchSynchronizationDecision Decide(MatchProviderObservation[] observations,
        MatchMetadataEvaluation[] evaluations, MatchMetadataIdentitySnapshot identities)
    {
        var matchId = evaluations[0].MatchId;
        var baseline = identities.Matches.SingleOrDefault(x => x.MatchId == matchId &&
            identities.Matches.Count(m => m.MatchId == matchId) == 1);
        var reasons = new HashSet<MatchSynchronizationReasonCode>();
        var candidates = new List<MatchSynchronizationCandidate>();
        var fields = new List<MatchSynchronizationFieldAssessment>();
        var rejected = false;
        var review = false;
        foreach (var result in evaluations)
        {
            if (result.Failure is not { } failure) continue;
            if (result.Disposition == MatchMetadataEvaluationDisposition.Conflict &&
                failure.Code == IdentityResolutionFailureCode.RelationshipConflict &&
                Enum.GetNames<MatchMetadataField>().Contains(failure.Field, StringComparer.Ordinal))
            {
                review = true;
                reasons.Add(MatchSynchronizationReasonCode.ContradictoryObservations);
            }
            else
            {
                rejected = true;
                reasons.Add(result.Disposition == MatchMetadataEvaluationDisposition.InvalidInput
                    ? MatchSynchronizationReasonCode.InputInvalid
                    : failure.Code == IdentityResolutionFailureCode.RelationshipConflict
                        ? MatchSynchronizationReasonCode.ImmutableRelationshipConflict : MatchSynchronizationReasonCode.IdentityFailure);
            }
        }
        for (var i = 0; i < observations.Length; i++)
        {
            var result = evaluations[i];
            if (result.Failure is not null || baseline is null) continue;
            var observation = observations[i];
            var rowReasons = new HashSet<MatchSynchronizationReasonCode>();
            var scoreChanged = result.Changes.Any(c => c.Field is MatchMetadataField.HomeGoals or MatchMetadataField.AwayGoals);
            var nextStatus = observation.Status.Value ?? baseline.Status;
            if (result.Changes.Count > 0)
            {
                rowReasons.Add(MatchSynchronizationReasonCode.OrderingUnverified);
                rowReasons.Add(MatchSynchronizationReasonCode.AuthorityUnverified);
                if (result.Changes.Any(c => c.Presence == MatchObservationFieldPresence.Null))
                    Block(MatchSynchronizationReasonCode.NullableClearRequiresReview);
                if (baseline.Status == FootballMatchStatus.Finished)
                    Block(MatchSynchronizationReasonCode.FinishedCorrectionRequiresReview);
                if (Exceptional(baseline.Status) || Exceptional(nextStatus))
                    Block(MatchSynchronizationReasonCode.ExceptionalStateRequiresReview);
                if (baseline.Status == FootballMatchStatus.Unknown || nextStatus != baseline.Status &&
                    !((baseline.Status == FootballMatchStatus.Scheduled && nextStatus is FootballMatchStatus.Live or FootballMatchStatus.Finished) ||
                      (baseline.Status == FootballMatchStatus.Live && nextStatus == FootballMatchStatus.Finished)))
                    Block(MatchSynchronizationReasonCode.TransitionRequiresReview);
                if (baseline.Status == FootballMatchStatus.Live && result.Changes.Any(c => c.Field is MatchMetadataField.KickoffUtc or MatchMetadataField.Matchday))
                    Block(MatchSynchronizationReasonCode.InPlayMetadataCorrectionRequiresReview);
                if (scoreChanged && nextStatus == FootballMatchStatus.Scheduled)
                    Block(MatchSynchronizationReasonCode.ScheduledScoreRequiresReview);
                if (nextStatus == FootballMatchStatus.Finished && baseline.Status != FootballMatchStatus.Finished && !ExplicitPair(observation))
                    rowReasons.Add(MatchSynchronizationReasonCode.ExplicitFinalScorePairRequired);
                if (scoreChanged && !ExplicitPair(observation))
                    rowReasons.Add(MatchSynchronizationReasonCode.ExplicitScorePairRequired);
                if (scoreChanged || nextStatus == FootballMatchStatus.Finished ||
                    (nextStatus == FootballMatchStatus.Live &&
                     (Overlay(baseline.HomeGoals, observation.HomeGoals) is not null ||
                      Overlay(baseline.AwayGoals, observation.AwayGoals) is not null)))
                    rowReasons.Add(MatchSynchronizationReasonCode.ScoreSemanticsUnverified);
            }
            var ordered = Freeze(rowReasons.OrderBy(x => x));
            reasons.UnionWith(rowReasons);
            Add(MatchMetadataField.KickoffUtc, baseline.KickoffUtc, observation.KickoffUtc);
            Add(MatchMetadataField.Matchday, baseline.Matchday, observation.Matchday);
            Add(MatchMetadataField.Status, (FootballMatchStatus?)baseline.Status, observation.Status);
            Add(MatchMetadataField.HomeGoals, baseline.HomeGoals, observation.HomeGoals);
            Add(MatchMetadataField.AwayGoals, baseline.AwayGoals, observation.AwayGoals);
            candidates.Add(new(i, baseline with {
                KickoffUtc = Overlay(baseline.KickoffUtc, observation.KickoffUtc),
                Matchday = Overlay(baseline.Matchday, observation.Matchday), Status = nextStatus,
                HomeGoals = Overlay(baseline.HomeGoals, observation.HomeGoals), AwayGoals = Overlay(baseline.AwayGoals, observation.AwayGoals)
            }, Freeze(result.Changes)));
            void Block(MatchSynchronizationReasonCode reason) { review = true; rowReasons.Add(reason); }
            void Add<T>(MatchMetadataField field, T? before, MatchObservationField<T> proposal) where T : struct
                => fields.Add(new MatchSynchronizationFieldAssessment<T>(i, field, proposal.Presence,
                    before, Overlay(before, proposal), ordered));
        }
        var outcome = rejected ? MatchSynchronizationOutcome.Rejected : review ? MatchSynchronizationOutcome.RequiresReview :
            evaluations.All(e => e.Disposition == MatchMetadataEvaluationDisposition.Unchanged)
                ? MatchSynchronizationOutcome.NoChange : MatchSynchronizationOutcome.InsufficientEvidence;
        if (outcome == MatchSynchronizationOutcome.NoChange) reasons.Add(MatchSynchronizationReasonCode.NoEffectiveChanges);
        return new(matchId, baseline, Freeze(observations), Freeze(evaluations), Freeze(candidates), Freeze(fields), outcome,
            Freeze(reasons.OrderBy(x => x)));
    }

    private static bool Exceptional(FootballMatchStatus status)
        => status is FootballMatchStatus.Postponed or FootballMatchStatus.Suspended or FootballMatchStatus.Cancelled;
    private static bool ExplicitPair(MatchProviderObservation observation)
        => observation.HomeGoals.Presence == MatchObservationFieldPresence.Value && observation.AwayGoals.Presence == MatchObservationFieldPresence.Value;
    private static T? Overlay<T>(T? baseline, MatchObservationField<T> proposal) where T : struct
        => proposal.Presence == MatchObservationFieldPresence.Missing ? baseline : proposal.Value;
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
