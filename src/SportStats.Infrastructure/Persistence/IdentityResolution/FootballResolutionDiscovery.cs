using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;

namespace SportStats.Infrastructure.Persistence.IdentityResolution;

internal static class FootballResolutionDiscovery
{
    // Foundation reader: complete scalar snapshots deliberately retain duplicate mappings.
    // Sequential queries respect the dedicated context/connection ownership contract.
    internal static async Task<FootballDiscovery> ReadAsync(SportStatsDbContext context, CancellationToken token)
    {
        var sports = await context.Sports.AsNoTracking().Select(x => new DiscoveredSport(x.Id, x.Code)).ToArrayAsync(token);
        var competitions = await context.Competitions.AsNoTracking()
            .Select(x => new DiscoveredCompetition(x.Id, x.SportId, x.Code)).ToArrayAsync(token);
        var seasons = await context.Seasons.AsNoTracking()
            .Select(x => new DiscoveredSeason(x.Id, x.CompetitionId, x.Key)).ToArrayAsync(token);
        var teams = await context.FootballTeams.AsNoTracking().Select(x => x.Id).ToArrayAsync(token);
        var memberships = await context.FootballSeasonTeams.AsNoTracking()
            .Select(x => new { x.SeasonId, x.TeamId }).ToArrayAsync(token);
        var matches = await context.FootballMatches.AsNoTracking()
            .Select(x => new DiscoveredMatch(x.Id, x.SeasonId, x.HomeTeamId, x.AwayTeamId)).ToArrayAsync(token);
        var mappings = new List<DiscoveredMapping>();
        mappings.AddRange(await context.CompetitionExternalIdentities.AsNoTracking().Select(x => new DiscoveredMapping(
            IdentityLockEntity.Competition, new ProviderIdentity(x.ProviderKey, x.IdentityScope, x.ExternalId), x.CompetitionId)).ToArrayAsync(token));
        mappings.AddRange(await context.SeasonExternalIdentities.AsNoTracking().Select(x => new DiscoveredMapping(
            IdentityLockEntity.Season, new ProviderIdentity(x.ProviderKey, x.IdentityScope, x.ExternalId), x.SeasonId)).ToArrayAsync(token));
        mappings.AddRange(await context.FootballTeamExternalIdentities.AsNoTracking().Select(x => new DiscoveredMapping(
            IdentityLockEntity.FootballTeam, new ProviderIdentity(x.ProviderKey, x.IdentityScope, x.ExternalId), x.TeamId)).ToArrayAsync(token));
        mappings.AddRange(await context.FootballMatchExternalIdentities.AsNoTracking().Select(x => new DiscoveredMapping(
            IdentityLockEntity.FootballMatch, new ProviderIdentity(x.ProviderKey, x.IdentityScope, x.ExternalId), x.MatchId)).ToArrayAsync(token));
        return new(Array.AsReadOnly(sports), Array.AsReadOnly(competitions), Array.AsReadOnly(seasons), Array.AsReadOnly(teams),
            Array.AsReadOnly(memberships.Select(x => (x.SeasonId, x.TeamId)).ToArray()), Array.AsReadOnly(matches), mappings.AsReadOnly());
    }

    /// <summary>Planning only; all transaction lifecycle and lock I/O belongs to existing helpers.
    /// Validation precedes transaction inspection and SQL. No resolver or persistence executor is registered.</summary>
    internal static async Task<FootballPlanningResult> PrepareAsync(SportStatsDbContext context,
        IdentityResolutionBatch batch, TimeSpan maxLockWait, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (snapshot, invalid) = FootballResolutionPlanner.SnapshotAndValidate(batch, cancellationToken);
        if (invalid is not null) return new(null, invalid);
        FootballPlanningResult? result = null;
        var shell = new FootballResolutionTransaction(context);
        var session = new EfFootballResolutionPreparationSession(context, maxLockWait);
        return await shell.ExecuteAsync(_ => Task.FromResult(result!), r => r.Failure is null,
            async token => { result = await PlanAndLockAsync(snapshot!, session, token); }, cancellationToken);
    }

    // Shared lock-phase preparation. Does not invoke a transaction shell; its caller owns that boundary.
    internal static async Task<FootballPlanningResult> PlanAndLockAsync(ValidatedFootballResolutionInput snapshot,
        IFootballResolutionPreparationSession session, CancellationToken token)
    {
        FootballPlanningResult? result = null;
        await session.AcquireAsync(async discoveryToken =>
        {
            result = new FootballResolutionPlanner().PlanValidated(snapshot, await session.ReadAsync(discoveryToken), discoveryToken);
            return result.Plan?.IndividualDataLocks ?? new FootballAdvisoryLockPlan([]);
        }, token);
        token.ThrowIfCancellationRequested();
        return result ?? throw new InvalidOperationException("Preparation did not produce a planning result.");
    }
}

// Offline I/O seam; production delegates transaction checks and lock ownership to existing helpers.
internal interface IFootballResolutionPreparationSession
{
    Task<FootballDiscovery> ReadAsync(CancellationToken token);
    Task AcquireAsync(Func<CancellationToken, Task<FootballAdvisoryLockPlan>> discover, CancellationToken token);
}

internal sealed class EfFootballResolutionPreparationSession(SportStatsDbContext context, TimeSpan maxLockWait)
    : IFootballResolutionPreparationSession
{
    public Task<FootballDiscovery> ReadAsync(CancellationToken token) => FootballResolutionDiscovery.ReadAsync(context, token);
    public Task AcquireAsync(Func<CancellationToken, Task<FootballAdvisoryLockPlan>> discover, CancellationToken token)
    {
        var transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        return new PostgreSqlAdvisoryLockAcquirer().AcquireDiscoveredAsync(transaction, discover, maxLockWait, token);
    }
}
