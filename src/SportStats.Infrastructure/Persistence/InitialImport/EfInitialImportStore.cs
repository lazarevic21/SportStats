using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.InitialImport;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Infrastructure.Persistence.IdentityResolution;

namespace SportStats.Infrastructure.Persistence.InitialImport;

/// <summary>Dedicated-context create-only bootstrap. Preview performs only no-tracking SELECTs.
/// Advisory coordination protects cooperating writers only; external mapping indexes are nonunique.</summary>
public sealed class EfInitialImportStore(SportStatsDbContext context, TimeSpan? maxLockWait = null) : IInitialImportStore
{
    private readonly TimeSpan lockWait = maxLockWait ?? TimeSpan.FromSeconds(15);
    // Release gate: only enable after guarded PostgreSQL atomicity verification.
    internal static bool ApplySafetyVerified => false;

    public async Task<Guid> ResolveFootballSportIdAsync(CancellationToken cancellationToken = default)
    {
        await VerifyTargetAsync(cancellationToken);
        var ids = await context.Sports.AsNoTracking().Where(x => x.Code == "football").Select(x => x.Id).ToArrayAsync(cancellationToken);
        if (ids.Length != 1) throw new InvalidOperationException("An unambiguous existing football sport is required.");
        return ids[0];
    }

    public async Task<InitialImportResult> PreviewAsync(IdentityResolutionBatch batch, CancellationToken cancellationToken = default)
    {
        RequireEmptyTracker();
        var (snapshot, invalid) = Validate(batch, cancellationToken);
        if (invalid is not null) return Failure(invalid);
        await VerifyTargetAsync(cancellationToken);
        IDbContextTransaction? owned = null;
        try
        {
            if (context.Database.CurrentTransaction is null)
            {
                owned = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
                await using var readOnly = context.Database.GetDbConnection().CreateCommand();
                readOnly.Transaction = owned.GetDbTransaction();
                readOnly.CommandText = "SET TRANSACTION READ ONLY";
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            }
            var planned = new FootballResolutionPlanner().PlanValidated(snapshot!, await FootballResolutionDiscovery.ReadAsync(context, cancellationToken), cancellationToken);
            return planned.Failure is not null ? Failure(planned.Failure) : await CompareAsync(planned.Plan!, cancellationToken);
        }
        finally { if (owned is not null) await owned.DisposeAsync(); }
    }

    public async Task<InitialImportResult> ApplyAsync(IdentityResolutionBatch batch, CancellationToken cancellationToken = default)
    {
        if (!ApplySafetyVerified) throw new InvalidOperationException("Apply is disabled pending guarded PostgreSQL atomicity verification.");
        return await ApplyForSafetyVerificationAsync(batch, cancellationToken);
    }

    // Internal verification seam shares the public lifecycle without enabling the release gate.
    internal async Task<InitialImportResult> ApplyForSafetyVerificationAsync(IdentityResolutionBatch batch, CancellationToken cancellationToken = default)
    {
        RequireEmptyTracker();
        if (context.Database.CurrentTransaction is not null) throw new InvalidOperationException("Apply requires ownership of a new transaction.");
        var (snapshot, invalid) = Validate(batch, cancellationToken);
        if (invalid is not null) return Failure(invalid);
        await VerifyTargetAsync(cancellationToken);
        return await InitialImportAtomicOperation.ExecuteAsync(new EfInitialImportAtomicSession(context),
            token => ApplyWithinTransactionAsync(snapshot!.Input, token), cancellationToken);
    }

    // Guarded integration fixture retains its rollback-only outer transaction. Never commits it.
    // Its caller MUST roll back the entire outer transaction on ANY failure/exception/cancellation.
    internal async Task<InitialImportResult> ApplyWithinTransactionAsync(IdentityResolutionBatch batch, CancellationToken cancellationToken = default)
    {
        RequireEmptyTracker();
        var (snapshot, invalid) = Validate(batch, cancellationToken);
        if (invalid is not null) return Failure(invalid);
        var transaction = context.Database.CurrentTransaction;
        if (transaction?.GetDbTransaction() is not NpgsqlTransaction pg || pg.IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("An existing ReadCommitted PostgreSQL transaction is required.");
        await VerifyTargetAsync(cancellationToken);
        var planned = await FootballResolutionDiscovery.PrepareAsync(context, snapshot!.Input, lockWait, cancellationToken);
        if (planned.Failure is not null) return Failure(planned.Failure);
        var preview = await CompareAsync(planned.Plan!, cancellationToken);
        if (preview.Conflicts.Count != 0) return WithoutCreates(preview);
        var input = snapshot.Input;
        var orchestrator = new FootballResolutionOrchestrator(context, lockWait);
        var stages = new IdentityResolutionBatch[]
        {
            new(input.Competitions, [], [], []), new([], input.Seasons, [], []),
            new([], [], input.Teams, []), new([], [], [], input.Matches)
        };
        for (var i = 0; i < stages.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = i switch
            {
                0 => await orchestrator.PersistCompetitionsAsync(stages[i], cancellationToken),
                1 => await orchestrator.PersistSeasonsAsync(stages[i], cancellationToken),
                2 => await orchestrator.PersistTeamsAsync(stages[i], cancellationToken),
                _ => await orchestrator.PersistMatchesAsync(stages[i], cancellationToken)
            };
            if (result is IdentityResolutionBatchResult.Failed failed) return Failure(failed.Failure);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return preview;
    }

    private async Task<InitialImportResult> CompareAsync(FootballResolutionPlan plan, CancellationToken token)
    {
        var conflicts = new List<IdentityResolutionFailure>();
        var counts = new List<InitialImportCount>();
        async Task Check<T>(string kind, IReadOnlyList<PlannedResolution> resolutions, IReadOnlyList<T> requests,
            Func<Guid, T, CancellationToken, Task<bool>> same)
        {
            var created = 0; var existing = 0; var conflicted = 0;
            for (var i = 0; i < resolutions.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (resolutions[i].Entity.ExistingId is not Guid id) { created++; continue; }
                if (await same(id, requests[i], token)) existing++;
                else
                {
                    conflicted++;
                    conflicts.Add(new(IdentityResolutionFailureCode.RelationshipConflict, $"{kind}[{i}].Metadata", "Existing canonical metadata differs; updates are forbidden."));
                }
            }
            counts.Add(new(kind, created, existing, 0, conflicted));
        }
        await Check("Competitions", plan.Competitions, plan.Input.Competitions, async (id, request, ct) =>
            await context.Competitions.AsNoTracking().AnyAsync(x => x.Id == id && x.Name == request.Competition.Name, ct));
        // Plan.Seasons can also contain implicit season references. Explicit requests are matched by exact identity.
        var seasons = plan.Input.Seasons.Select(x => plan.Seasons.Single(y => y.Identity == x.Identity)).ToArray();
        await Check("Seasons", seasons, plan.Input.Seasons, async (id, request, ct) =>
            await context.Seasons.AsNoTracking().AnyAsync(x => x.Id == id && x.StartDate == request.Season.StartDate && x.EndDate == request.Season.EndDate, ct));
        await Check("Teams", plan.Teams, plan.Input.Teams, async (id, request, ct) =>
            await context.FootballTeams.AsNoTracking().AnyAsync(x => x.Id == id && x.Name == request.Team.Name && x.ShortName == request.Team.ShortName && x.CrestUrl == request.Team.CrestUrl, ct));
        await Check("Matches", plan.Matches, plan.Input.Matches, async (id, request, ct) =>
            await context.FootballMatches.AsNoTracking().AnyAsync(x => x.Id == id && x.KickoffUtc == request.Match.KickoffUtc &&
                x.Status == request.Match.Status && x.Matchday == request.Match.Matchday && x.HomeGoals == request.Match.HomeGoals.Value && x.AwayGoals == request.Match.AwayGoals.Value, ct));
        counts.Add(new("Memberships", plan.Memberships.Count(x => !x.Exists), plan.Memberships.Count(x => x.Exists), 0, 0));
        var resolutions = plan.Competitions.Concat(plan.Seasons).Concat(plan.Teams).Concat(plan.Matches).ToArray();
        counts.Add(new("Mappings", resolutions.Count(x => x.Entity.ExistingId is null), resolutions.Count(x => x.Entity.ExistingId is not null), 0, 0));
        return new(false, counts.AsReadOnly(), conflicts.AsReadOnly());
    }

    private static (ValidatedFootballResolutionInput? Snapshot, IdentityResolutionFailure? Failure) Validate(IdentityResolutionBatch batch, CancellationToken token)
    {
        var result = FootballResolutionPlanner.SnapshotAndValidate(batch, token);
        if (result.Failure is not null) return result;
        var input = result.Snapshot!.Input;
        if (input.Competitions.Count != 1 || input.Seasons.Count != 1 || input.Teams.Count < 2 || input.Matches.Count == 0 || input.Teams.Count > 100 || input.Matches.Count > 5000)
            return (null, new(IdentityResolutionFailureCode.InvalidInput, "Batch", "One complete bounded competition-season import is required."));
        var competition = input.Competitions[0].Competition;
        var season = input.Seasons[0].Season;
        var profile = InitialImportProfile.PremierLeague;
        var seasonScope = profile.SeasonScope(season.QueryYear);
        bool Identity(ProviderIdentity value, string scope) => value.ProviderKey == profile.ProviderKey && value.IdentityScope == scope &&
            long.TryParse(value.ExternalId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0 &&
            value.ExternalId == id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (competition.Code != profile.CompetitionCode || !Identity(competition.Identity, profile.CompetitionScope) || !Identity(season.Identity, seasonScope) ||
            season.QueryYear != season.StartDate.Year || season.EndDate <= season.StartDate ||
            season.Key != FormattableString.Invariant($"{season.StartDate.Year:D4}/{season.EndDate.Year % 100:D2}") ||
            input.Seasons[0].Competition is not EntityReference<SportStats.Domain.Platform.Competition>.External cr || cr.Identity != competition.Identity)
            return (null, new(IdentityResolutionFailureCode.InvalidInput, "Seasons", "The selected competition and season identities or relationships are invalid."));
        var teams = input.Teams.Select(x => x.Identity).ToHashSet();
        foreach (var team in input.Teams)
            if (!Identity(team.Identity, profile.CompetitionScope) || team.Season is not EntityReference<SportStats.Domain.Platform.Season>.External sr || sr.Identity != season.Identity)
                return (null, new(IdentityResolutionFailureCode.InvalidInput, "Teams", "Team identity or season relationship is invalid."));
        for (var i = 0; i < input.Matches.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var m = input.Matches[i].Match;
            if (!Identity(m.Identity, seasonScope) || m.Season != season || !teams.Contains(m.HomeTeam) || !teams.Contains(m.AwayTeam) ||
                input.Matches[i].Competition is not EntityReference<SportStats.Domain.Platform.Competition>.External mr || mr.Identity != competition.Identity ||
                DateOnly.FromDateTime(m.KickoffUtc) < season.StartDate || DateOnly.FromDateTime(m.KickoffUtc) > season.EndDate)
                return (null, new(IdentityResolutionFailureCode.RelationshipConflict, $"Matches[{i}]", "Match identity, season or participants do not match the selected import."));
            var scheduled = m.ProviderStatus == "TIMED" && m.Status == FootballMatchStatus.Scheduled && m.HomeGoals.Presence == ScoreFieldPresence.Null && m.AwayGoals.Presence == ScoreFieldPresence.Null && m.HomeGoals.Value is null && m.AwayGoals.Value is null;
            var finished = m.ProviderStatus == "FINISHED" && m.Status == FootballMatchStatus.Finished && m.HomeGoals.Presence == ScoreFieldPresence.Value && m.AwayGoals.Presence == ScoreFieldPresence.Value && m.HomeGoals.Value >= 0 && m.AwayGoals.Value >= 0;
            if ((!scheduled && !finished) || m.Matchday is null or <= 0 || m.KickoffUtc.Kind != DateTimeKind.Utc)
                return (null, new(IdentityResolutionFailureCode.InvalidInput, $"Matches[{i}]", "Unsupported status, score, kickoff or matchday."));
        }
        return result;
    }

    private void RequireEmptyTracker()
    {
        if (context.ChangeTracker.Entries().Any()) throw new InvalidOperationException("Import requires an empty dedicated context.");
    }
    private async Task VerifyTargetAsync(CancellationToken token)
    {
        var connection = context.Database.GetDbConnection();
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
            if (settings.Database != "sportstats_test" || settings.Username != "sportstats_app" || settings.Enlist || settings.Pooling)
                throw new InvalidOperationException("Importer requires explicit nonpooled sportstats_test application-role configuration.");
        }
        catch
        {
            throw new InvalidOperationException("Importer requires explicit nonpooled sportstats_test application-role configuration.");
        }
        if (connection.State != ConnectionState.Open)
        {
            await context.Database.OpenConnectionAsync(token);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT current_database(), current_user, session_user, r.rolsuper, r.rolcreatedb, r.rolcreaterole, r.rolbypassrls, EXISTS (SELECT 1 FROM pg_catalog.pg_roles elevated WHERE (elevated.rolsuper OR elevated.rolcreatedb OR elevated.rolcreaterole OR elevated.rolbypassrls) AND pg_catalog.pg_has_role(current_user, elevated.oid, 'MEMBER')) FROM pg_catalog.pg_roles r WHERE r.rolname = current_user";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || reader.GetString(0) != "sportstats_test" || reader.GetString(1) != "sportstats_app" || reader.GetString(2) != "sportstats_app" ||
            Enumerable.Range(3, 5).Any(reader.GetBoolean))
            throw new InvalidOperationException("Importer database identity or role safety verification failed.");
    }
    private static InitialImportResult Failure(IdentityResolutionFailure failure)
    {
        var entity = failure.Field.Split('[', '.')[0];
        var rejected = failure.Code == IdentityResolutionFailureCode.InvalidInput;
        return new(false, [new(entity, 0, 0, rejected ? 1 : 0, rejected ? 0 : 1)], [failure]);
    }
    private static InitialImportResult WithoutCreates(InitialImportResult result) => result with { Counts = result.Counts.Select(x => x with { Created = 0 }).ToArray() };
}
