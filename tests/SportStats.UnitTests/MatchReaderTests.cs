using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SportStats.Application.Football.Matches;
using SportStats.Infrastructure;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.Matches;
using Xunit;

namespace SportStats.UnitTests;

public sealed class MatchReaderTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(2, false, 2)]
    [InlineData(3, true, 2)]
    public void LookaheadTrimsOnlyExtraRowAndPreservesOrder(int count, bool hasNextPage, int expectedCount)
    {
        var rows = Enumerable.Range(0, count).Select(_ => SampleResponse()).ToList();
        var expected = rows.Take(expectedCount).ToArray();

        var result = EfMatchReader.CreatePage(rows, new(4, 2));

        Assert.Equal(expected, result.Items);
        Assert.Equal(hasNextPage, result.HasNextPage);
        Assert.Equal(4, result.Page);
        Assert.Equal(2, result.PageSize);
    }

    [Fact]
    public void QueryTranslatesToOneBoundedOrderedProjectionWithoutConnecting()
    {
        var guard = new RejectConnection();
        using var context = Context(guard);
        var query = new EfMatchReader(context).BuildQuery(new(3, 10));

        var sql = query.ToQueryString();

        Assert.Contains("football.matches", sql);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, @"\bJOIN\b").Count);
        Assert.DoesNotContain("platform.seasons", sql);
        Assert.DoesNotContain("season_teams", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("KickoffUtc", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
        Assert.Matches(@"=\s*'?11'?", sql);
        Assert.Matches(@"=\s*'?20'?", sql);
        Assert.DoesNotContain("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HomeGoals", sql);
        Assert.Contains("AwayGoals", sql);
        Assert.Contains("AsNoTracking", query.Expression.ToString());
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, guard.Attempts);
    }
    [Fact]
    public void SyntheticProjectionPreservesTeamsStatusAndNullableScores()
    {
        var guard = new RejectConnection();
        using var context = Context(guard);
        var season = new SportStats.Domain.Platform.Season(new SportStats.Domain.Platform.Competition(
            new SportStats.Domain.Platform.Sport("football", "Football"), "TEST", "Test"), "2026");
        var home = new SportStats.Domain.Football.FootballSeasonTeam(season, new("Home"));
        var away = new SportStats.Domain.Football.FootballSeasonTeam(season, new("Away"));
        var matches = new[]
        {
            new SportStats.Domain.Football.FootballMatch(season, home, away),
            new SportStats.Domain.Football.FootballMatch(season, away, home,
                new DateTime(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc),
                SportStats.Domain.Football.FootballMatchStatus.Finished, 4, new(0, 2))
        };
        var query = new EfMatchReader(context).BuildQuery(new());
        var select = Assert.IsType<System.Linq.Expressions.MethodCallExpression>(query.Expression, exactMatch: false);
        var selector = (System.Linq.Expressions.Expression<Func<SportStats.Domain.Football.FootballMatch, MatchResponse>>)
            ((System.Linq.Expressions.UnaryExpression)select.Arguments[1]).Operand;
        var rows = matches.Select(selector.Compile()).ToArray();
        Assert.Equal(matches[0].Id, rows[0].Id);
        Assert.Equal(season.Id, rows[0].SeasonId);
        Assert.Equal(new MatchTeamResponse(home.TeamId, "Home"), rows[0].HomeTeam);
        Assert.Equal(new MatchTeamResponse(away.TeamId, "Away"), rows[0].AwayTeam);
        Assert.Null(rows[0].KickoffUtc);
        Assert.Null(rows[0].HomeGoals);
        Assert.Null(rows[0].AwayGoals);
        Assert.Equal("Unknown", rows[0].Status);
        Assert.Equal("Finished", rows[1].Status);
        Assert.Equal(0, rows[1].HomeGoals);
        Assert.Equal(2, rows[1].AwayGoals);
        Assert.Equal(4, rows[1].Matchday);
        Assert.Equal(matches[1].KickoffUtc, rows[1].KickoffUtc);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, guard.Attempts);
    }
    [Fact]
    public void SyntheticQueryCombinesFiltersAndHonorsBothTeamRolesAndDateBoundaries()
    {
        var guard = new RejectConnection();
        using var context = Context(guard);
        var sport = new SportStats.Domain.Platform.Sport("football", "Football");
        var competition = new SportStats.Domain.Platform.Competition(sport, "TEST", "Test");
        var season = new SportStats.Domain.Platform.Season(competition, "2026");
        var otherSeason = new SportStats.Domain.Platform.Season(competition, "2027");
        var home = new SportStats.Domain.Football.FootballSeasonTeam(season, new("Home"));
        var away = new SportStats.Domain.Football.FootballSeasonTeam(season, new("Away"));
        var third = new SportStats.Domain.Football.FootballSeasonTeam(season, new("Third"));
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var matches = new[]
        {
            new SportStats.Domain.Football.FootballMatch(season, home, away, start),
            new SportStats.Domain.Football.FootballMatch(season, away, home, start.AddDays(1)),
            new SportStats.Domain.Football.FootballMatch(season, home, away, start.AddDays(-1)),
            new SportStats.Domain.Football.FootballMatch(season, home, away, start.AddDays(2)),
            new SportStats.Domain.Football.FootballMatch(season, home, away),
            new SportStats.Domain.Football.FootballMatch(season, away, third, start),
            new SportStats.Domain.Football.FootballMatch(otherSeason,
                new(otherSeason, home.Team), new(otherSeason, away.Team), start)
        };
        var request = new MatchPageRequest(pageSize: 1, seasonId: season.Id, teamId: home.TeamId,
            from: new(2026, 1, 1), to: new(2026, 1, 3));
        var query = new EfMatchReader(context).BuildQuery(request);
        var expression = new SyntheticSource(matches.AsQueryable()).Visit(query.Expression)!;
        var rows = System.Linq.Expressions.Expression.Lambda<Func<IQueryable<MatchResponse>>>(expression).Compile()().ToList();
        Assert.Equal(new[] { matches[0].Id, matches[1].Id }, rows.Select(x => x.Id));
        var page = EfMatchReader.CreatePage(rows, request);
        Assert.Single(page.Items);
        Assert.True(page.HasNextPage);
        var sql = query.ToQueryString();
        Assert.Contains("WHERE", sql);
        Assert.Contains(" OR ", sql);
        Assert.Contains(">=", sql);
        Assert.Contains(" < ", sql);
        Assert.DoesNotContain("::text", sql); // Status stays a converted enum scalar, then ToString runs in the final projection.
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, guard.Attempts);
    }

    private sealed class SyntheticSource(IQueryable<SportStats.Domain.Football.FootballMatch> source)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override System.Linq.Expressions.Expression VisitExtension(System.Linq.Expressions.Expression node) =>
            node is Microsoft.EntityFrameworkCore.Query.EntityQueryRootExpression ? source.Expression : base.VisitExtension(node);
    }

    [Fact]
    public void SyntheticQueryOrdersNullDatesLastUsesIdTieBreakerAndSkipsEarlierPage()
    {
        var guard = new RejectConnection();
        using var context = Context(guard);
        var season = new SportStats.Domain.Platform.Season(new SportStats.Domain.Platform.Competition(
            new SportStats.Domain.Platform.Sport("football", "Football"), "TEST", "Test"), "2026");
        var home = new SportStats.Domain.Football.FootballSeasonTeam(season, new("Home"));
        var away = new SportStats.Domain.Football.FootballSeasonTeam(season, new("Away"));
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = new SportStats.Domain.Football.FootballMatch(Guid.Parse("11111111-1111-1111-1111-111111111111"), season, home, away, date);
        var second = new SportStats.Domain.Football.FootballMatch(Guid.Parse("22222222-2222-2222-2222-222222222222"), season, home, away, date);
        var third = new SportStats.Domain.Football.FootballMatch(season, home, away, date.AddDays(1));
        var unknown = new SportStats.Domain.Football.FootballMatch(season, home, away);
        var source = new[] { unknown, third, second, first }.AsQueryable();
        var query = new EfMatchReader(context).BuildQuery(new(page: 2, pageSize: 1));
        var expression = new SyntheticSource(source).Visit(query.Expression)!;
        var rows = System.Linq.Expressions.Expression.Lambda<Func<IQueryable<MatchResponse>>>(expression).Compile()().ToArray();
        Assert.Equal(new[] { second.Id, third.Id }, rows.Select(x => x.Id));
        var allQuery = new EfMatchReader(context).BuildQuery(new());
        var allExpression = new SyntheticSource(source).Visit(allQuery.Expression)!;
        var all = System.Linq.Expressions.Expression.Lambda<Func<IQueryable<MatchResponse>>>(allExpression).Compile()().ToArray();
        Assert.Equal(new[] { first.Id, second.Id, third.Id, unknown.Id }, all.Select(x => x.Id));
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
            new EfMatchReader(context).ReadAsync(new(), cancellation.Token));

        Assert.Equal(0, guard.Attempts);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task NullRequestNeverAttemptsConnection()
    {
        var guard = new RejectConnection();
        await using var context = Context(guard);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new EfMatchReader(context).ReadAsync(null!));
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void InfrastructureRegistersOneScopedReaderWithoutResolvingDatabase()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        var registration = Assert.Single(services, x => x.ServiceType == typeof(IMatchReader));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Equal(typeof(EfMatchReader), registration.ImplementationType);
    }

    private static MatchResponse SampleResponse() => new(Guid.NewGuid(), Guid.NewGuid(),
        new(Guid.NewGuid(), "Home"), new(Guid.NewGuid(), "Away"), null, "Unknown", null, null, null);

    private static SportStatsDbContext Context(RejectConnection guard) => new(
        new DbContextOptionsBuilder<SportStatsDbContext>()
            .UseNpgsql("Host=offline.invalid;Database=synthetic;Username=synthetic;Password=synthetic")
            .AddInterceptors(guard).Options);

    private sealed class RejectConnection : DbConnectionInterceptor
    {
        public int Attempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result)
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
