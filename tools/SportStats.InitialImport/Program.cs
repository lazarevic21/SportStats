using SportStats.InitialImport;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.InitialImport;
using SportStats.Infrastructure;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.InitialImport;

// Apply remains deliberately unavailable until guarded PostgreSQL verification is complete.
const bool applyVerified = false;
if (!ImportOptions.TryParse(args, out var options))
{
    Console.WriteLine("No requests made. Usage: --live --competition PL --year YYYY [--apply]");
    return 2;
}
if (options.Apply && !applyVerified)
{
    Console.WriteLine("Apply disabled: transaction atomicity and database safety verification is required.");
    return 2;
}
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    // Environment only: no configuration files, application host, migrations or startup jobs.
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    configuration["ConnectionStrings:SportStats"] = ImportDatabaseConfiguration.Validate(
        configuration.GetConnectionString("SportStats"));
    var services = new ServiceCollection();
    services.AddInfrastructure(configuration);
    services.AddScoped<IInitialImportStore, EfInitialImportStore>();
    using var provider = services.BuildServiceProvider();
    using var scope = provider.CreateScope();
    var store = scope.ServiceProvider.GetRequiredService<IInitialImportStore>();
    // Verify the guarded target before any provider request.
    var sportId = await store.ResolveFootballSportIdAsync(cancellation.Token);
    var validator = new InitialImportValidator(scope.ServiceProvider.GetRequiredService<IFootballPayloadNormalizer>(),
        scope.ServiceProvider.GetRequiredService<IFootballMatchObservationNormalizer>());
    var fetcher = new InitialImportFetcher(scope.ServiceProvider.GetRequiredService<IFootballDataClient>(), validator);
    var bundle = await fetcher.FetchAsync(InitialImportProfile.Select(options.Competition), options.Year, cancellation.Token);
    var batch = bundle.ToBatch(sportId);
    var preview = await store.PreviewAsync(batch, cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { Mode = "preview", Result = preview }));
    if (!preview.Success) return 1;
    if (!options.Apply) return 0;
    Console.WriteLine("Type APPLY PL CREATE ONLY to confirm this invocation's create-only transaction:");
    if (Console.IsInputRedirected || await Console.In.ReadLineAsync(cancellation.Token) != "APPLY PL CREATE ONLY")
    {
        Console.WriteLine("Apply not confirmed. No writes performed.");
        return 2;
    }
    var applied = await store.ApplyAsync(batch, cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { Mode = "apply", Result = applied }));
    return applied.Success && applied.Applied ? 0 : 1;
}
catch (OperationCanceledException) { Console.WriteLine("Import cancelled; no success is claimed."); return 130; }
catch (FormatException)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Mode = "rejected", Applied = false,
        Counts = new[] { new InitialImportCount("snapshot", 0, 0, 1, 0) },
        Reason = "Provider snapshot failed strict import validation."
    }));
    return 1;
}
catch (Exception)
{
    Console.WriteLine("Import failed; no success is claimed. Details suppressed to protect credentials and payloads. Re-preview before retrying.");
    return 1;
}


