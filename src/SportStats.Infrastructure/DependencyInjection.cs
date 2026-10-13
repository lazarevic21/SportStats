using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SportStats.Application.Football.Ingestion;
using SportStats.Infrastructure.Configuration;
using SportStats.Infrastructure.FootballData;
using SportStats.Infrastructure.Persistence;

namespace SportStats.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IFootballPayloadNormalizer, FootballDataNormalizer>();
        return services.AddSportStatsDbContext<SportStatsDbContext>(configuration);
    }

    public static IServiceCollection AddSportStatsDbContext<TContext>(this IServiceCollection services,
        IConfiguration configuration) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The callback runs when a context is resolved, never during API startup.
        services.AddDbContext<TContext>(options =>
        {
            options.UseNpgsql(DatabaseConnectionString.GetApplicationConnectionString(configuration),
                postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "platform"));
            options.EnableSensitiveDataLogging(false);
            options.EnableDetailedErrors(false);
        });
        return services;
    }
}
