using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;

namespace SportStats.IntegrationTests;

public enum SeasonScenario { CreateInternal, CreateExternal, OccupiedNaturalKey, IncorrectParent, InconsistentKey, FailureRollback, CancellationRollback }
public sealed record SeasonScenarioObservation(bool IdentityAndMetadataCorrect, bool ReplayWithoutWrites,
    bool ExpectedConflictRejected, bool EarlierInsertObserved, bool CallerTransactionPreserved,
    bool CallerWorkPreserved, bool TrackingEmpty, bool AllFixtureRowsRolledBack);

public sealed partial class PostgreSqlTestDatabaseFixture
{
    public async Task<SeasonScenarioObservation> RunSeasonScenarioAsync(SeasonScenario scenario, CancellationToken token = default)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        if (!Enum.IsDefined(scenario) || unusable) throw new InvalidOperationException("The guarded scenario is unavailable.");
        await VerifyOwnedSessionAsync(token);
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("Scenarios require a clean fixture-owned session.");
        var parentId = Guid.NewGuid();
        var otherParentId = Guid.NewGuid();
        var markerCode = "fixture-season-" + otherParentId.ToString("N");
        var identity = new ProviderIdentity("fixture", Guid.NewGuid().ToString("N"), " 000season opaque ");
        var parentIdentity = identity with { ExternalId = " 000PL opaque " };
        var seasonIds = new HashSet<Guid>();
        SeasonScenarioObservation? observed = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        try
        {
            scenarioInterceptor.ActivateSeason(connection, transaction.GetDbTransaction(), () => VerifyOwnedSessionAsync(token));
            await new PostgreSqlAdvisoryLockAcquirer().AcquireAsync((NpgsqlTransaction)transaction.GetDbTransaction(),
                new FootballAdvisoryLockPlan([]), TimeSpan.FromSeconds(10), token);
            var sport = await context.Sports.AsTracking().SingleOrDefaultAsync(s => s.Code == "football", token)
                ?? throw new InvalidOperationException("sportstats_test requires an existing football Sport; no sport will be inserted.");
            if (await context.Competitions.AnyAsync(c => c.SportId == sport.Id && c.Code == "PL", token))
                throw new InvalidOperationException("sportstats_test must have an unoccupied football/PL scope; permanent rows will not be changed.");
            var parent = new Competition(parentId, sport, "PL", "Caller-owned parent");
            var other = new Competition(otherParentId, sport, markerCode, "Caller-owned marker");
            context.Add(parent); context.Add(other);
            context.Add(new CompetitionExternalIdentity(Guid.NewGuid(), parentIdentity.ProviderKey,
                parentIdentity.IdentityScope, parentIdentity.ExternalId, parentId));
            if (scenario is SeasonScenario.OccupiedNaturalKey or SeasonScenario.IncorrectParent or SeasonScenario.InconsistentKey)
            {
                var seeded = new Season(Guid.NewGuid(), scenario == SeasonScenario.IncorrectParent ? other : parent,
                    scenario == SeasonScenario.InconsistentKey ? "2025" : "2026", new(2026, 8, 1), new(2027, 5, 31));
                context.Add(seeded); seasonIds.Add(seeded.Id);
                if (scenario != SeasonScenario.OccupiedNaturalKey)
                    context.Add(new SeasonExternalIdentity(Guid.NewGuid(), identity.ProviderKey, identity.IdentityScope, identity.ExternalId, seeded.Id));
            }
            await context.SaveChangesAsync(token);
            context.ChangeTracker.Clear();
            var failure = scenario == SeasonScenario.FailureRollback;
            var cancelled = scenario == SeasonScenario.CancellationRollback;
            scenarioInterceptor.ObserveSeasonResolution(failure, cancelled ? cancellation : null);
            if (failure || cancelled) context.Database.AutoSavepointsEnabled = false;
            EntityReference<Competition> reference = scenario == SeasonScenario.CreateExternal
                ? new EntityReference<Competition>.External(parentIdentity) : new EntityReference<Competition>.Internal(parentId);
            var input = new NormalizedSeason(identity, 2026, "2026", new(2026, 8, 1), new(2027, 5, 31));
            var batch = new IdentityResolutionBatch([], [new(reference, input)], [], []);
            var orchestrator = new FootballResolutionOrchestrator(context, TimeSpan.FromSeconds(10));
            var identityCorrect = false;
            var replay = false;
            var rejected = false;
            if (failure || cancelled)
            {
                var expected = false;
                try { await orchestrator.PersistSeasonsAsync(batch, cancellation.Token); }
                catch (DbUpdateException exception) when (failure && exception.InnerException is InjectedSeasonMappingFailure) { expected = true; }
                catch (OperationCanceledException) when (cancelled && cancellation.IsCancellationRequested) { expected = true; }
                if (!expected || !scenarioInterceptor.EarlierInsertObserved)
                    throw new InvalidOperationException("Expected failure after a successful season INSERT was not observed.");
                seasonIds.UnionWith(scenarioInterceptor.InsertedSeasonIds);
                if (await context.Seasons.AnyAsync(s => s.CompetitionId == parentId, token) ||
                    await context.SeasonExternalIdentities.AnyAsync(m => m.ProviderKey == identity.ProviderKey && m.IdentityScope == identity.IdentityScope, token))
                    throw new InvalidOperationException("Resolver savepoint rollback left partial season writes.");
            }
            else if (scenario is SeasonScenario.OccupiedNaturalKey or SeasonScenario.IncorrectParent or SeasonScenario.InconsistentKey)
            {
                var result = await orchestrator.PersistSeasonsAsync(batch, token);
                var expectedCode = scenario == SeasonScenario.IncorrectParent
                    ? IdentityResolutionFailureCode.RelationshipConflict : IdentityResolutionFailureCode.NaturalKeyConflict;
                rejected = result is IdentityResolutionBatchResult.Failed failed && failed.Failure.Code == expectedCode &&
                    scenarioInterceptor.SeasonWriteCount == 0;
            }
            else
            {
                var first = await orchestrator.PersistSeasonsAsync(batch, token) as IdentityResolutionBatchResult.Resolved
                    ?? throw new InvalidOperationException("Season creation failed.");
                var id = first.Seasons.Single().EntityId;
                seasonIds.Add(id);
                var stored = await context.Seasons.AsNoTracking().SingleAsync(s => s.Id == id, token);
                var mapping = await context.SeasonExternalIdentities.AsNoTracking().SingleAsync(m => m.SeasonId == id, token);
                identityCorrect = id != Guid.Empty && mapping.Id != Guid.Empty && mapping.Id != id && stored.CompetitionId == parentId &&
                    stored.Key == input.Key && stored.StartDate == input.StartDate && stored.EndDate == input.EndDate &&
                    mapping.ProviderKey == identity.ProviderKey && mapping.IdentityScope == identity.IdentityScope && mapping.ExternalId == identity.ExternalId &&
                    first.Seasons[0].Disposition == IdentityResolutionDisposition.CreatedEntity;
                var writes = scenarioInterceptor.SeasonWriteCount;
                var exactReplay = await orchestrator.PersistSeasonsAsync(batch, token) as IdentityResolutionBatchResult.Resolved;
                var exactReplayCorrect = exactReplay?.Seasons.Single().EntityId == id &&
                    exactReplay.Seasons[0].Disposition == IdentityResolutionDisposition.ExistingMapping &&
                    scenarioInterceptor.SeasonWriteCount == writes && !context.ChangeTracker.Entries().Any();
                var changed = batch with { Seasons = [new(reference, input with { StartDate = new(2026, 7, 1), EndDate = new(2027, 6, 30) })] };
                var second = await orchestrator.PersistSeasonsAsync(changed, token) as IdentityResolutionBatchResult.Resolved;
                var after = await context.Seasons.AsNoTracking().SingleAsync(s => s.Id == id, token);
                replay = exactReplayCorrect && second?.Seasons.Single().EntityId == id && second.Seasons[0].Disposition == IdentityResolutionDisposition.ExistingMapping &&
                    scenarioInterceptor.SeasonWriteCount == writes && writes > 0 && after.StartDate == input.StartDate && after.EndDate == input.EndDate &&
                    await context.Seasons.CountAsync(s => s.CompetitionId == parentId, token) == 1 &&
                    await context.SeasonExternalIdentities.CountAsync(m => m.SeasonId == id, token) == 1;
            }
            var empty = !context.ChangeTracker.Entries().Any();
            var preserved = ReferenceEquals(context.Database.CurrentTransaction, transaction) && transaction.GetDbTransaction().IsolationLevel == IsolationLevel.ReadCommitted;
            var callerWork = await context.Competitions.AsNoTracking().AnyAsync(c => c.Id == parentId && c.Name == "Caller-owned parent", token) &&
                await context.Competitions.AsNoTracking().AnyAsync(c => c.Id == otherParentId && c.Code == markerCode && c.Name == "Caller-owned marker", token) &&
                await context.CompetitionExternalIdentities.AnyAsync(m => m.CompetitionId == parentId && m.ExternalId == parentIdentity.ExternalId, token);
            observed = new(identityCorrect, replay, rejected, scenarioInterceptor.EarlierInsertObserved, preserved, callerWork, empty, false);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await transaction.RollbackAsync(cleanup.Token); cleanup.Token.ThrowIfCancellationRequested(); }
            catch
            {
                unusable = true;
                await connection.DisposeAsync();
                throw new InvalidOperationException("Full test transaction rollback could not be confirmed; the fixture was closed and is unusable.");
            }
            finally
            {
                scenarioInterceptor.Deactivate();
                context.ChangeTracker.Clear();
                context.Database.AutoSavepointsEnabled = true;
                await transaction.DisposeAsync();
            }
        }
        await VerifyOwnedSessionAsync(token);
        var removed = !await context.Competitions.AnyAsync(c => c.Id == parentId || c.Id == otherParentId, token) &&
            !await context.CompetitionExternalIdentities.AnyAsync(m => m.CompetitionId == parentId, token) &&
            !await context.Seasons.AnyAsync(s => seasonIds.Contains(s.Id) || s.CompetitionId == parentId || s.CompetitionId == otherParentId, token) &&
            !await context.SeasonExternalIdentities.AnyAsync(m => seasonIds.Contains(m.SeasonId) ||
                (m.ProviderKey == identity.ProviderKey && m.IdentityScope == identity.IdentityScope), token);
        return observed! with { AllFixtureRowsRolledBack = removed };
    }
}
