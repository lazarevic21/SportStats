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

public sealed record TeamContentionObservation(bool ServerObservedWait, bool OwnerPersisted,
    bool RolledBackParentRejected, bool RetryPersisted, bool ReplayWithoutWrites,
    bool TrackingEmpty, bool AllFixtureRowsRolledBack);

public sealed partial class PostgreSqlTestDatabaseFixture
{
    // Both callers roll back. The contender cannot see the owner's uncommitted parents;
    // after release it must reject that missing parent, then retry with its own seeds.
    // This verifies real resolver serialization, not reuse of a committed winner.
    public async Task<TeamContentionObservation> RunTeamContentionScenarioAsync(
        bool existingMappedTeam, CancellationToken token = default)
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
        var teamIds = new HashSet<Guid> { seededTeamId };
        var identity = new ProviderIdentity("fixture", Guid.NewGuid().ToString("N"), "contention-team");
        var batch = new IdentityResolutionBatch([], [],
            [new(new EntityReference<Season>.Internal(seasonId), new(identity, "Contention team", "CT", null))], []);
        var ownerTransaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token);
        IDbContextTransaction? contenderTransaction = null;
        Task<IdentityResolutionBatchResult>? pending = null;
        var ownerReleased = false;
        TeamContentionObservation? observation = null;
        try
        {
            scenarioInterceptor.ActivateTeam(connection, ownerTransaction.GetDbTransaction(), () => VerifyOwnedSessionAsync(deadline.Token));
            await new PostgreSqlAdvisoryLockAcquirer().AcquireAsync((NpgsqlTransaction)ownerTransaction.GetDbTransaction(),
                new FootballAdvisoryLockPlan([]), TimeSpan.FromSeconds(15), deadline.Token);
            await SeedTeamContentionParentsAsync(parentId, seasonId, seededTeamId, identity, existingMappedTeam, deadline.Token);
            scenarioInterceptor.ObserveTeamResolution(false);
            var owner = await new FootballResolutionOrchestrator(context, TimeSpan.FromSeconds(15))
                .PersistTeamsAsync(batch, deadline.Token) as IdentityResolutionBatchResult.Resolved
                ?? throw new InvalidOperationException("Owner team persistence did not resolve.");
            var ownerTeamId = owner.Teams.Single().EntityId;
            teamIds.Add(ownerTeamId);
            var ownerPersisted = await HasSingleTeamContentionGraphAsync(seasonId, ownerTeamId, identity, existingMappedTeam, deadline.Token) &&
                owner.Teams.Single().Disposition == (existingMappedTeam
                    ? IdentityResolutionDisposition.ExistingMapping : IdentityResolutionDisposition.CreatedEntity) &&
                (!existingMappedTeam || ownerTeamId == seededTeamId);

            contenderTransaction = await contender.context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token);
            contender.scenarioInterceptor.ActivateTeam(contender.connection, contenderTransaction.GetDbTransaction(),
                () => contender.VerifyOwnedSessionAsync(deadline.Token));
            contender.scenarioInterceptor.ObserveTeamResolution(false);
            pending = new FootballResolutionOrchestrator(contender.context, TimeSpan.FromSeconds(15))
                .PersistTeamsAsync(batch, acquisition.Token);
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
                contender.scenarioInterceptor.TeamWriteCount == 0 && !contender.context.ChangeTracker.Entries().Any();
            if (!rejected)
                throw new InvalidOperationException("The contender did not safely reject the rolled-back parent.");

            await contender.SeedTeamContentionParentsAsync(parentId, seasonId, seededTeamId, identity, existingMappedTeam, deadline.Token);
            contender.scenarioInterceptor.ObserveTeamResolution(false);
            var resolver = new FootballResolutionOrchestrator(contender.context, TimeSpan.FromSeconds(15));
            var retried = await resolver.PersistTeamsAsync(batch, deadline.Token) as IdentityResolutionBatchResult.Resolved
                ?? throw new InvalidOperationException("Contender retry did not resolve.");
            var retryId = retried.Teams.Single().EntityId;
            teamIds.Add(retryId);
            var retryCorrect = await contender.HasSingleTeamContentionGraphAsync(seasonId, retryId, identity, existingMappedTeam, deadline.Token) &&
                retried.Teams.Single().Disposition == (existingMappedTeam
                    ? IdentityResolutionDisposition.ExistingMapping : IdentityResolutionDisposition.CreatedEntity) &&
                (!existingMappedTeam || retryId == seededTeamId);
            var writes = contender.scenarioInterceptor.TeamWriteCount;
            var replay = await resolver.PersistTeamsAsync(batch, deadline.Token) as IdentityResolutionBatchResult.Resolved;
            var replayCorrect = replay?.Teams.Single().EntityId == retryId &&
                replay.Teams.Single().Disposition == IdentityResolutionDisposition.ExistingMapping &&
                contender.scenarioInterceptor.TeamWriteCount == writes && writes > 0;
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
                        throw new InvalidOperationException("Team contention cleanup failed; the contender was closed.");
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
            !await context.FootballTeams.AnyAsync(t => teamIds.Contains(t.Id), token) &&
            !await context.FootballTeamExternalIdentities.AnyAsync(m => m.ProviderKey == identity.ProviderKey && m.IdentityScope == identity.IdentityScope, token) &&
            !await context.FootballSeasonTeams.AnyAsync(m => m.SeasonId == seasonId, token);
        return observation! with { AllFixtureRowsRolledBack = removed };
    }

    private async Task SeedTeamContentionParentsAsync(Guid competitionId, Guid seasonId, Guid teamId,
        ProviderIdentity identity, bool existingMappedTeam, CancellationToken token)
    {
        var sport = await context.Sports.AsTracking().SingleOrDefaultAsync(s => s.Code == "football", token)
            ?? throw new InvalidOperationException("sportstats_test requires an existing football Sport; no sport will be inserted.");
        if (await context.Competitions.AnyAsync(c => c.SportId == sport.Id && c.Code == "PL", token))
            throw new InvalidOperationException("sportstats_test must have an unoccupied football/PL scope; permanent rows will not be changed.");
        var competition = new Competition(competitionId, sport, "PL", "Caller-owned contention parent");
        var season = new Season(seasonId, competition, "2026", new(2026, 8, 1), new(2027, 5, 31));
        context.Add(competition);
        context.Add(season);
        if (existingMappedTeam)
        {
            context.Add(new FootballTeam(teamId, "Existing contention team", "ECT", null));
            context.Add(new FootballTeamExternalIdentity(Guid.NewGuid(), identity.ProviderKey,
                identity.IdentityScope, identity.ExternalId, teamId));
        }
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();
    }

    private async Task<bool> HasSingleTeamContentionGraphAsync(Guid seasonId, Guid teamId,
        ProviderIdentity identity, bool existingMappedTeam, CancellationToken token)
        => await context.FootballTeams.CountAsync(t => t.Id == teamId &&
               t.Name == (existingMappedTeam ? "Existing contention team" : "Contention team") &&
               t.ShortName == (existingMappedTeam ? "ECT" : "CT") && t.CrestUrl == null, token) == 1 &&
           await context.FootballTeamExternalIdentities.CountAsync(m => m.TeamId == teamId &&
               m.ProviderKey == identity.ProviderKey && m.IdentityScope == identity.IdentityScope && m.ExternalId == identity.ExternalId, token) == 1 &&
           await context.FootballSeasonTeams.CountAsync(m => m.SeasonId == seasonId && m.TeamId == teamId, token) == 1;
}
