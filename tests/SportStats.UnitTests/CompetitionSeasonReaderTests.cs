using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SportStats.Application.Competitions;
using SportStats.Infrastructure;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.Competitions;
using Xunit;

namespace SportStats.UnitTests;

public sealed class CompetitionSeasonReaderTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(2, false, 2)]
    [InlineData(3, true, 2)]
    public void LookaheadTrimsOnlyExtraRowAndPreservesOrder(int count, bool hasNextPage, int expectedCount)
    {
        var rows = Enumerable.Range(0, count).Select(i => new SeasonResponse(
            Guid.NewGuid(), Guid.NewGuid(), $"season-{i}", null, null)).ToList();
        var expected = rows.Take(expectedCount).ToArray();

        var result = EfCompetitionSeasonReader.CreatePage(rows, new(Guid.NewGuid(), 4, 2));

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
        var competitionId = Guid.NewGuid();
        var query = new EfCompetitionSeasonReader(context).BuildQuery(new(competitionId, 3, 10));

        var sql = query.ToQueryString();

        Assert.Contains("FROM platform.seasons AS s", sql);
        Assert.Contains("WHERE s.\"CompetitionId\" =", sql);
        Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY s.\"Key\", s.\"Id\"", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
        Assert.Matches(@"=\s*'?11'?", sql);
        Assert.Matches(@"=\s*'?20'?", sql);
        Assert.DoesNotContain("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(competitionId.ToString(), sql);
        Assert.Contains("s.\"StartDate\"", sql);
        Assert.Contains("s.\"EndDate\"", sql);
        Assert.Contains("AsNoTracking", query.Expression.ToString());
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void ScalarProjectionPreservesNullableDatesWithoutTrackingEntities()
    {
        using var context = Context(new RejectConnection());
        var competitionId = Guid.NewGuid();
        var competition = new SportStats.Domain.Platform.Competition(competitionId,
            new SportStats.Domain.Platform.Sport("sport", "Sport"), "league", "League");
        var seasons = new[]
        {
            new SportStats.Domain.Platform.Season(competition, "alpha"),
            new SportStats.Domain.Platform.Season(competition, "beta", new DateOnly(2025, 8, 1), new DateOnly(2026, 5, 1))
        };
        // Execute only the scalar selector over synthetic entities, never the database query.
        var query = new EfCompetitionSeasonReader(context).BuildQuery(new(competitionId));
        var select = Assert.IsType<System.Linq.Expressions.MethodCallExpression>(query.Expression, exactMatch: false);
        var selector = (System.Linq.Expressions.Expression<Func<SportStats.Domain.Platform.Season, SeasonResponse>>)
            ((System.Linq.Expressions.UnaryExpression)select.Arguments[1]).Operand;
        var rows = seasons.Select(selector.Compile()).ToArray();
        Assert.Equal(new SeasonResponse(seasons[0].Id, competitionId, "alpha", null, null), rows[0]);
        Assert.Equal(new DateOnly(2025, 8, 1), rows[1].StartDate);
        Assert.Equal(new DateOnly(2026, 5, 1), rows[1].EndDate);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task PreCancelledReadNeverAttemptsConnection()
    {
        var guard = new RejectConnection();
        await using var context = Context(guard);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EfCompetitionSeasonReader(context).ReadAsync(new(Guid.NewGuid()), cancellation.Token));

        Assert.Equal(0, guard.Attempts);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task NullRequestNeverAttemptsConnection()
    {
        var guard = new RejectConnection();
        await using var context = Context(guard);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new EfCompetitionSeasonReader(context).ReadAsync(null!));
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void InfrastructureRegistersOneScopedReaderWithoutResolvingDatabase()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        var registration = Assert.Single(services, x => x.ServiceType == typeof(ICompetitionSeasonReader));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Equal(typeof(EfCompetitionSeasonReader), registration.ImplementationType);
    }

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
