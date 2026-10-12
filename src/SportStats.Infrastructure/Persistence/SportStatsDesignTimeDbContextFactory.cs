using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SportStats.Infrastructure.Persistence;

/// <summary>Creates the model for tooling without reading credentials or configuring a connection.</summary>
public sealed class SportStatsDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SportStatsDbContext>
{
    public SportStatsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SportStatsDbContext>()
            .UseNpgsql(postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "platform"))
            .EnableSensitiveDataLogging(false)
            .EnableDetailedErrors(false)
            .Options;

        return new SportStatsDbContext(options);
    }
}
