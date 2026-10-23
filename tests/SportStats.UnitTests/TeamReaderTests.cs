using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SportStats.Application.Football.Teams;
using SportStats.Infrastructure;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.Teams;
using Xunit;

namespace SportStats.UnitTests;

public sealed class TeamReaderTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(2, false, 2)]
    [InlineData(3, true, 2)]
    public void LookaheadTrimsOnlyExtraRowAndPreservesOrder(int count, bool hasNextPage, int expectedCount)
    {
        var rows = Enumerable.Range(0, count).Select(i => new TeamResponse(
            Guid.NewGuid(), $"Team {i}", null, null)).ToList();
        var expected = rows.Take(expectedCount).ToArray();

        var result = EfTeamReader.CreatePage(rows, new(4, 2));

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
        var query = new EfTeamReader(context).BuildQuery(new(3, 10));

        var sql = query.ToQueryString();

        var alias = System.Text.RegularExpressions.Regex.Match(sql, @"FROM football\.teams AS (\w+)").Groups[1].Value;
        Assert.NotEmpty(alias);
        Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"ORDER BY {alias}.\"Name\", {alias}.\"Id\"", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
        Assert.Matches(@"=\s*'?11'?", sql);
        Assert.Matches(@"=\s*'?20'?", sql);
        Assert.DoesNotContain("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{alias}.\"ShortName\"", sql);
        Assert.Contains($"{alias}.\"CrestUrl\"", sql);
        Assert.Contains("AsNoTracking", query.Expression.ToString());
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void ScalarProjectionPreservesNullableIdentityFieldsWithoutTrackingEntities()
    {
        var guard = new RejectConnection();
        using var context = Context(guard);
        var teams = new[]
        {
            new SportStats.Domain.Football.FootballTeam("Same name"),
            new SportStats.Domain.Football.FootballTeam("Same name", "Short", "https://example.invalid/crest.png")
        };
        // Execute only the scalar selector over synthetic entities, never the database query.
        var query = new EfTeamReader(context).BuildQuery(new());
        var select = Assert.IsType<System.Linq.Expressions.MethodCallExpression>(query.Expression, exactMatch: false);
        var selector = (System.Linq.Expressions.Expression<Func<SportStats.Domain.Football.FootballTeam, TeamResponse>>)
            ((System.Linq.Expressions.UnaryExpression)select.Arguments[1]).Operand;
        var rows = teams.Select(selector.Compile()).ToArray();
        Assert.Equal(new TeamResponse(teams[0].Id, "Same name", null, null), rows[0]);
        Assert.Equal(new TeamResponse(teams[1].Id, "Same name", "Short", "https://example.invalid/crest.png"), rows[1]);
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
            new EfTeamReader(context).ReadAsync(new(), cancellation.Token));

        Assert.Equal(0, guard.Attempts);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task NullRequestNeverAttemptsConnection()
    {
        var guard = new RejectConnection();
        await using var context = Context(guard);
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new EfTeamReader(context).ReadAsync(null!));
        Assert.Equal(0, guard.Attempts);
    }

    [Fact]
    public void InfrastructureRegistersOneScopedReaderWithoutResolvingDatabase()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        var registration = Assert.Single(services, x => x.ServiceType == typeof(ITeamReader));
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.Equal(typeof(EfTeamReader), registration.ImplementationType);
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
