using Microsoft.EntityFrameworkCore;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;

namespace SportStats.Infrastructure.Persistence.IdentityResolution;

// Team-only staging seam; transaction and connection ownership stay with the shell.
internal interface ITeamPersistenceSession
{
    Task<Season?> LoadSeasonAsync(Guid id, CancellationToken token);
    Task<FootballTeam?> LoadTeamAsync(Guid id, CancellationToken token);
    void StageTeam(FootballTeam team);
    void StageMapping(FootballTeamExternalIdentity mapping);
    void StageMembership(FootballSeasonTeam membership);
    Task SaveChangesAsync(CancellationToken token);
}

internal sealed class EfTeamPersistenceSession(SportStatsDbContext context) : ITeamPersistenceSession
{
    // Membership construction validates the season's sport through this principal graph.
    public Task<Season?> LoadSeasonAsync(Guid id, CancellationToken token)
        => context.Seasons.AsTracking().Include(season => season.Competition).ThenInclude(competition => competition.Sport)
            .SingleOrDefaultAsync(season => season.Id == id, token);
    public Task<FootballTeam?> LoadTeamAsync(Guid id, CancellationToken token)
        => context.FootballTeams.AsTracking().SingleOrDefaultAsync(team => team.Id == id, token);
    // Explicit staging never marks existing principals or their graphs Added/Modified.
    public void StageTeam(FootballTeam team) => context.Entry(team).State = EntityState.Added;
    public void StageMapping(FootballTeamExternalIdentity mapping) => context.Entry(mapping).State = EntityState.Added;
    public void StageMembership(FootballSeasonTeam membership) => context.Entry(membership).State = EntityState.Added;
    public async Task SaveChangesAsync(CancellationToken token) => await context.SaveChangesAsync(token);
}
