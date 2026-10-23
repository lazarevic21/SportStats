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

public sealed class CompetitionReaderTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(2, false, 2)]
    [InlineData(3, true, 2)]
    public void LookaheadTrimsOnlyExtraRowAndPreservesOrder(int count, bool hasNextPage, int expectedCount)
    {
        var rows = Enumerable.Range(0, count).Select(i => new CompetitionResponse(
            Guid.NewGuid(), $"league-{i}", $"League {i}", Guid.NewGuid(), "sport", "Sport")).ToList();
        var expected = rows.Take(expectedCount).ToArray();

        var result = EfCompetitionReader.CreatePage(rows, new(4, 2));

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
        var query = new EfCompetitionReader(context).BuildQuery(new(3, 10));

        var sql = query.ToQueryString();

        Assert.Contains("FROM platform.competitions AS c", sql);
        Assert.Contains("INNER JOIN platform.sports AS s", sql);
        Assert.Contains("ORDER BY s.\"Code\", c.\"Code\", c.\"Id\"", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
        Assert.Matches(@"=\s*'?11'?", sql);
        Assert.Matches(@"=\s*'?20'?", sql);
        Assert.DoesNotContain("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AsNoTracking", query.Expression.ToString());
        Assert.Empty(context.ChangeTracker.Entries());
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
            new EfCompetitionReader(context).ReadAsync(new(), cancellation.Token));

        Assert.Equal(0, guard.Attempts);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task NullRequestNeverAttemptsConnection()
    {
        var guard = new RejectConnection();
        await using var context = Context(guard);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new EfCompetitionReader(context).ReadAsync(null!));
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void InfrastructureRegistersOneScopedReaderWithoutResolvingDatabase()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        var registration = Assert.Single(services, x => x.ServiceType == typeof(ICompetitionReader));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Equal(typeof(EfCompetitionReader), registration.ImplementationType);
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
