using Microsoft.EntityFrameworkCore;
using Npgsql;
using SportStats.Application.Football.InitialImport;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.InitialImport.Verification;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.InitialImport;

using var verificationTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = verificationTimeout.Token;
var stage = "approval-and-configuration";
// Deliberately separate from the rollback-only fixtures and the CLI. No connection fallback.
try
{
    var marker = Environment.GetEnvironmentVariable("SPORTSTATS_DISPOSABLE_MARKER");
    var settings = DisposableVerificationContract.ValidateConfiguration(args,
        Environment.GetEnvironmentVariable("SPORTSTATS_DISPOSABLE_APPROVAL"), marker,
        Environment.GetEnvironmentVariable("SPORTSTATS_DISPOSABLE_ADMIN_CONNECTION"));
    await using var admin = new NpgsqlConnection(settings.ConnectionString);
    stage = "admin-connection";
    await admin.OpenAsync(token);
    stage = "database-isolation-guard";
    await using (var guard = new NpgsqlCommand("SELECT current_setting('sportstats.disposable_marker', true), (SELECT count(*) FROM pg_catalog.pg_namespace WHERE nspname IN ('platform','football')), current_user, session_user", admin))
    await using (var reader = await guard.ExecuteReaderAsync(token))
        if (!await reader.ReadAsync(token) || reader.IsDBNull(0) || reader.GetString(0) != marker || reader.GetInt64(1) != 0 || reader.GetString(2) != "postgres" || reader.GetString(3) != "postgres") throw new InvalidOperationException();
    var sportId = await new DisposableMigrationVerification(settings.ConnectionString, marker!, value => stage = value).RunAsync(token);
    // Generated hexadecimal password has no quoting ambiguity and is never printed.
    stage = "application-role-creation";
    var password = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    await using (var role = new NpgsqlCommand($"CREATE ROLE sportstats_app LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS; GRANT CONNECT ON DATABASE sportstats_test TO sportstats_app; GRANT USAGE ON SCHEMA platform, football TO sportstats_app; GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA platform, football TO sportstats_app;", admin))
        await role.ExecuteNonQueryAsync(token);
    settings.Username = "sportstats_app"; settings.Password = password;
    var options = new DbContextOptionsBuilder<SportStatsDbContext>()
        .UseNpgsql(settings.ConnectionString, postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "platform")).Options;
    var competition = new NormalizedCompetition(new("football-data.org", "competition:PL", "2021"), "PL", "Premier League");
    var season = new NormalizedSeason(new("football-data.org", "competition:PL/season:2026", "2502"), 2026, "2026/27", new(2026, 8, 21), new(2027, 5, 30));
    var home = new NormalizedTeam(new("football-data.org", "competition:PL", "57"), "Home", null, null);
    var away = new NormalizedTeam(new("football-data.org", "competition:PL", "65"), "Away", null, null);
    var match = new NormalizedMatch(new("football-data.org", "competition:PL/season:2026", "560542"), season, home.Identity, away.Identity,
        new(2026, 8, 21, 19, 0, 0, DateTimeKind.Utc), FootballMatchStatus.Finished, "FINISHED", 1,
        new(ScoreFieldPresence.Value, 0), new(ScoreFieldPresence.Value, 2));
    var batch = new InitialImportBundle(competition, season, [home, away], [match]).ToBatch(sportId);
    await using (var first = new SportStatsDbContext(options))
    {
        stage = "initial-import";
        var created = await new EfInitialImportStore(first).ApplyForSafetyVerificationAsync(batch, token);
        Require(created.Success && created.Applied && created.Counts.Single(x => x.Entity == "Matches").Created == 1);
        Require(first.Database.CurrentTransaction is null && !first.ChangeTracker.Entries().Any());
    }
    // Each read and replay uses a fresh physical session. Snapshot includes every stored column.
    stage = "committed-snapshot";
    var committed = await Snapshot(settings.ConnectionString, token);
    await using (var replay = new SportStatsDbContext(options))
    {
        stage = "identical-replay";
        var result = await new EfInitialImportStore(replay).ApplyForSafetyVerificationAsync(batch, token);
        Require(result.Success && result.Applied && result.Counts.All(x => x.Created == 0));
    }
    stage = "identical-replay-snapshot";
    Require(committed == await Snapshot(settings.ConnectionString, token));
    await using (var conflict = new SportStatsDbContext(options))
    {
        stage = "conflict-replay";
        var changed = batch with { Matches = [batch.Matches[0] with { Match = match with { Matchday = 2, HomeGoals = new(ScoreFieldPresence.Value, 7) } }] };
        var result = await new EfInitialImportStore(conflict).ApplyForSafetyVerificationAsync(changed, token);
        Require(!result.Success && !result.Applied && result.Counts.All(x => x.Created == 0));
        Require(conflict.Database.CurrentTransaction is null && !conflict.ChangeTracker.Entries().Any());
    }
    stage = "conflict-preservation-snapshot";
    Require(committed == await Snapshot(settings.ConnectionString, token));
    Console.WriteLine("Disposable durable commit, fresh-session identical replay, and conflict preservation passed. Public apply remains disabled.");
    return 0;
}
catch (Exception exception)
{
    // Provider exceptions and connection strings must never reach terminal output.
    // Only fixed classifications, safe diagnostic codes and stage constants.
    var category = exception switch
    {
        OperationCanceledException => "cancelled-or-timeout",
        PostgresException => "postgresql-error",
        NpgsqlException => "connection-or-provider-error",
        IOException => "file-io-error",
        InvalidOperationException => "guard-or-assertion",
        ArgumentException => "invalid-configuration",
        _ => "unexpected-error"
    };
    Console.Error.WriteLine($"Disposable verification refused or failed at stage '{stage}' ({category}; {DisposableFailureDiagnostics.Describe(exception)}). No release gate was enabled.");
    return 1;
}

static void Require(bool condition) { if (!condition) throw new InvalidOperationException(); }
static async Task<string> Snapshot(string connectionString, CancellationToken token)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync(token);
    var rows = new List<string>();
    foreach (var table in new[] { "platform.competitions", "platform.competition_external_ids", "platform.seasons", "platform.season_external_ids", "football.teams", "football.team_external_ids", "football.matches", "football.match_external_ids", "football.season_teams" })
    {
        await using var command = new NpgsqlCommand($"SELECT COALESCE(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text)::text, '[]') FROM {table} t", connection);
        rows.Add(table + ":" + (string)(await command.ExecuteScalarAsync(token))!);
    }
    return string.Join('\n', rows);
}
