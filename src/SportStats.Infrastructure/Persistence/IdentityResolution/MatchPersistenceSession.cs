using Microsoft.EntityFrameworkCore;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;

namespace SportStats.Infrastructure.Persistence.IdentityResolution;

// Match-only staging seam; transaction and connection ownership stay with the shell.
internal interface IMatchPersistenceSession
{
    Task<Season?> LoadSeasonAsync(Guid id, CancellationToken token);
    Task<FootballSeasonTeam?> LoadMembershipAsync(Guid seasonId, Guid teamId, CancellationToken token);
    void StageMatch(FootballMatch match);
    void StageMapping(FootballMatchExternalIdentity mapping);
    Task SaveChangesAsync(CancellationToken token);
}

internal sealed class EfMatchPersistenceSession(SportStatsDbContext context) : IMatchPersistenceSession
{
    public Task<Season?> LoadSeasonAsync(Guid id, CancellationToken token)
        => context.Seasons.AsTracking().Include(season => season.Competition).ThenInclude(competition => competition.Sport)
            .SingleOrDefaultAsync(season => season.Id == id, token);
    public Task<FootballSeasonTeam?> LoadMembershipAsync(Guid seasonId, Guid teamId, CancellationToken token)
        => context.FootballSeasonTeams.AsTracking().Include(membership => membership.Team)
            .SingleOrDefaultAsync(membership => membership.SeasonId == seasonId && membership.TeamId == teamId, token);
    // Explicit entry state avoids inserting or modifying existing principal graphs.
    public void StageMatch(FootballMatch match) => context.Entry(match).State = EntityState.Added;
    public void StageMapping(FootballMatchExternalIdentity mapping) => context.Entry(mapping).State = EntityState.Added;
    public async Task SaveChangesAsync(CancellationToken token) => await context.SaveChangesAsync(token);
}
