using SportStats.Application.Football.IdentityResolution;
using SportStats.Domain.Football;
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

/// <summary>Shared orchestration with narrowly scoped competition, season, team and match persistence entry points.
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
    private readonly ITeamPersistenceSession? teamPersistence;
    private readonly IMatchPersistenceSession? matchPersistence;

    public FootballResolutionOrchestrator(SportStatsDbContext context, TimeSpan maxLockWait)
        : this(new FootballResolutionTransaction(context), new EfFootballResolutionPreparationSession(context, maxLockWait),
            new FootballIdentityMaterializer(), new EfCompetitionPersistenceSession(context), new EfSeasonPersistenceSession(context),
            new EfTeamPersistenceSession(context), new EfMatchPersistenceSession(context)) { }

    internal FootballResolutionOrchestrator(FootballResolutionTransaction shell, IFootballResolutionPreparationSession session,
        FootballIdentityMaterializer materializer, ICompetitionPersistenceSession? competitionPersistence = null,
        ISeasonPersistenceSession? seasonPersistence = null, ITeamPersistenceSession? teamPersistence = null,
        IMatchPersistenceSession? matchPersistence = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(materializer);
        this.shell = shell;
        this.session = session;
        this.materializer = materializer;
        this.competitionPersistence = competitionPersistence;
        this.seasonPersistence = seasonPersistence;
        this.teamPersistence = teamPersistence;
        this.matchPersistence = matchPersistence;
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

    internal Task<IdentityResolutionBatchResult> PersistTeamsAsync(IdentityResolutionBatch batch, CancellationToken token = default)
        => ExecuteCoreAsync<IdentityResolutionBatchResult>(batch, PersistTeamsWithinOperationAsync,
            failure => new IdentityResolutionBatchResult.Failed(failure),
            result => result is IdentityResolutionBatchResult.Resolved, false, token, teamOnly: true);

    internal Task<IdentityResolutionBatchResult> PersistMatchesAsync(IdentityResolutionBatch batch, CancellationToken token = default)
        => ExecuteCoreAsync<IdentityResolutionBatchResult>(batch, PersistMatchesWithinOperationAsync,
            failure => new IdentityResolutionBatchResult.Failed(failure),
            result => result is IdentityResolutionBatchResult.Resolved, false, token, matchOnly: true);

    private async Task<TResult> ExecuteCoreAsync<TResult>(IdentityResolutionBatch batch,
        Func<ValidatedFootballResolutionInput, FootballIdentityMaterialization, CancellationToken, Task<TResult>> operation,
        Func<IdentityResolutionFailure, TResult> failed, Func<TResult, bool> successful, bool competitionOnly, CancellationToken token,
        bool seasonOnly = false, bool teamOnly = false, bool matchOnly = false)
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
        if (teamOnly)
        {
            var input = snapshot!.Input;
            var unsupported = input.Competitions.Count > 0 ? "Competitions" : input.Seasons.Count > 0 ? "Seasons" : input.Matches.Count > 0 ? "Matches" : null;
            if (unsupported is not null)
                return failed(new(IdentityResolutionFailureCode.InvalidInput, unsupported, "Only team requests are supported by this persistence entry point."));
            if (teamPersistence is null) throw new InvalidOperationException("Team persistence is not configured.");
        }
        if (matchOnly)
        {
            var input = snapshot!.Input;
            var unsupported = input.Competitions.Count > 0 ? "Competitions" : input.Seasons.Count > 0 ? "Seasons" : input.Teams.Count > 0 ? "Teams" : null;
            if (unsupported is not null)
                return failed(new(IdentityResolutionFailureCode.InvalidInput, unsupported, "Only match requests are supported by this persistence entry point."));
            if (matchPersistence is null) throw new InvalidOperationException("Match persistence is not configured.");
        }
        FootballPlanningResult? original = null;
        return await shell.ExecuteAsync<TResult>(async operationToken =>
        {
            if (original!.Failure is not null) return failed(original.Failure);
            var refreshed = new FootballResolutionPlanner().PlanValidated(snapshot!, await session.ReadAsync(operationToken), operationToken);
            if (refreshed.Failure is not null) return failed(refreshed.Failure);
            if (!FootballResolutionPlanComparison.Equivalent(original.Plan!, refreshed.Plan!, operationToken))
                throw new InvalidOperationException("The locked identity resolution plan changed before materialization.");
            if (matchOnly)
            {
                // Match requests include implicit season resolutions. Never allocate or write parents here.
                for (var i = 0; i < refreshed.Plan!.Matches.Count; i++)
                {
                    operationToken.ThrowIfCancellationRequested();
                    var match = refreshed.Plan.Matches[i];
                    var missing = match.Parent?.ExistingId is null ? "Match.Season"
                        : match.Home?.ExistingId is null ? "Match.HomeTeam"
                        : match.Away?.ExistingId is null ? "Match.AwayTeam" : null;
                    if (missing is not null)
                        return failed(new(IdentityResolutionFailureCode.MissingReference, $"Matches[{i}].{missing}", "Match persistence requires existing dependencies."));
                }
            }
            var identities = materializer.Materialize(refreshed.Plan!, operationToken);
            var result = await operation(snapshot!, identities, operationToken);
            operationToken.ThrowIfCancellationRequested();
            return result;
        }, successful,
            async acquisitionToken => { original = await FootballResolutionDiscovery.PlanAndLockAsync(snapshot!, session, acquisitionToken); }, token);
    }

    // Private continuation: only reachable after this call's locked revalidation/materialization.
    private async Task<IdentityResolutionBatchResult> PersistMatchesWithinOperationAsync(
        ValidatedFootballResolutionInput snapshot, FootballIdentityMaterialization identities, CancellationToken token)
    {
        var persistence = matchPersistence!;
        var seasons = new Dictionary<Guid, Season>();
        var memberships = new Dictionary<(Guid SeasonId, Guid TeamId), FootballSeasonTeam>();
        var matches = new Dictionary<Guid, FootballMatch>();
        var results = new List<IdentityResolutionResult<FootballMatch>.Resolved>();
        for (var i = 0; i < snapshot.Input.Matches.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var request = snapshot.Input.Matches[i].Match;
            var identity = identities.Matches[i];
            results.Add(new(identity.Entity.Id, identity.Identity,
                identity.Entity.IsNew ? IdentityResolutionDisposition.CreatedEntity : IdentityResolutionDisposition.ExistingMapping));
            if (!identity.Entity.IsNew || matches.ContainsKey(identity.Entity.Id)) continue;
            var parent = identity.Parent!;
            var home = identity.Home!;
            var away = identity.Away!;
            if (parent.IsNew || home.IsNew || away.IsNew)
                throw new InvalidOperationException("Match persistence requires existing dependencies.");
            if (home.Id == away.Id)
                return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.RelationshipConflict,
                    $"Matches[{i}].Match.AwayTeam", "Home and away teams must differ."));
            if (!seasons.TryGetValue(parent.Id, out var season))
            {
                season = await persistence.LoadSeasonAsync(parent.Id, token);
                if (season is null)
                    return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.MissingReference,
                        $"Matches[{i}].Match.Season", "The required season is unavailable."));
                var competition = identities.Seasons.First(s => s.Entity.Id == parent.Id).Parent!;
                if (season.Id != parent.Id || season.Key != request.Season.Key || season.CompetitionId != competition.Id ||
                    season.Competition?.Id != competition.Id || season.Competition.Code != "PL" || season.Competition.Sport?.Code != "football")
                    return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.RelationshipConflict,
                        $"Matches[{i}].Match.Season", "The season does not match the resolved football parent."));
                seasons.Add(parent.Id, season);
            }
            foreach (var (team, field) in new[] { (home, "HomeTeam"), (away, "AwayTeam") })
            {
                token.ThrowIfCancellationRequested();
                var key = (parent.Id, team.Id);
                if (memberships.ContainsKey(key)) continue;
                var membership = await persistence.LoadMembershipAsync(parent.Id, team.Id, token);
                if (membership is null)
                    return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.MissingReference,
                        $"Matches[{i}].Match.{field}", "The required season membership is unavailable."));
                if (membership.SeasonId != parent.Id || membership.TeamId != team.Id || membership.Team?.Id != team.Id)
                    return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.RelationshipConflict,
                        $"Matches[{i}].Match.{field}", "The membership does not match the resolved season and team."));
                memberships.Add(key, membership);
            }
            matches.Add(identity.Entity.Id, new FootballMatch(identity.Entity.Id, season,
                memberships[(parent.Id, home.Id)], memberships[(parent.Id, away.Id)], request.KickoffUtc,
                request.Status, request.Matchday, new FootballScore(request.HomeGoals.Value, request.AwayGoals.Value)));
        }
        // Validate and construct the entire batch before staging any writes.
        foreach (var match in matches.Values) { token.ThrowIfCancellationRequested(); persistence.StageMatch(match); }
        foreach (var mapping in identities.NewMappings)
        {
            token.ThrowIfCancellationRequested();
            if (mapping.Kind != IdentityLockEntity.FootballMatch || !matches.ContainsKey(mapping.EntityId))
                throw new InvalidOperationException("Match materialization contains an incompatible mapping.");
            persistence.StageMapping(new FootballMatchExternalIdentity(mapping.Id, mapping.Identity.ProviderKey,
                mapping.Identity.IdentityScope, mapping.Identity.ExternalId, mapping.EntityId));
        }
        token.ThrowIfCancellationRequested();
        if (matches.Count > 0) await persistence.SaveChangesAsync(token);
        token.ThrowIfCancellationRequested();
        return new IdentityResolutionBatchResult.Resolved([], [], [], results.AsReadOnly());
    }

    // Private continuation: only reachable after this call's locked revalidation/materialization.
    private async Task<IdentityResolutionBatchResult> PersistTeamsWithinOperationAsync(
        ValidatedFootballResolutionInput snapshot, FootballIdentityMaterialization identities, CancellationToken token)
    {
        var persistence = teamPersistence!;
        var teams = new Dictionary<Guid, FootballTeam>();
        var seasons = new Dictionary<Guid, Season>();
        var memberships = new List<FootballSeasonTeam>();
        var requestIndices = new Dictionary<(Guid SeasonId, Guid TeamId), int>();
        var results = new List<IdentityResolutionResult<FootballTeam>.Resolved>();
        for (var i = 0; i < snapshot.Input.Teams.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var request = snapshot.Input.Teams[i];
            var identity = identities.Teams[i];
            var parent = identity.Parent ?? throw new InvalidOperationException("Team materialization requires a season.");
            if (parent.IsNew || parent.Kind != IdentityLockEntity.Season)
                throw new InvalidOperationException("Team persistence requires an existing season.");
            requestIndices.TryAdd((parent.Id, identity.Entity.Id), i);
            if (identity.Entity.IsNew && !teams.ContainsKey(identity.Entity.Id))
                teams.Add(identity.Entity.Id, new FootballTeam(identity.Entity.Id, request.Team.Name, request.Team.ShortName, request.Team.CrestUrl));
            results.Add(new(identity.Entity.Id, identity.Identity,
                identity.Entity.IsNew ? IdentityResolutionDisposition.CreatedEntity : IdentityResolutionDisposition.ExistingMapping));
        }
        // Build the complete graph before staging. Existing teams are loaded only for missing memberships.
        foreach (var membership in identities.Memberships)
        {
            token.ThrowIfCancellationRequested();
            if (membership.Exists) continue;
            var index = requestIndices[(membership.Season.Id, membership.Team.Id)];
            if (!seasons.TryGetValue(membership.Season.Id, out var season))
            {
                season = await persistence.LoadSeasonAsync(membership.Season.Id, token);
                if (season is null)
                    return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.MissingReference,
                        $"Teams[{index}].Season", "The required season is unavailable."));
                if (season.Id != membership.Season.Id || season.Competition?.Code != "PL" || season.Competition.Sport?.Code != "football")
                    return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.RelationshipConflict,
                        $"Teams[{index}].Season", "The season does not match the resolved football parent."));
                seasons.Add(season.Id, season);
            }
            if (!teams.TryGetValue(membership.Team.Id, out var team))
            {
                team = await persistence.LoadTeamAsync(membership.Team.Id, token);
                if (team is null)
                    return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.MissingReference,
                        $"Teams[{index}].Team.Identity", "The required team is unavailable."));
                if (team.Id != membership.Team.Id)
                    return new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.RelationshipConflict,
                        $"Teams[{index}].Team.Identity", "The team does not match the resolved identity."));
                teams.Add(team.Id, team);
            }
            memberships.Add(new FootballSeasonTeam(season, team));
        }
        foreach (var identity in identities.Teams.DistinctBy(identity => identity.Entity.Id))
        {
            token.ThrowIfCancellationRequested();
            if (identity.Entity.IsNew) persistence.StageTeam(teams[identity.Entity.Id]);
        }
        foreach (var mapping in identities.NewMappings)
        {
            token.ThrowIfCancellationRequested();
            if (mapping.Kind != IdentityLockEntity.FootballTeam || !teams.ContainsKey(mapping.EntityId))
                throw new InvalidOperationException("Team materialization contains an incompatible mapping.");
            persistence.StageMapping(new FootballTeamExternalIdentity(mapping.Id, mapping.Identity.ProviderKey,
                mapping.Identity.IdentityScope, mapping.Identity.ExternalId, mapping.EntityId));
        }
        foreach (var membership in memberships) { token.ThrowIfCancellationRequested(); persistence.StageMembership(membership); }
        token.ThrowIfCancellationRequested();
        if (identities.NewMappings.Count > 0 || memberships.Count > 0) await persistence.SaveChangesAsync(token);
        token.ThrowIfCancellationRequested();
        return new IdentityResolutionBatchResult.Resolved([], [], results.AsReadOnly(), []);
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
