using Microsoft.EntityFrameworkCore;
using SportStats.Domain.Platform;

namespace SportStats.Infrastructure.Persistence.IdentityResolution;

// Season-only staging seam; transaction and connection ownership stay with the shell.
internal interface ISeasonPersistenceSession
{
    Task<Competition?> LoadCompetitionAsync(Guid id, CancellationToken token);
    void StageSeason(Season season);
    void StageMapping(SeasonExternalIdentity mapping);
    Task SaveChangesAsync(CancellationToken token);
}

internal sealed class EfSeasonPersistenceSession(SportStatsDbContext context) : ISeasonPersistenceSession
{
    // Keep the existing principal Unchanged; explicit staging avoids graph Add of its parents.
    public Task<Competition?> LoadCompetitionAsync(Guid id, CancellationToken token)
        => context.Competitions.AsTracking().SingleOrDefaultAsync(competition => competition.Id == id, token);
    public void StageSeason(Season season) => context.Entry(season).State = EntityState.Added;
    public void StageMapping(SeasonExternalIdentity mapping) => context.Entry(mapping).State = EntityState.Added;
    public async Task SaveChangesAsync(CancellationToken token) => await context.SaveChangesAsync(token);
}
