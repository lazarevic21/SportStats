using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SportStats.Infrastructure;
using SportStats.Infrastructure.Persistence;
using Xunit;

namespace SportStats.UnitTests;

public sealed class DatabaseModelTests
{
    [Fact]
    public void UnknownPersistedMatchStatusesMaterializeSafely()
    {
        using var context = CreateContext();
        var converter = Entity(context.Model, "FootballMatch").FindProperty("Status")!.GetValueConverter()!;
        Assert.Equal(SportStats.Domain.Football.FootballMatchStatus.Unknown, converter.ConvertFromProvider("future-status"));
        Assert.Equal(SportStats.Domain.Football.FootballMatchStatus.Unknown, converter.ConvertFromProvider("2"));
        Assert.Equal(SportStats.Domain.Football.FootballMatchStatus.Finished, converter.ConvertFromProvider("Finished"));
        Assert.Equal("Finished", converter.ConvertToProvider(SportStats.Domain.Football.FootballMatchStatus.Finished));
    }

    private static SportStatsDbContext CreateContext() => new(new DbContextOptionsBuilder<SportStatsDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline_model;Username=offline_model") .Options);

    private static IEntityType Entity(IModel model, string name) => Assert.Single(model.GetEntityTypes(), e => e.ClrType.Name == name);

    [Fact]
    public void PlatformUsesUuidKeysAndScopedUniqueNaturalKeys()
    {
        using var context = CreateContext();
        foreach (var name in new[] { "Sport", "Competition", "Season" })
        {
            var entity = Entity(context.Model, name);
            Assert.Equal("platform", entity.GetSchema());
            Assert.Equal("Id", Assert.Single(entity.FindPrimaryKey()!.Properties).Name);
            Assert.Equal("uuid", entity.FindProperty("Id")!.GetColumnType());
        }
        AssertUnique(Entity(context.Model, "Sport"), "Code");
        AssertUnique(Entity(context.Model, "Competition"), "SportId", "Code");
        AssertUnique(Entity(context.Model, "Season"), "CompetitionId", "Key");
        var season = Entity(context.Model, "Season");
        Assert.Equal("date", season.FindProperty("StartDate")!.GetColumnType());
        Assert.Equal("date", season.FindProperty("EndDate")!.GetColumnType());
    }

    [Fact]
    public void FootballMembershipAndDeletionRulesAreEncodedInModel()
    {
        using var context = CreateContext();
        var model = context.Model;
        var participation = Entity(model, "FootballSeasonTeam");
        Assert.Equal(new[] { "SeasonId", "TeamId" }, participation.FindPrimaryKey()!.Properties.Select(p => p.Name));
        var match = Entity(model, "FootballMatch");
        Assert.Contains(match.GetForeignKeys(), f => f.Properties.Select(p => p.Name).SequenceEqual(new[] { "SeasonId", "HomeTeamId" }));
        Assert.Contains(match.GetForeignKeys(), f => f.Properties.Select(p => p.Name).SequenceEqual(new[] { "SeasonId", "AwayTeamId" }));
        foreach (var fk in match.GetForeignKeys().Where(f => f.PrincipalEntityType.ClrType.Name == "FootballSeasonTeam"))
            Assert.Equal(new[] { "SeasonId", "TeamId" }, fk.PrincipalKey.Properties.Select(p => p.Name));
        Assert.True(match.FindProperty("HomeGoals")!.IsNullable);
        Assert.True(match.FindProperty("AwayGoals")!.IsNullable);
        Assert.Equal("timestamp with time zone", match.FindProperty("KickoffUtc")!.GetColumnType());
        Assert.Contains(match.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "SeasonId", "KickoffUtc" }));
        var designMatch = Entity(context.GetService<IDesignTimeModel>().Model, "FootballMatch");
        Assert.Contains(designMatch.GetCheckConstraints(), c => c.Sql.Contains("\"HomeTeamId\" <> \"AwayTeamId\""));
        Assert.Contains(designMatch.GetCheckConstraints(), c => c.Sql.Contains("\"HomeGoals\" >= 0"));
        Assert.Contains(designMatch.GetCheckConstraints(), c => c.Sql.Contains("\"AwayGoals\" >= 0"));
        foreach (var entity in model.GetEntityTypes())
        foreach (var fk in entity.GetForeignKeys())
        {
            Assert.DoesNotContain(fk.Properties, p => p.IsShadowProperty());
            var snapshotRows = entity.ClrType.Name == "FootballStandingRow" && fk.PrincipalEntityType.ClrType.Name == "FootballStandingSnapshot";
            Assert.Equal(snapshotRows ? DeleteBehavior.Cascade : DeleteBehavior.Restrict, fk.DeleteBehavior);
        }
        foreach (var name in new[] { "FootballTeam", "FootballSeasonTeam", "FootballMatch", "FootballStandingSnapshot", "FootballStandingRow" })
            Assert.Equal("football", Entity(model, name).GetSchema());
    }

    [Fact]
    public void StandingsPreserveSnapshotsWithoutUnsupportedAggregateColumnsOrUniquePositions()
    {
        using var context = CreateContext();
        var snapshot = Entity(context.Model, "FootballStandingSnapshot");
        Assert.Equal("timestamp with time zone", snapshot.FindProperty("RetrievedAtUtc")!.GetColumnType());
        Assert.Contains(snapshot.GetIndexes(), i => i.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { "SeasonId", "ProviderKey", "TableType", "Scope", "RetrievedAtUtc" }));
        var row = Entity(context.Model, "FootballStandingRow");
        foreach (var name in new[] { "Played", "Won", "Drawn", "Lost", "GoalsFor", "GoalsAgainst", "GoalDifference", "Points" })
            Assert.Null(row.FindProperty(name));
        Assert.DoesNotContain(row.GetIndexes(), i => i.IsUnique && i.Properties.Any(p => p.Name == "Position"));
        Assert.DoesNotContain(row.GetKeys(), k => k.Properties.Any(p => p.Name == "Position"));
        Assert.Contains(row.GetForeignKeys(), f => f.PrincipalEntityType.ClrType.Name == "FootballSeasonTeam");
        var snapshotFk = Assert.Single(row.GetForeignKeys(), f => f.PrincipalEntityType.ClrType.Name == "FootballStandingSnapshot");
        Assert.Equal(new[] { "SnapshotId", "SeasonId" }, snapshotFk.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "Id", "SeasonId" }, snapshotFk.PrincipalKey.Properties.Select(p => p.Name));
    }

    [Fact]
    public void ConcreteContextRegistrationIsLazyAndDoesNotOpenAConnection()
    {
        var missing = new ConfigurationBuilder().Build();
        using (var provider = new ServiceCollection().AddInfrastructure(missing).BuildServiceProvider())
        using (var scope = provider.CreateScope())
            Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<SportStatsDbContext>());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SportStats"] = "Host=127.0.0.1;Port=1;Database=offline_model;Username=offline_model"
        }).Build();
        using var validProvider = new ServiceCollection().AddInfrastructure(configuration).BuildServiceProvider();
        using var validScope = validProvider.CreateScope();
        var context = validScope.ServiceProvider.GetRequiredService<SportStatsDbContext>();
        Assert.NotEmpty(context.Model.GetEntityTypes());
        var relationalOptions = Assert.Single(context.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>());
        Assert.Equal("platform", relationalOptions.MigrationsHistoryTableSchema);
        Assert.Equal("__EFMigrationsHistory", relationalOptions.MigrationsHistoryTableName);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Password=synthetic-secret;invalid-setting=synthetic-secret")]
    public void ConcreteContextRejectsInvalidConfigurationWithoutLeakingCredentials(string? connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:SportStats"] = connectionString }).Build();
        using var provider = new ServiceCollection().AddInfrastructure(configuration).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var exception = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<SportStatsDbContext>());
        Assert.DoesNotContain("synthetic-secret", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void ProviderIdentitiesUseSeparateTypedForeignKeysAndNonuniqueScopedLookup()
    {
        using var context = CreateContext();
        foreach (var (name, target) in new[]
        {
            ("CompetitionExternalIdentity", "Competition"), ("SeasonExternalIdentity", "Season"),
            ("FootballTeamExternalIdentity", "FootballTeam"), ("FootballMatchExternalIdentity", "FootballMatch")
        })
        {
            var entity = Entity(context.Model, name);
            Assert.Equal(typeof(Guid), Assert.Single(entity.FindPrimaryKey()!.Properties).ClrType);
            Assert.Equal(typeof(string), entity.FindProperty("ExternalId")!.ClrType);
            Assert.Contains(entity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType.Name == target);
            Assert.Contains(entity.GetIndexes(), index => !index.IsUnique && index.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { "ProviderKey", "IdentityScope", "ExternalId" }));
            Assert.DoesNotContain(entity.GetIndexes(), index => index.IsUnique && index.Properties.Any(p => p.Name == "ExternalId"));
        }
    }

    private static void AssertUnique(IEntityType entity, params string[] properties) =>
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique && index.Properties.Select(p => p.Name).SequenceEqual(properties));

    [Fact]
    public void TypedProviderMappingsRequireExplicitScopeAndKeepExternalIdsAsOpaqueStrings()
    {
        var id = Guid.NewGuid();
        var target = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new CompetitionExternalIdentity(id, "provider", " ", "001-A", target));
        Assert.Throws<ArgumentException>(() => new SeasonExternalIdentity(id, "provider", "season-scope", "", target));
        Assert.Throws<ArgumentException>(() => new FootballTeamExternalIdentity(id, "", "team-scope", "001-A", target));
        Assert.Throws<ArgumentException>(() => new FootballMatchExternalIdentity(id, "provider", "match-scope", "001-A", Guid.Empty));
        var mapping = new CompetitionExternalIdentity(id, "provider", "competition-scope", "001-A", target);
        Assert.Equal("001-A", mapping.ExternalId);
        Assert.Equal(target, mapping.CompetitionId);
        Assert.Equal(id, mapping.Id);
    }
}
