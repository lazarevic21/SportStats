using System.Data.Common;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SportStats.Application.Football.Standings;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.Infrastructure;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.Standings;
using Xunit;

namespace SportStats.UnitTests;

public sealed class StandingsReaderTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(2, false, 2)]
    [InlineData(3, true, 2)]
    public void LookaheadPreservesMetadataAndTrimsOnlyExtraRow(int count, bool hasNext, int expectedCount)
    {
        var request = new StandingsPageRequest(Guid.NewGuid(), 4, 2);
        var snapshot = Metadata();
        var rows = Enumerable.Range(1, count).Select(i => new StandingRowResponse(i, new(Guid.NewGuid(), $"Team {i}"))).ToList();
        var expected = rows.Take(expectedCount).ToArray();
        var result = EfStandingsReader.CreatePage(new(snapshot, rows), request);
        Assert.Equal(snapshot, result.Snapshot);
        Assert.Equal(request.SeasonId, result.SeasonId);
        Assert.Equal(expected, result.Items);
        Assert.Equal(hasNext, result.HasNextPage);
        Assert.Equal(4, result.Page);
        Assert.Equal(2, result.PageSize);
    }

    [Fact]
    public void MissingSnapshotReturnsEmptyPageWithNullMetadata()
    {
        var request = new StandingsPageRequest(Guid.NewGuid(), 50, 2);
        var result = EfStandingsReader.CreatePage(null, request);
        Assert.Equal(request.SeasonId, result.SeasonId);
        Assert.Null(result.Snapshot);
        Assert.Empty(result.Items);
        Assert.False(result.HasNextPage);
        Assert.Equal(50, result.Page);
    }

    [Fact]
    public void QueryTranslatesToOneCorrelatedBoundedProjectionWithoutConnecting()
    {
        var guard = new RejectConnection();
        using var context = Context(guard);
        var seasonId = Guid.NewGuid();
        var query = new EfStandingsReader(context).BuildQuery(new(seasonId, 3, 10));
        var sql = query.ToQueryString();
        Assert.Contains("football.standing_snapshots", sql);
        Assert.Contains("football.standing_rows", sql);
        Assert.Contains("football.teams", sql);
        Assert.DoesNotContain("platform.seasons", sql);
        Assert.DoesNotContain("season_teams", sql);
        Assert.Contains(seasonId.ToString(), sql);
        Assert.Contains("LATERAL", sql);
        Assert.Contains("LEFT JOIN", sql);
        var snapshotLimit = System.Text.RegularExpressions.Regex.Match(sql, @"LIMIT (@\w+)");
        Assert.True(snapshotLimit.Success);
        Assert.Contains($"-- {snapshotLimit.Groups[1].Value}='1'", sql);
        Assert.True(snapshotLimit.Index < sql.IndexOf("LEFT JOIN LATERAL", StringComparison.Ordinal));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, @"\bJOIN\b").Count);
        Assert.Contains("LIMIT @", sql);
        Assert.Contains("OFFSET @", sql);
        Assert.Matches(@"=\s*'?11'?", sql);
        Assert.Matches(@"=\s*'?20'?", sql);
        Assert.Matches("ORDER BY [^\r\n]*\"RetrievedAtUtc\" DESC, [^\r\n]*\"Id\" DESC", sql);
        Assert.Matches("ORDER BY [^\r\n]*\"Position\", [^\r\n]*\"TeamId\"", sql);
        Assert.DoesNotContain("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AsNoTracking", query.Expression.ToString());
        Assert.Contains("AsSingleQuery", query.Expression.ToString());
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void SyntheticQuerySelectsLatestSnapshotBeforeOrderingAndPagingItsRows()
    {
        var guard = new RejectConnection();
        using var context = Context(guard);
        var season = Season();
        var otherSeason = Season();
        var date = new DateTime(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc);
        var old = new FootballStandingSnapshot(season, "old-provider", date.AddDays(-1), "old-table", "old-scope");
        var tied = new FootballStandingSnapshot(Guid.Parse("11111111-1111-1111-1111-111111111111"), season,
            "tied-provider", date, "home", "group-a");
        var latest = new FootballStandingSnapshot(Guid.Parse("22222222-2222-2222-2222-222222222222"), season,
            "latest-provider", date, "away", "group-b");
        var other = new FootballStandingSnapshot(otherSeason, "other-provider", date.AddDays(1), "total", "all");
        var first = Participant(season, "11111111-1111-1111-1111-111111111111", "First");
        var second = Participant(season, "22222222-2222-2222-2222-222222222222", "Second");
        var third = Participant(season, "33333333-3333-3333-3333-333333333333", "Third");
        var rows = new[]
        {
            new FootballStandingRow(latest, third, 2),
            new FootballStandingRow(latest, second, 1),
            new FootballStandingRow(latest, first, 1),
            new FootballStandingRow(old, first, 1),
            new FootballStandingRow(tied, second, 1)
        };
        var request = new StandingsPageRequest(season.Id, 2, 1);
        var query = new EfStandingsReader(context).BuildQuery(request);
        var snapshot = Evaluate(query, new[] { old, other, tied, latest }, rows).Single();
        Assert.Equal(new StandingsSnapshotResponse(latest.Id, "latest-provider", date, "away", "group-b"), snapshot.Snapshot);
        Assert.Equal(new[] { second.TeamId, third.TeamId }, snapshot.Items.Select(x => x.Team.Id));
        Assert.Equal(new[] { 1, 2 }, snapshot.Items.Select(x => x.Position));
        Assert.Equal("Second", snapshot.Items[0].Team.Name);
        var page = EfStandingsReader.CreatePage(snapshot, request);
        Assert.Equal(second.TeamId, Assert.Single(page.Items).Team.Id);
        Assert.True(page.HasNextPage);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, guard.Attempts);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public void SyntheticEmptyLatestSnapshotNeverFallsBackToOlderRows(int page)
    {
        var guard = new RejectConnection();
        using var context = Context(guard);
        var season = Season();
        var date = new DateTime(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc);
        var old = new FootballStandingSnapshot(season, "provider", date.AddDays(-1), "total", "all");
        var latest = new FootballStandingSnapshot(season, "provider", date, "total", "all");
        var participant = Participant(season, "11111111-1111-1111-1111-111111111111", "First");
        var query = new EfStandingsReader(context).BuildQuery(new(season.Id, page));
        var snapshot = Evaluate(query, new[] { old, latest }, new[] { new FootballStandingRow(old, participant, 1) }).Single();
        Assert.Equal(latest.Id, snapshot.Snapshot.Id);
        Assert.Empty(snapshot.Items);
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void SyntheticMissingSeasonOrSnapshotReturnsNoSnapshot()
    {
        var guard = new RejectConnection();
        using var context = Context(guard);
        var season = Season();
        var other = new FootballStandingSnapshot(season, "provider", DateTime.UtcNow, "total", "all");
        var query = new EfStandingsReader(context).BuildQuery(new(Guid.NewGuid()));
        Assert.Empty(Evaluate(query, new[] { other }, Array.Empty<FootballStandingRow>()));
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public async Task PreCancelledReadNeverAttemptsConnection()
    {
        var guard = new RejectConnection();
        await using var context = Context(guard);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EfStandingsReader(context).ReadAsync(new(Guid.NewGuid()), cancellation.Token));
        Assert.Equal(0, guard.Attempts);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task NullRequestNeverAttemptsConnection()
    {
        var guard = new RejectConnection();
        await using var context = Context(guard);
        await Assert.ThrowsAsync<ArgumentNullException>(() => new EfStandingsReader(context).ReadAsync(null!));
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void InfrastructureRegistersOneScopedReaderWithoutResolvingDatabase()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        var registration = Assert.Single(services, x => x.ServiceType == typeof(IStandingsReader));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Equal(typeof(EfStandingsReader), registration.ImplementationType);
    }

    private static EfStandingsReader.SnapshotPage[] Evaluate(IQueryable<EfStandingsReader.SnapshotPage> query,
        IEnumerable<FootballStandingSnapshot> snapshots, IEnumerable<FootballStandingRow> rows)
    {
        var expression = new SyntheticSources(snapshots.AsQueryable(), rows.AsQueryable()).Visit(query.Expression)!;
        return Expression.Lambda<Func<IQueryable<EfStandingsReader.SnapshotPage>>>(expression).Compile()().ToArray();
    }

    private sealed class SyntheticSources(IQueryable<FootballStandingSnapshot> snapshots, IQueryable<FootballStandingRow> rows)
        : ExpressionVisitor
    {
        protected override Expression VisitExtension(Expression node) => node switch
        {
            Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression root when root.EntityType.ClrType == typeof(FootballStandingSnapshot) => snapshots.Expression,
            Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression root when root.EntityType.ClrType == typeof(FootballStandingRow) => rows.Expression,
            _ => base.VisitExtension(node)
        };

        protected override Expression VisitMember(MemberExpression node) =>
            node.Member.Name == nameof(SportStatsDbContext.FootballStandingRows) && node.Type == typeof(DbSet<FootballStandingRow>)
                ? rows.Expression : base.VisitMember(node);
    }

    private static Season Season() => new(new Competition(new Sport("football", "Football"), "TEST", "Test"), "2026");
    private static FootballSeasonTeam Participant(Season season, string id, string name) => new(season, new FootballTeam(Guid.Parse(id), name));
    private static StandingsSnapshotResponse Metadata() => new(Guid.NewGuid(), "provider", DateTime.UtcNow, "total", "all");
    private static SportStatsDbContext Context(RejectConnection guard) => new(
        new DbContextOptionsBuilder<SportStatsDbContext>()
            .UseNpgsql("Host=offline.invalid;Database=synthetic;Username=synthetic;Password=synthetic")
            .AddInterceptors(guard).Options);

    private sealed class RejectConnection : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Attempts++;
            throw new InvalidOperationException("Offline test forbids connections.");
        }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("Offline test forbids connections.");
        }
    }
}
