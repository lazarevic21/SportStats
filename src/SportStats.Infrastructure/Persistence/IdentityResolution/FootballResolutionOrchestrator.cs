using SportStats.Application.Football.IdentityResolution;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;

namespace SportStats.Infrastructure.Persistence.IdentityResolution;

// No plans, handles, IDs, callbacks or transaction capabilities escape this foundation operation.
internal abstract record FootballResolutionOrchestrationResult
{
    private FootballResolutionOrchestrationResult() { }
    internal sealed record Completed : FootballResolutionOrchestrationResult;
    internal sealed record Failed(IdentityResolutionFailure Failure) : FootballResolutionOrchestrationResult;
}

/// <summary>Shared orchestration with narrowly scoped competition and season persistence entry points.
/// ExecuteAsync remains read-only and discards allocations. Persistence always runs
/// its own locked preparation/revalidation in one shell call; it never accepts an externally
/// prepared plan. Persisted results remain provisional until the caller commits.</summary>
internal sealed class FootballResolutionOrchestrator
{
    private readonly FootballResolutionTransaction shell;
    private readonly IFootballResolutionPreparationSession session;
    private readonly FootballIdentityMaterializer materializer;
    private readonly ICompetitionPersistenceSession? competitionPersistence;
    private readonly ISeasonPersistenceSession? seasonPersistence;

    public FootballResolutionOrchestrator(SportStatsDbContext context, TimeSpan maxLockWait)
        : this(new FootballResolutionTransaction(context), new EfFootballResolutionPreparationSession(context, maxLockWait),
            new FootballIdentityMaterializer(), new EfCompetitionPersistenceSession(context), new EfSeasonPersistenceSession(context)) { }

    internal FootballResolutionOrchestrator(FootballResolutionTransaction shell, IFootballResolutionPreparationSession session,
        FootballIdentityMaterializer materializer, ICompetitionPersistenceSession? competitionPersistence = null,
        ISeasonPersistenceSession? seasonPersistence = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(materializer);
        this.shell = shell;
        this.session = session;
        this.materializer = materializer;
        this.competitionPersistence = competitionPersistence;
        this.seasonPersistence = seasonPersistence;
    }

    internal Task<FootballResolutionOrchestrationResult> ExecuteAsync(IdentityResolutionBatch batch, CancellationToken token = default)
        => ExecuteCoreAsync<FootballResolutionOrchestrationResult>(batch,
            (_, _, _) => Task.FromResult<FootballResolutionOrchestrationResult>(new FootballResolutionOrchestrationResult.Completed()),
            failure => new FootballResolutionOrchestrationResult.Failed(failure),
            result => result is FootballResolutionOrchestrationResult.Completed, false, token);

    internal Task<IdentityResolutionBatchResult> PersistCompetitionsAsync(IdentityResolutionBatch batch, CancellationToken token = default)
        => ExecuteCoreAsync<IdentityResolutionBatchResult>(batch, PersistCompetitionsWithinOperationAsync,
            failure => new IdentityResolutionBatchResult.Failed(failure),
            result => result is IdentityResolutionBatchResult.Resolved, true, token);

    internal Task<IdentityResolutionBatchResult> PersistSeasonsAsync(IdentityResolutionBatch batch, CancellationToken token = default)
        => ExecuteCoreAsync<IdentityResolutionBatchResult>(batch, PersistSeasonsWithinOperationAsync,
            failure => new IdentityResolutionBatchResult.Failed(failure),
            result => result is IdentityResolutionBatchResult.Resolved, false, token, seasonOnly: true);

    private async Task<TResult> ExecuteCoreAsync<TResult>(IdentityResolutionBatch batch,
        Func<ValidatedFootballResolutionInput, FootballIdentityMaterialization, CancellationToken, Task<TResult>> operation,
        Func<IdentityResolutionFailure, TResult> failed, Func<TResult, bool> successful, bool competitionOnly, CancellationToken token,
        bool seasonOnly = false)
    {
        // Input must be owned and validated before inspecting the caller transaction or issuing SQL.
        var (snapshot, invalid) = FootballResolutionPlanner.SnapshotAndValidate(batch, token);
        if (invalid is not null) return failed(invalid);
        if (competitionOnly)
        {
            var input = snapshot!.Input;
            var unsupported = input.Seasons.Count > 0 ? "Seasons" : input.Teams.Count > 0 ? "Teams" : input.Matches.Count > 0 ? "Matches" : null;
            if (unsupported is not null)
                return failed(new(IdentityResolutionFailureCode.InvalidInput, unsupported, "Only competition requests are supported by this persistence entry point."));
            if (competitionPersistence is null) throw new InvalidOperationException("Competition persistence is not configured.");
        }
        if (seasonOnly)
        {
            var input = snapshot!.Input;
            var unsupported = input.Competitions.Count > 0 ? "Competitions" : input.Teams.Count > 0 ? "Teams" : input.Matches.Count > 0 ? "Matches" : null;
            if (unsupported is not null)
                return failed(new(IdentityResolutionFailureCode.InvalidInput, unsupported, "Only season requests are supported by this persistence entry point."));
            if (seasonPersistence is null) throw new InvalidOperationException("Season persistence is not configured.");
        }
        FootballPlanningResult? original = null;
        return await shell.ExecuteAsync<TResult>(async operationToken =>
        {
            if (original!.Failure is not null) return failed(original.Failure);
            var refreshed = new FootballResolutionPlanner().PlanValidated(snapshot!, await session.ReadAsync(operationToken), operationToken);
            if (refreshed.Failure is not null) return failed(refreshed.Failure);
            if (!FootballResolutionPlanComparison.Equivalent(original.Plan!, refreshed.Plan!, operationToken))
                throw new InvalidOperationException("The locked identity resolution plan changed before materialization.");
            var identities = materializer.Materialize(refreshed.Plan!, operationToken);
            var result = await operation(snapshot!, identities, operationToken);
            operationToken.ThrowIfCancellationRequested();
            return result;
        }, successful,
            async acquisitionToken => { original = await FootballResolutionDiscovery.PlanAndLockAsync(snapshot!, session, acquisitionToken); }, token);
    }

    // Private continuation: only reachable after this call's locked revalidation/materialization.
    private async Task<IdentityResolutionBatchResult> PersistSeasonsWithinOperationAsync(
        ValidatedFootballResolutionInput snapshot, FootballIdentityMaterialization identities, CancellationToken token)
    {
        var persistence = seasonPersistence!;
        var competitions = new Dictionary<Guid, Competition>();
        var seasons = new Dictionary<Guid, Season>();
        var results = new List<IdentityResolutionResult<Season>.Resolved>();
        for (var i = 0; i < snapshot.Input.Seasons.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var request = snapshot.Input.Seasons[i];
            var identity = identities.Seasons[i];
            if (identity.Entity.IsNew && !seasons.ContainsKey(identity.Entity.Id))
            {
                var parent = identity.Parent ?? throw new InvalidOperationException("Season materialization requires a parent.");
                if (parent.IsNew || parent.Kind != IdentityLockEntity.Competition)
                    throw new InvalidOperationException("Season persistence requires an existing competition.");
                if (!competitions.TryGetValue(parent.Id, out var competition))
                {
                    competition = await persistence.LoadCompetitionAsync(parent.Id, token);
                    if (competition is null)
                        return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.MissingReference,
                            $"Seasons[{i}].Competition", "The required competition is unavailable."));
                    if (competition.Id != parent.Id || competition.Code != "PL")
                        return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.RelationshipConflict,
                            $"Seasons[{i}].Competition", "The competition does not match the resolved parent."));
                    competitions.Add(parent.Id, competition);
                }
                var season = new Season(identity.Entity.Id, competition, request.Season.Key, request.Season.StartDate, request.Season.EndDate);
                seasons.Add(season.Id, season);
            }
            results.Add(new(identity.Entity.Id, identity.Identity,
                identity.Entity.IsNew ? IdentityResolutionDisposition.CreatedEntity : IdentityResolutionDisposition.ExistingMapping));
        }
        foreach (var season in seasons.Values) { token.ThrowIfCancellationRequested(); persistence.StageSeason(season); }
        foreach (var mapping in identities.NewMappings)
        {
            token.ThrowIfCancellationRequested();
            if (mapping.Kind != IdentityLockEntity.Season || !seasons.ContainsKey(mapping.EntityId))
                throw new InvalidOperationException("Season materialization contains an incompatible mapping.");
            persistence.StageMapping(new SeasonExternalIdentity(mapping.Id, mapping.Identity.ProviderKey,
                mapping.Identity.IdentityScope, mapping.Identity.ExternalId, mapping.EntityId));
        }
        token.ThrowIfCancellationRequested();
        if (seasons.Count > 0 || identities.NewMappings.Count > 0) await persistence.SaveChangesAsync(token);
        token.ThrowIfCancellationRequested();
        return new IdentityResolutionBatchResult.Resolved([], results.AsReadOnly(), [], []);
    }

    // Private continuation: only reachable after this call's locked revalidation/materialization.
    private async Task<IdentityResolutionBatchResult> PersistCompetitionsWithinOperationAsync(
        ValidatedFootballResolutionInput snapshot, FootballIdentityMaterialization identities, CancellationToken token)
    {
        var persistence = competitionPersistence!;
        var sports = new Dictionary<Guid, Sport>();
        var competitions = new Dictionary<Guid, Competition>();
        var results = new List<IdentityResolutionResult<Competition>.Resolved>();
        // Construct the complete pending graph before saving. Existing competitions are never attached or modified.
        for (var i = 0; i < snapshot.Input.Competitions.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var request = snapshot.Input.Competitions[i];
            var identity = identities.Competitions[i];
            if (identity.Entity.IsNew && !competitions.ContainsKey(identity.Entity.Id))
            {
                if (!sports.TryGetValue(request.SportId, out var sport))
                {
                    sport = await persistence.LoadSportAsync(request.SportId, token);
                    if (sport is null)
                        return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.MissingReference,
                            $"Competitions[{i}].SportId", "The required sport is unavailable."));
                    if (sport.Code != "football")
                        return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.RelationshipConflict,
                            $"Competitions[{i}].SportId", "The competition must belong to football."));
                    sports.Add(request.SportId, sport);
                }
                var competition = new Competition(identity.Entity.Id, sport, request.Competition.Code, request.Competition.Name);
                competitions.Add(competition.Id, competition);
            }
            results.Add(new(identity.Entity.Id, identity.Identity,
                identity.Entity.IsNew ? IdentityResolutionDisposition.CreatedEntity : IdentityResolutionDisposition.ExistingMapping));
        }
        foreach (var competition in competitions.Values) { token.ThrowIfCancellationRequested(); persistence.StageCompetition(competition); }
        foreach (var mapping in identities.NewMappings)
        {
            token.ThrowIfCancellationRequested();
            if (mapping.Kind != IdentityLockEntity.Competition || !competitions.ContainsKey(mapping.EntityId))
                throw new InvalidOperationException("Competition materialization contains an incompatible mapping.");
            persistence.StageMapping(new CompetitionExternalIdentity(mapping.Id, mapping.Identity.ProviderKey,
                mapping.Identity.IdentityScope, mapping.Identity.ExternalId, mapping.EntityId));
        }
        token.ThrowIfCancellationRequested();
        if (competitions.Count > 0 || identities.NewMappings.Count > 0) await persistence.SaveChangesAsync(token);
        token.ThrowIfCancellationRequested();
        return new IdentityResolutionBatchResult.Resolved(results.AsReadOnly(), [], [], []);
    }
}
