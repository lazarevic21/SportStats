using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;

namespace SportStats.Infrastructure.Persistence.IdentityResolution;

// New entities have symbolic references: planning neither allocates IDs nor constructs tracked entities.
internal sealed record PlannedEntity(IdentityLockEntity Kind, Guid? ExistingId, ProviderIdentity? NewIdentity);
internal sealed record PlannedResolution(ProviderIdentity Identity, PlannedEntity Entity,
    PlannedEntity? Parent = null, PlannedEntity? Home = null, PlannedEntity? Away = null);
internal sealed record PlannedMembership(PlannedEntity Season, PlannedEntity Team, bool Exists);

// No GUID is allocated during planning. These exact proposed natural keys have no individually
// addressable lock key yet; the football coordination lock is their exclusive protection.
internal sealed record CoordinationProtectedSeasonKey(PlannedEntity Competition, string Key);
internal sealed record CoordinationProtectedMembershipKey(PlannedEntity Season, PlannedEntity Team);

internal sealed record FootballResolutionPlan(
    IdentityResolutionBatch Input,
    IReadOnlyList<PlannedResolution> Competitions,
    IReadOnlyList<PlannedResolution> Seasons,
    IReadOnlyList<PlannedResolution> Teams,
    IReadOnlyList<PlannedMembership> Memberships,
    IReadOnlyList<PlannedResolution> Matches,
    FootballAdvisoryLockPlan IndividualDataLocks,
    IReadOnlyList<CoordinationProtectedSeasonKey> CoordinationProtectedSeasonKeys,
    IReadOnlyList<CoordinationProtectedMembershipKey> CoordinationProtectedMembershipKeys)
{
    /// <summary>
    /// Mandatory for every plan, including those with individually addressable natural keys.
    /// The future executor must use this plan only in its originating caller-owned transaction,
    /// retain football coordination through transaction completion, and acquire IndividualDataLocks
    /// before writes. Those data locks do not individually cover the listed symbolic season keys
    /// (new competition ID) or membership keys (new season and/or team ID).
    /// PostgreSQL transaction-scoped coordination is acquired BEFORE the shell's savepoint;
    /// rollback/release of that resolver savepoint never releases it. Coordination must remain
    /// held until transaction completion: caller rollback to an earlier savepoint invalidates
    /// this plan and its lock protection, so the plan must never be reused after such rollback.
    /// Every cooperating writer must acquire coordination first; relaxing this protocol requires
    /// redesigning symbolic-key protection. Plans cannot be reused in a different transaction.
    /// </summary>
    public bool RequiresCoordinationUntilTransactionEnd => true;
}

internal sealed record FootballPlanningResult(FootballResolutionPlan? Plan, IdentityResolutionFailure? Failure);

// Immutable scalar discovery data. Readers must include all mappings (including duplicates),
// natural-key candidates, referenced entities and memberships. Never select an arbitrary mapping.
internal sealed record DiscoveredSport(Guid Id, string Code);
internal sealed record DiscoveredCompetition(Guid Id, Guid SportId, string Code);
internal sealed record DiscoveredSeason(Guid Id, Guid CompetitionId, string Key);
internal sealed record DiscoveredMatch(Guid Id, Guid SeasonId, Guid HomeTeamId, Guid AwayTeamId);
internal sealed record DiscoveredMapping(IdentityLockEntity Kind, ProviderIdentity Identity, Guid TargetId);
internal sealed record FootballDiscovery(
    IReadOnlyList<DiscoveredSport> Sports,
    IReadOnlyList<DiscoveredCompetition> Competitions,
    IReadOnlyList<DiscoveredSeason> Seasons,
    IReadOnlyList<Guid> Teams,
    IReadOnlyList<(Guid SeasonId, Guid TeamId)> Memberships,
    IReadOnlyList<DiscoveredMatch> Matches,
    IReadOnlyList<DiscoveredMapping> Mappings);
