using Microsoft.EntityFrameworkCore;
using SportStats.Domain.Platform;
using SportStats.Domain.Football;

namespace SportStats.Infrastructure.Persistence;

public sealed class SportStatsDbContext(DbContextOptions<SportStatsDbContext> options) : DbContext(options)
{
    public DbSet<Sport> Sports => Set<Sport>();
    public DbSet<Competition> Competitions => Set<Competition>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<FootballTeam> FootballTeams => Set<FootballTeam>();
    public DbSet<FootballSeasonTeam> FootballSeasonTeams => Set<FootballSeasonTeam>();
    public DbSet<FootballMatch> FootballMatches => Set<FootballMatch>();
    public DbSet<FootballStandingSnapshot> FootballStandingSnapshots => Set<FootballStandingSnapshot>();
    public DbSet<FootballStandingRow> FootballStandingRows => Set<FootballStandingRow>();
    public DbSet<CompetitionExternalIdentity> CompetitionExternalIdentities => Set<CompetitionExternalIdentity>();
    public DbSet<SeasonExternalIdentity> SeasonExternalIdentities => Set<SeasonExternalIdentity>();
    public DbSet<FootballTeamExternalIdentity> FootballTeamExternalIdentities => Set<FootballTeamExternalIdentity>();
    public DbSet<FootballMatchExternalIdentity> FootballMatchExternalIdentities => Set<FootballMatchExternalIdentity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SportStatsDbContext).Assembly);
    }
}
