using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SportStats.Application.Football.Ingestion;
using SportStats.Infrastructure;
using SportStats.Infrastructure.FootballData;
using SportStats.ProviderValidation;

if (!ValidationOptions.TryParse(args, out var options) || !options.Live)
{
    Console.WriteLine("No requests made. Usage: --live [--competition PL] [--year YYYY] [--spacing-seconds 7..300] [--compare]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    // No application host, database services, configuration files or startup side effects.
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var services = new ServiceCollection();
    services.AddFootballDataClient(configuration);
    services.AddSingleton<IFootballMatchObservationNormalizer, FootballDataNormalizer>();
    using var provider = services.BuildServiceProvider();
    var runner = new ProviderValidationRunner(provider.GetRequiredService<IFootballDataClient>(),
        provider.GetRequiredService<IFootballMatchObservationNormalizer>());
    return await runner.RunAsync(options, Console.Out, Console.In, cancellation.Token);
}
catch (OperationCanceledException) { Console.WriteLine("Validation cancelled."); return 130; }
catch (Exception) { Console.WriteLine("Validation failed; details suppressed to protect credentials and payloads."); return 1; }
