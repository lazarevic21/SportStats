using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.InitialImport;
using SportStats.Domain.Football;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.InitialImport;
using Xunit;

namespace SportStats.UnitTests;

public sealed class InitialImportStoreBoundaryTests
{
    private static IdentityResolutionBatch Batch()
    {
        var competition = new NormalizedCompetition(new("football-data.org", "competition:PL", "2021"), "PL", "Premier League");
        var season = new NormalizedSeason(new("football-data.org", "competition:PL/season:2026", "2502"), 2026,
            "2026/27", new(2026, 8, 21), new(2027, 5, 30));
        var home = new NormalizedTeam(new("football-data.org", "competition:PL", "57"), "Home", null, null);
        var away = new NormalizedTeam(new("football-data.org", "competition:PL", "65"), "Away", null, null);
        var match = new NormalizedMatch(new("football-data.org", "competition:PL/season:2026", "560542"), season,
            home.Identity, away.Identity, new(2026, 8, 21, 19, 0, 0, DateTimeKind.Utc), FootballMatchStatus.Scheduled,
            "TIMED", 1, new(ScoreFieldPresence.Null, null), new(ScoreFieldPresence.Null, null));
        return new InitialImportBundle(competition, season, [home, away], [match]).ToBatch(Guid.NewGuid());
    }

    [Fact]
    public async Task ApplyReleaseGateRejectsBeforeOpeningAnyConnection()
    {
        using var connection = new NpgsqlConnection();
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql(connection).Options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new EfInitialImportStore(context).ApplyAsync(Batch()));
        Assert.Contains("disabled", error.Message);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Null(context.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("scope")]
    [InlineData("externalId")]
    [InlineData("participant")]
    [InlineData("season")]
    [InlineData("status")]
    public async Task InvalidDirectInputIsRejectedWithoutDatabaseAccess(string mutation)
    {
        var batch = Batch();
        batch = mutation switch
        {
            "provider" => batch with { Competitions = [batch.Competitions[0] with { Competition = batch.Competitions[0].Competition with { Identity = batch.Competitions[0].Identity with { ProviderKey = "untrusted" } } }] },
            "scope" => batch with { Teams = [batch.Teams[0] with { Team = batch.Teams[0].Team with { Identity = batch.Teams[0].Identity with { IdentityScope = "competition:PD" } } }, batch.Teams[1]] },
            "externalId" => batch with { Matches = [batch.Matches[0] with { Match = batch.Matches[0].Match with { Identity = batch.Matches[0].Identity with { ExternalId = "0560542" } } }] },
            "participant" => batch with { Matches = [batch.Matches[0] with { Match = batch.Matches[0].Match with { AwayTeam = batch.Teams[1].Identity with { ExternalId = "999" } } }] },
            "season" => batch with { Matches = [batch.Matches[0] with { Match = batch.Matches[0].Match with { Season = batch.Matches[0].Match.Season with { Key = "other" } } }] },
            _ => batch with { Matches = [batch.Matches[0] with { Match = batch.Matches[0].Match with { Status = FootballMatchStatus.Unknown } }] }
        };
        using var connection = new NpgsqlConnection();
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql(connection).Options);
        var result = await new EfInitialImportStore(context).PreviewAsync(batch);
        Assert.False(result.Success);
        Assert.False(result.Applied);
        Assert.NotEmpty(result.Conflicts);
        Assert.All(result.Counts, x => Assert.Equal(0, x.Created));
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ValidPreviewRejectsProductionTargetBeforeConnecting()
    {
        using var connection = new NpgsqlConnection("Host=localhost;Database=sportstats_dev;Username=sportstats_app;Password=synthetic-secret");
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql(connection).Options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new EfInitialImportStore(context).PreviewAsync(Batch()));
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(context.ChangeTracker.Entries());
    }
}
