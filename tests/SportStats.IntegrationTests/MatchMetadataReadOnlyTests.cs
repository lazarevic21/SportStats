using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;
using SportStats.Domain.Football;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.MatchMetadata;
using Xunit;

namespace SportStats.IntegrationTests;

// Execute the real Npgsql query pipeline against intercepted result sets; never open a socket.
public sealed class MatchMetadataReadOnlyTests
{
    private static readonly Guid Match = Guid.NewGuid(), Season = Guid.NewGuid(), Home = Guid.NewGuid(), Away = Guid.NewGuid();
    private static readonly DateTime Utc = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static ProviderIdentity Key(string id) => new("provider", "scope", id);
    private static MatchProviderObservation Observation => new(Key("match"), new("provider", "/matches"),
        new(Key("season"), 2026, "2026", new(2026, 8, 1), new(2027, 6, 1)), Key("home"), Key("away"),
        MatchObservationField<DateTime>.FromValue(Utc), MatchObservationField<int>.FromValue(1),
        MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Scheduled), "TIMED",
        MatchObservationField<int>.FromValue(0), MatchObservationField<int>.FromValue(0), default, Utc);

    [Fact]
    public async Task RealQueriesLoadPersistedMetadataWithoutTrackingOrWrites()
    {
        var commands = new Results();
        await using var context = Context(commands);
        var results = await new MatchMetadataEvaluationService(new EfMatchMetadataIdentityReader(context), new())
            .EvaluateBatchAsync([Observation, Observation]);
        Assert.All(results, x => Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, x.Disposition));
        Assert.Equal(6, commands.Sql.Count);
        Assert.All(commands.Sql, sql => Assert.StartsWith("SELECT", sql));
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Null(context.Database.CurrentTransaction);
        Assert.All(results, x => Assert.False(x.IsUpdateAuthorized));
    }

    [Fact]
    public async Task DuplicateMappingRowsSurviveDatabaseLoading()
    {
        var commands = new Results { Duplicate = true };
        await using var context = Context(commands);
        var result = Assert.Single(await new MatchMetadataEvaluationService(new EfMatchMetadataIdentityReader(context), new())
            .EvaluateBatchAsync([Observation]));
        Assert.Equal(SportStats.Application.Football.IdentityResolution.IdentityResolutionFailureCode.DuplicateMappings, result.Failure!.Code);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task DatabaseFailurePropagatesWithoutWrites()
    {
        var commands = new Results { Fail = true };
        await using var context = Context(commands);
        await Assert.ThrowsAsync<DataException>(() => new EfMatchMetadataIdentityReader(context).LoadAsync([Observation]));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("missing", MatchMetadataEvaluationDisposition.Conflict)]
    [InlineData("ambiguous", MatchMetadataEvaluationDisposition.Conflict)]
    [InlineData("dangling", MatchMetadataEvaluationDisposition.Conflict)]
    [InlineData("relationship", MatchMetadataEvaluationDisposition.Conflict)]
    [InlineData("concurrent-delete", MatchMetadataEvaluationDisposition.Conflict)]
    [InlineData("cross-tuple", MatchMetadataEvaluationDisposition.Unchanged)]
    public async Task LoadedRowsPreserveStrictResolution(string scenario, MatchMetadataEvaluationDisposition expected)
    {
        var commands = new Results { Scenario = scenario };
        await using var context = Context(commands);
        var tracked = new SportStats.Domain.Football.FootballTeam(Guid.NewGuid(), "Already tracked");
        context.Attach(tracked);
        var result = Assert.Single(await new MatchMetadataEvaluationService(new EfMatchMetadataIdentityReader(context), new())
            .EvaluateBatchAsync([Observation]));
        Assert.Equal(expected, result.Disposition);
        Assert.Equal(EntityState.Unchanged, Assert.Single(context.ChangeTracker.Entries()).State);
        Assert.All(commands.Sql, sql => Assert.StartsWith("SELECT", sql));
    }

    [Fact]
    public async Task LoadedAliasesAreEvaluatedTogetherInRequestOrder()
    {
        var commands = new Results { Scenario = "aliases" };
        await using var context = Context(commands);
        var service = new MatchMetadataEvaluationService(new EfMatchMetadataIdentityReader(context), new());
        var alias = Observation with { Identity = Key("alias") };
        var results = await service.EvaluateBatchAsync([alias, Observation]);
        Assert.All(results, x => Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, x.Disposition));
        var conflicts = await service.EvaluateBatchAsync([alias with { HomeGoals = MatchObservationField<int>.FromValue(1) }, Observation]);
        Assert.All(conflicts, x => Assert.Equal(MatchMetadataEvaluationDisposition.Conflict, x.Disposition));
        Assert.Equal(12, commands.Sql.Count);
    }

    [Fact]
    public async Task PrecancelledReaderDoesNotIssueQueries()
    {
        var commands = new Results();
        await using var context = Context(commands);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EfMatchMetadataIdentityReader(context)
            .LoadAsync([Observation], cancellation.Token));
        Assert.Empty(commands.Sql);
    }

    private static SportStatsDbContext Context(Results results) => new(new DbContextOptionsBuilder<SportStatsDbContext>()
        .UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused;Password=unused")
        .AddInterceptors(new NoConnection(), results).Options);

    private sealed class NoConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class Results : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public bool Duplicate { get; init; }
        public bool Fail { get; init; }
        public string? Scenario { get; init; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sql.Add(command.CommandText);
            Assert.StartsWith("SELECT", command.CommandText);
            if (Fail) throw new DataException("Synthetic database failure.");
            var sql = command.CommandText;
            DataTable table;
            if (sql.Contains("external_ids"))
            {
                table = Table(typeof(string), typeof(string), typeof(string), typeof(Guid));
                if (sql.Contains("match_external"))
                {
                    if (Scenario != "missing") table.Rows.Add("provider", "scope", "match", Match);
                    if (Duplicate) table.Rows.Add("provider", "scope", "match", Match);
                    if (Scenario == "ambiguous") table.Rows.Add("provider", "scope", "match", Guid.NewGuid());
                    if (Scenario == "cross-tuple") table.Rows.Add("provider", "other-scope", "match", Guid.NewGuid());
                    if (Scenario == "aliases") table.Rows.Add("provider", "scope", "alias", Match);
                }
                else if (sql.Contains("season_external")) table.Rows.Add("provider", "scope", "season", Season);
                else { table.Rows.Add("provider", "scope", "home", Home); table.Rows.Add("provider", "scope", "away", Away); }
            }
            else if (sql.Contains("football.matches"))
            {
                table = Table(typeof(Guid), typeof(Guid), typeof(Guid), typeof(Guid), typeof(DateTime), typeof(int), typeof(string), typeof(int), typeof(int));
                if (Scenario != "dangling") table.Rows.Add(Match, Season, Scenario == "relationship" ? Away : Home, Away, Utc, 1, "Scheduled", 0, 0);
            }
            else
            {
                table = Table(typeof(Guid));
                if (sql.Contains("platform.seasons"))
                {
                    if (Scenario != "concurrent-delete") table.Rows.Add(Season);
                }
                else { table.Rows.Add(Home); table.Rows.Add(Away); }
            }
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }

        private static DataTable Table(params Type[] types)
        {
            var table = new DataTable();
            for (var i = 0; i < types.Length; i++) table.Columns.Add("c" + i, types[i]);
            return table;
        }
    }
}
