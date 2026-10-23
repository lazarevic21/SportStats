using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Competitions;
using SportStats.Application.Football.MatchMetadata;
using SportStats.Application.Football.Teams;
using SportStats.Application.Football.Matches;
using SportStats.Application.Football.Standings;
using SportStats.Infrastructure.Configuration;
using SportStats.Infrastructure.FootballData;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.Competitions;
using SportStats.Infrastructure.Persistence.MatchMetadata;
using SportStats.Infrastructure.Persistence.Teams;
using SportStats.Infrastructure.Persistence.Matches;
using SportStats.Infrastructure.Persistence.Standings;

namespace SportStats.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IFootballPayloadNormalizer, FootballDataNormalizer>();
        services.AddSingleton<IFootballMatchObservationNormalizer, FootballDataNormalizer>();
        services.AddSingleton<MatchMetadataChangeEvaluator>();
        services.AddScoped<IMatchMetadataIdentityReader, EfMatchMetadataIdentityReader>();
        services.AddScoped<ICompetitionReader, EfCompetitionReader>();
        services.AddScoped<ICompetitionSeasonReader, EfCompetitionSeasonReader>();
        services.AddScoped<ITeamReader, EfTeamReader>();
        services.AddScoped<IMatchReader, EfMatchReader>();
        services.AddScoped<IStandingsReader, EfStandingsReader>();
        services.AddScoped<IMatchMetadataEvaluationService, MatchMetadataEvaluationService>();
        services.AddFootballDataClient(configuration);
        return services.AddSportStatsDbContext<SportStatsDbContext>(configuration);
    }

    public static IServiceCollection AddFootballDataClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<FootballDataOptions>(options =>
        {
            var section = configuration.GetSection(FootballDataOptions.SectionName);
            options.ApiToken = section[nameof(FootballDataOptions.ApiToken)] ?? string.Empty;
            options.TimeoutSeconds = ReadInteger(section, nameof(FootballDataOptions.TimeoutSeconds), 30);
            options.MaxResponseBytes = ReadInteger(section, nameof(FootballDataOptions.MaxResponseBytes), 4 * 1024 * 1024);
        });
        services.AddHttpClient<IFootballDataClient, FootballDataClient>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            })
            .RedactLoggedHeaders(_ => true)
            .RemoveAllLoggers();
        return services;
    }

    private static int ReadInteger(IConfigurationSection section, string key, int defaultValue)
    {
        var value = section[key];
        if (value is null) return defaultValue;
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new InvalidOperationException("FootballData numeric configuration is invalid.");
        return parsed;
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
