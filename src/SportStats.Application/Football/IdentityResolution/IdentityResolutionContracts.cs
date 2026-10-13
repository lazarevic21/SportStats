using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;

namespace SportStats.Application.Football.IdentityResolution;

/// <summary>A typed reference to an existing entity or an opaque provider identity.
/// External references may refer to parents in the same batch. String equality is ordinal.</summary>
public abstract record EntityReference<T> where T : class
{
    private EntityReference() { }
    public sealed record Internal(Guid Id) : EntityReference<T>;
    public sealed record External(ProviderIdentity Identity) : EntityReference<T>;
}

public interface IIdentityResolutionRequest
{
    ProviderIdentity Identity { get; }
}

public sealed record CompetitionResolutionRequest(Guid SportId, NormalizedCompetition Competition)
    : IIdentityResolutionRequest
{
    public ProviderIdentity Identity => Competition.Identity;
}

public sealed record SeasonResolutionRequest(EntityReference<Competition> Competition, NormalizedSeason Season)
    : IIdentityResolutionRequest
{
    public ProviderIdentity Identity => Season.Identity;
}

/// <summary>Resolve the team and ensure its membership in the supplied season.
/// Team names are metadata, never natural keys.</summary>
public sealed record TeamResolutionRequest(EntityReference<Season> Season, NormalizedTeam Team)
    : IIdentityResolutionRequest
{
    public ProviderIdentity Identity => Team.Identity;
}

/// <summary>Season and participants are resolved from Match's provider references.
/// Competition supplies the parent of Match.Season. Kickoff and team names are not natural keys.</summary>
public sealed record MatchResolutionRequest(EntityReference<Competition> Competition, NormalizedMatch Match)
    : IIdentityResolutionRequest
{
    public ProviderIdentity Identity => Match.Identity;
}

public enum IdentityResolutionFailureCode
{
    /// <summary>Multiple mapping rows for one exact identity, even when targets agree.</summary>
    DuplicateMappings,
    /// <summary>More than one distinct target is a candidate for the identity.</summary>
    AmbiguousMappings,
    /// <summary>A mapped entity contradicts the requested natural key, or key ownership conflicts.</summary>
    NaturalKeyConflict,
    /// <summary>A required sport, parent, team, or membership cannot be resolved.</summary>
    MissingReference,
    /// <summary>A resolved entity has incompatible parents or participants.</summary>
    RelationshipConflict,
    InvalidInput
}

/// <summary>Field is a contract property path; Detail must be a safe diagnostic, never raw payloads or secrets.
/// If rows are duplicated and targets differ, report AmbiguousMappings; otherwise DuplicateMappings.</summary>
public sealed record IdentityResolutionFailure(
    IdentityResolutionFailureCode Code, string Field, string Detail);

public enum IdentityResolutionDisposition { ExistingMapping, LinkedExistingEntity, CreatedEntity }

/// <summary>Success and failure are mutually exclusive. T prevents mixing entity types.
/// An ID is provisional until the owning transaction commits.</summary>
public abstract record IdentityResolutionResult<T> where T : class
{
    private IdentityResolutionResult() { }
    public sealed record Resolved(Guid EntityId, ProviderIdentity Identity, IdentityResolutionDisposition Disposition)
        : IdentityResolutionResult<T>;
    public sealed record Failed(IdentityResolutionFailure Failure) : IdentityResolutionResult<T>;
}

/// <summary>Implementations must snapshot input collections before asynchronous processing.
/// Resolve competitions, then seasons, then teams/memberships, then matches, regardless of input order.
/// Repeated exact identities are invalid input; never silently choose a request.</summary>
public sealed record IdentityResolutionBatch(
    IReadOnlyList<CompetitionResolutionRequest> Competitions,
    IReadOnlyList<SeasonResolutionRequest> Seasons,
    IReadOnlyList<TeamResolutionRequest> Teams,
    IReadOnlyList<MatchResolutionRequest> Matches);

/// <summary>Only a fully successful batch exposes results, in input order within each entity type.</summary>
public abstract record IdentityResolutionBatchResult
{
    private IdentityResolutionBatchResult() { }
    public sealed record Resolved(
        IReadOnlyList<IdentityResolutionResult<Competition>.Resolved> Competitions,
        IReadOnlyList<IdentityResolutionResult<Season>.Resolved> Seasons,
        IReadOnlyList<IdentityResolutionResult<FootballTeam>.Resolved> Teams,
        IReadOnlyList<IdentityResolutionResult<FootballMatch>.Resolved> Matches) : IdentityResolutionBatchResult;
    public sealed record Failed(IdentityResolutionFailure Failure) : IdentityResolutionBatchResult;
}

/// <summary>Application port only. Resolve parents before children. Never normalize identity strings,
/// guess matches from names/kickoffs, or reparent existing mappings.
/// Every call, including batches, requires an existing caller-owned, EF-enlisted PostgreSQL
/// transaction at ReadCommitted isolation and a dedicated context with an empty change tracker.
/// The resolver never opens, commits, or disposes the caller's transaction. Successful results
/// remain provisional until the caller commits. Batch work is atomic within a resolver savepoint:
/// failure, exception, or cancellation rolls back resolver writes and clears resolver tracking.
/// Cleanup uses an independent bounded token. If lock acquisition or cleanup fails, the transaction
/// is unusable for further resolver calls and requires full rollback by its owner; rollback is not
/// claimed when cleanup cannot be confirmed. The caller must end a failed transaction rather than
/// re-enlist it under a different EF transaction handle. Cancellation is honored until completion begins;
/// cancellation arriving after that boundary does not turn successful completion into failure.
/// Validate requests before persistence. Cancellation propagates as OperationCanceledException,
/// never InvalidInput. Storage/transient errors remain exceptions, not identity failures.</summary>
public interface IFootballIdentityResolver
{
    Task<IdentityResolutionResult<Competition>> ResolveCompetitionAsync(CompetitionResolutionRequest request, CancellationToken cancellationToken = default);
    Task<IdentityResolutionResult<Season>> ResolveSeasonAsync(SeasonResolutionRequest request, CancellationToken cancellationToken = default);
    Task<IdentityResolutionResult<FootballTeam>> ResolveTeamAsync(TeamResolutionRequest request, CancellationToken cancellationToken = default);
    Task<IdentityResolutionResult<FootballMatch>> ResolveMatchAsync(MatchResolutionRequest request, CancellationToken cancellationToken = default);
    Task<IdentityResolutionBatchResult> ResolveBatchAsync(IdentityResolutionBatch batch, CancellationToken cancellationToken = default);
}
