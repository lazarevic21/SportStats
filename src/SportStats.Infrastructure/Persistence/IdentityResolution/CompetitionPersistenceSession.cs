using Microsoft.EntityFrameworkCore;
using SportStats.Domain.Platform;

namespace SportStats.Infrastructure.Persistence.IdentityResolution;

// Offline seam for competition-only staging and saving; no transaction/connection capabilities.
internal interface ICompetitionPersistenceSession
{
    Task<Sport?> LoadSportAsync(Guid id, CancellationToken token);
    void StageCompetition(Competition competition);
    void StageMapping(CompetitionExternalIdentity mapping);
    Task SaveChangesAsync(CancellationToken token);
}

internal sealed class EfCompetitionPersistenceSession(SportStatsDbContext context) : ICompetitionPersistenceSession
{
    // A tracking query keeps the existing principal Unchanged. Explicit entry state below avoids
    // graph Add marking Sport as Added or allocating IDs through parameterless constructors.
    public Task<Sport?> LoadSportAsync(Guid id, CancellationToken token)
        => context.Sports.AsTracking().SingleOrDefaultAsync(sport => sport.Id == id, token);
    public void StageCompetition(Competition competition) => context.Entry(competition).State = EntityState.Added;
    public void StageMapping(CompetitionExternalIdentity mapping) => context.Entry(mapping).State = EntityState.Added;
    public async Task SaveChangesAsync(CancellationToken token) => await context.SaveChangesAsync(token);
}
