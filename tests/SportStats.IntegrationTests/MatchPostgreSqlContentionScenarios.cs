using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;

namespace SportStats.IntegrationTests;

public sealed record MatchContentionObservation(bool ServerObservedWait, bool OwnerPersisted,
    bool RolledBackParentRejected, bool RetryPersisted, bool ReplayWithoutWrites,
    bool TrackingEmpty, bool AllFixtureRowsRolledBack);

public sealed partial class PostgreSqlTestDatabaseFixture
{
    // Both callers roll back. The contender cannot see the owner's uncommitted parents;
    // after release it must reject that missing parent, then retry with its own seeds.
    // This verifies real resolver serialization, not reuse of a committed winner.
    // The caller takes coordination before seeding: failure verifies retention of that
    // pre-savepoint lock, not retention of data locks first acquired after the savepoint.
    public async Task<MatchContentionObservation> RunMatchContentionScenarioAsync(
        bool rollbackResolverSavepoint, CancellationToken token = default)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        token = deadline.Token;
        await VerifyOwnedSessionAsync(token);
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("Contention requires a clean fixture-owned session.");
        await using var contender = await OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"), token);
        await contender.VerifyOwnedSessionAsync(token);
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var parentId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var seededTeamId = Guid.NewGuid();
        var awayId = Guid.NewGuid();
        var matchIds = new HashSet<Guid>();
        var identity = new ProviderIdentity("fixture", Guid.NewGuid().ToString("N"), "contention-match");
        var input = new NormalizedMatch(identity,
            new(identity with { ExternalId = "season" }, 2026, "2026", new(2026, 8, 1), new(2027, 5, 31)),
            identity with { ExternalId = "home" }, identity with { ExternalId = "away" },
            new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc), FootballMatchStatus.Scheduled, "SCHEDULED", null,
            new(ScoreFieldPresence.Missing, null), new(ScoreFieldPresence.Null, null));
        var batch = new IdentityResolutionBatch([], [], [],
            [new(new EntityReference<Competition>.Internal(parentId), input)]);
        var ownerTransaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token);
        IDbContextTransaction? contenderTransaction = null;
        Task<IdentityResolutionBatchResult>? pending = null;
        var ownerReleased = false;
        MatchContentionObservation? observation = null;
        try
        {
            scenarioInterceptor.ActivateMatch(connection, ownerTransaction.GetDbTransaction(), () => VerifyOwnedSessionAsync(deadline.Token));
            await new PostgreSqlAdvisoryLockAcquirer().AcquireAsync((NpgsqlTransaction)ownerTransaction.GetDbTransaction(),
                new FootballAdvisoryLockPlan([]), TimeSpan.FromSeconds(15), deadline.Token);
            await SeedMatchContentionParentsAsync(parentId, seasonId, seededTeamId, awayId, identity, deadline.Token);
            scenarioInterceptor.ObserveMatchResolution(rollbackResolverSavepoint);
            var ownerPersisted = false;
            var autoSavepoints = context.Database.AutoSavepointsEnabled;
            if (rollbackResolverSavepoint) context.Database.AutoSavepointsEnabled = false;
            try
            {
                var owner = await new FootballResolutionOrchestrator(context, TimeSpan.FromSeconds(15))
                    .PersistMatchesAsync(batch, deadline.Token) as IdentityResolutionBatchResult.Resolved
                    ?? throw new InvalidOperationException("Owner match persistence did not resolve.");
                var ownerMatchId = owner.Matches.Single().EntityId;
                matchIds.Add(ownerMatchId);
                ownerPersisted = !rollbackResolverSavepoint &&
                    await HasSingleMatchContentionGraphAsync(seasonId, ownerMatchId, identity, deadline.Token);
            }
            catch (DbUpdateException exception) when (rollbackResolverSavepoint && exception.InnerException is InjectedMatchMappingFailure)
            {
                matchIds.UnionWith(scenarioInterceptor.InsertedMatchIds);
                ownerPersisted = scenarioInterceptor.EarlierInsertObserved &&
                    !await context.FootballMatches.AnyAsync(m => m.SeasonId == seasonId, deadline.Token) &&
                    await context.Seasons.AnyAsync(m => m.Id == seasonId, deadline.Token) &&
                    !context.ChangeTracker.Entries().Any();
            }
            finally { context.Database.AutoSavepointsEnabled = autoSavepoints; }
            contenderTransaction = await contender.context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token);
            contender.scenarioInterceptor.ActivateMatch(contender.connection, contenderTransaction.GetDbTransaction(),
                () => contender.VerifyOwnedSessionAsync(deadline.Token));
            contender.scenarioInterceptor.ObserveMatchResolution(false);
            pending = new FootballResolutionOrchestrator(contender.context, TimeSpan.FromSeconds(15))
                .PersistMatchesAsync(batch, acquisition.Token);
            var observed = false;
            while (!observed)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (pending.IsCompleted)
                {
                    await pending;
                    throw new InvalidOperationException("The competing resolver completed before caller rollback.");
                }
                await VerifyOwnedSessionAsync(deadline.Token);
                await using var command = new NpgsqlCommand("""
                    SELECT $1 = ANY(pg_catalog.pg_blocking_pids($2)) AND EXISTS (
                        SELECT 1 FROM pg_catalog.pg_locks
                        WHERE pid = $2 AND locktype = 'advisory' AND NOT granted)
                    """, connection, (NpgsqlTransaction)ownerTransaction.GetDbTransaction()) { CommandTimeout = 5 };
                command.Parameters.AddWithValue(connection.ProcessID);
                command.Parameters.AddWithValue(contender.connection.ProcessID);
                observed = (bool)(await command.ExecuteScalarAsync(deadline.Token)
                    ?? throw new InvalidOperationException("PostgreSQL did not return contention status."));
                if (!observed) await Task.Delay(25, deadline.Token);
            }
            await RollBackContentionTransactionAsync(this, ownerTransaction);
            ownerReleased = true;
            var afterRelease = await pending.WaitAsync(deadline.Token);
            var rejected = afterRelease is IdentityResolutionBatchResult.Failed failed &&
                failed.Failure.Code == IdentityResolutionFailureCode.MissingReference &&
                contender.scenarioInterceptor.MatchWriteCount == 0 && !contender.context.ChangeTracker.Entries().Any();
            if (!rejected)
                throw new InvalidOperationException("The contender did not safely reject the rolled-back parent.");

            await contender.SeedMatchContentionParentsAsync(parentId, seasonId, seededTeamId, awayId, identity, deadline.Token);
            contender.scenarioInterceptor.ObserveMatchResolution(false);
            var resolver = new FootballResolutionOrchestrator(contender.context, TimeSpan.FromSeconds(15));
            var retried = await resolver.PersistMatchesAsync(batch, deadline.Token) as IdentityResolutionBatchResult.Resolved
                ?? throw new InvalidOperationException("Contender retry did not resolve.");
            var retryId = retried.Matches.Single().EntityId;
            matchIds.Add(retryId);
            var retryCorrect = await contender.HasSingleMatchContentionGraphAsync(seasonId, retryId, identity, deadline.Token) &&
                retried.Matches.Single().Disposition == IdentityResolutionDisposition.CreatedEntity;
            var writes = contender.scenarioInterceptor.MatchWriteCount;
            var replay = await resolver.PersistMatchesAsync(batch, deadline.Token) as IdentityResolutionBatchResult.Resolved;
            var replayCorrect = replay?.Matches.Single().EntityId == retryId &&
                replay.Matches.Single().Disposition == IdentityResolutionDisposition.ExistingMapping &&
                contender.scenarioInterceptor.MatchWriteCount == writes && writes > 0;
            observation = new(observed, ownerPersisted, rejected, retryCorrect, replayCorrect,
                !context.ChangeTracker.Entries().Any() && !contender.context.ChangeTracker.Entries().Any(), false);
        }
        finally
        {
            acquisition.Cancel();
            try
            {
                if (pending is not null)
                {
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        contender.unusable = true;
                        await contender.connection.DisposeAsync();
                        throw new InvalidOperationException("Match contention cleanup failed; the contender was closed.");
                    }
                }
            }
            finally
            {
                try
                {
                    if (contenderTransaction is not null)
                    {
                        try { await RollBackContentionTransactionAsync(contender, contenderTransaction); }
                        finally { await contenderTransaction.DisposeAsync(); }
                    }
                }
                finally
                {
                    try { if (!ownerReleased) await RollBackContentionTransactionAsync(this, ownerTransaction); }
                    finally
                    {
                        scenarioInterceptor.Deactivate();
                        contender.scenarioInterceptor.Deactivate();
                        await ownerTransaction.DisposeAsync();
                    }
                }
            }
        }
        await VerifyOwnedSessionAsync(token);
        var removed = !await context.Competitions.AnyAsync(c => c.Id == parentId, token) &&
            !await context.Seasons.AnyAsync(s => s.Id == seasonId, token) &&
            !await context.FootballMatches.AnyAsync(t => t.SeasonId == seasonId || matchIds.Contains(t.Id), token) &&
            !await context.FootballTeams.AnyAsync(t => t.Id == seededTeamId || t.Id == awayId, token) &&
            !await context.FootballMatchExternalIdentities.AnyAsync(m => m.ProviderKey == identity.ProviderKey && m.IdentityScope == identity.IdentityScope, token) &&
            !await context.SeasonExternalIdentities.AnyAsync(m => m.SeasonId == seasonId, token) &&
            !await context.FootballTeamExternalIdentities.AnyAsync(m => m.TeamId == seededTeamId || m.TeamId == awayId, token) &&
            !await context.FootballSeasonTeams.AnyAsync(m => m.SeasonId == seasonId, token);
        return observation! with { AllFixtureRowsRolledBack = removed };
    }

    private async Task SeedMatchContentionParentsAsync(Guid competitionId, Guid seasonId, Guid homeId,
        Guid awayId, ProviderIdentity identity, CancellationToken token)
    {
        var sport = await context.Sports.AsTracking().SingleOrDefaultAsync(s => s.Code == "football", token)
            ?? throw new InvalidOperationException("sportstats_test requires an existing football Sport; no sport will be inserted.");
        if (await context.Competitions.AnyAsync(c => c.SportId == sport.Id && c.Code == "PL", token))
            throw new InvalidOperationException("sportstats_test must have an unoccupied football/PL scope; permanent rows will not be changed.");
        var competition = new Competition(competitionId, sport, "PL", "Caller-owned match contention parent");
        var season = new Season(seasonId, competition, "2026", new(2026, 8, 1), new(2027, 5, 31));
        var home = new FootballTeam(homeId, "Home contention team", "HCT", null);
        var away = new FootballTeam(awayId, "Away contention team", "ACT", null);
        context.Add(competition);
        context.Add(season);
        context.Add(home);
        context.Add(away);
        context.Add(new FootballSeasonTeam(season, home));
        context.Add(new FootballSeasonTeam(season, away));
        context.Add(new SeasonExternalIdentity(Guid.NewGuid(), identity.ProviderKey, identity.IdentityScope, "season", seasonId));
        context.Add(new FootballTeamExternalIdentity(Guid.NewGuid(), identity.ProviderKey, identity.IdentityScope, "home", homeId));
        context.Add(new FootballTeamExternalIdentity(Guid.NewGuid(), identity.ProviderKey, identity.IdentityScope, "away", awayId));
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();
    }

    private async Task<bool> HasSingleMatchContentionGraphAsync(Guid seasonId, Guid matchId,
        ProviderIdentity identity, CancellationToken token)
        => await context.FootballMatches.CountAsync(m => m.Id == matchId && m.SeasonId == seasonId, token) == 1 &&
           await context.FootballMatchExternalIdentities.CountAsync(m => m.MatchId == matchId &&
               m.ProviderKey == identity.ProviderKey && m.IdentityScope == identity.IdentityScope && m.ExternalId == identity.ExternalId, token) == 1;
}
