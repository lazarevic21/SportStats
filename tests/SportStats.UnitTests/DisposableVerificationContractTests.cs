using Microsoft.EntityFrameworkCore;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.InitialImport.Verification;
using SportStats.Infrastructure.Persistence;
using Xunit;

namespace SportStats.UnitTests;

public sealed class DisposableVerificationContractTests
{
    private const string Approval = "I APPROVE DISPOSABLE IMPORT COMMITS";
    private const string Marker = "aabbccddeeff00112233445566778899";
    private const string Connection = "Host=127.0.0.1;Port=55432;Database=sportstats_test;Username=postgres;Password=synthetic-test-only";
    private static readonly string[] Arguments = ["--approved-disposable-commit-replay"];

    [Fact]
    public void SeedTracksEveryCompositeMatchMembershipDependencyBeforeSaving()
    {
        using var context = new SportStatsDesignTimeDbContextFactory().CreateDbContext([]);
        context.AddRange(DisposableMigrationVerification.CreateSeedGraph());
        var entries = context.ChangeTracker.Entries().ToArray();
        Assert.Equal(11, entries.Length);
        Assert.All(entries, entry => Assert.Equal(EntityState.Added, entry.State));
        Assert.Single(context.ChangeTracker.Entries<Sport>());
        Assert.Equal(2, context.ChangeTracker.Entries<Competition>().Count());
        Assert.Equal(2, context.ChangeTracker.Entries<Season>().Count());
        Assert.Equal(2, context.ChangeTracker.Entries<FootballTeam>().Count());
        var memberships = context.ChangeTracker.Entries<FootballSeasonTeam>().Select(e => e.Entity).ToArray();
        Assert.Equal(2, memberships.Length);
        var matches = context.ChangeTracker.Entries<FootballMatch>().Select(e => e.Entity).ToArray();
        Assert.Equal(2, matches.Length);
        foreach (var match in matches)
        {
            Assert.Single(memberships, m => m.SeasonId == match.SeasonId && m.TeamId == match.HomeTeamId);
            Assert.Single(memberships, m => m.SeasonId == match.SeasonId && m.TeamId == match.AwayTeamId);
        }
        Assert.Null(context.Database.GetConnectionString());
    }

    [Fact]
    public void ValidConfigurationDisablesPoolingEnlistmentAndSensitiveDiagnostics()
    {
        var settings = DisposableVerificationContract.ValidateConfiguration(Arguments, Approval, Marker,
            Connection + ";Pooling=true;Enlist=true;Include Error Detail=true;Log Parameters=true;Persist Security Info=true");
        Assert.False(settings.Pooling);
        Assert.False(settings.Enlist);
        Assert.False(settings.IncludeErrorDetail);
        Assert.False(settings.LogParameters);
        Assert.False(settings.PersistSecurityInfo);
        Assert.Equal(10, settings.Timeout);
        Assert.Equal(20, settings.CommandTimeout);
        Assert.Equal(5000, settings.CancellationTimeout);
        Assert.Equal("127.0.0.1", settings.Host);
        Assert.Equal("sportstats_test", settings.Database);
        Assert.Equal("postgres", settings.Username);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I APPROVE DISPOSABLE IMPORT COMMITS ")]
    [InlineData("i approve disposable import commits")]
    public void MissingOrInexactApprovalIsRejected(string? approval) =>
        Assert.Throws<InvalidOperationException>(() => DisposableVerificationContract.ValidateConfiguration(Arguments, approval, Marker, Connection));

    [Fact]
    public void ArgumentsRequireOnlyTheExactExplicitOptIn()
    {
        foreach (var args in new[] { Array.Empty<string>(), new[] { "--apply" }, new[] { Arguments[0], "--apply" }, new[] { Arguments[0].ToUpperInvariant() } })
            Assert.Throws<InvalidOperationException>(() => DisposableVerificationContract.ValidateConfiguration(args, Approval, Marker, Connection));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-marker")]
    [InlineData("aabbccdd-eeff-0011-2233-445566778899")]
    public void InvalidIsolationMarkerIsRejected(string? marker) =>
        Assert.Throws<InvalidOperationException>(() => DisposableVerificationContract.ValidateConfiguration(Arguments, Approval, marker, Connection));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a connection string")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=sportstats_test;Username=postgres")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=sportstats_test;Username=postgres;Password=")]
    [InlineData("Host=127.0.0.1;Port=0;Database=sportstats_test;Username=postgres;Password=synthetic-test-only")]
    [InlineData("Host=localhost;Port=55432;Database=sportstats_test;Username=postgres;Password=synthetic-test-only")]
    [InlineData("Host=127.0.0.1,production.example;Port=55432;Database=sportstats_test;Username=postgres;Password=synthetic-test-only")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=sportstats_test;Username=postgres;Password=synthetic-test-only")]
    [InlineData("Host=127.0.0.1;Database=sportstats_test;Username=postgres;Password=synthetic-test-only")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=sportstats_dev;Username=postgres;Password=synthetic-test-only")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=sportstats_test;Username=sportstats_app;Password=synthetic-test-only")]
    [InlineData("Host=127.0.0.1;Port=55432;Database=sportstats_test;Username=postgres;Password=synthetic-test-only;Options=-c search_path=public")]
    public void UnsafeOrMalformedConnectionConfigurationIsRejected(string? connection) =>
        Assert.Throws<InvalidOperationException>(() => DisposableVerificationContract.ValidateConfiguration(Arguments, Approval, Marker, connection));

    private static IdentityIndexState ValidIndex() => new("platform", "competition_external_ids", "UX_competition_external_ids_identity",
        true, true, true, false, false, ["ProviderKey", "IdentityScope", "ExternalId"], 3);

    [Fact]
    public void IndexCertificationRequiresTheExactTableIdentityAndFullUsableIndex()
    {
        var valid = ValidIndex();
        Assert.True(DisposableVerificationContract.ValidIndex("platform", "competition_external_ids", valid));
        var invalid = new[]
        {
            valid with { Schema = "public" }, valid with { Table = "season_external_ids" },
            valid with { Name = "IX_competition_external_ids_identity" }, valid with { Unique = false },
            valid with { Valid = false }, valid with { Ready = false }, valid with { HasPredicate = true },
            valid with { HasExpression = true }, valid with { Columns = ["IdentityScope", "ProviderKey", "ExternalId"] },
            valid with { Columns = ["ProviderKey", "IdentityScope"] },
            valid with { Columns = ["ProviderKey", "IdentityScope", "ExternalId", "CompetitionId"] },
            valid with { Columns = ["ProviderKey", "IdentityScope", "externalid"] },
            valid with { AttributeCount = 4 }, valid with { AttributeCount = 2 }
        };
        foreach (var index in invalid)
            Assert.False(DisposableVerificationContract.ValidIndex("platform", "competition_external_ids", index));
    }

    [Fact]
    public void MigrationHistoryRequiresExactlyTheExpectedOrderedBaselineOrUpgrade()
    {
        var initial = DisposableVerificationContract.InitialMigration;
        var target = DisposableVerificationContract.TargetMigration;
        Assert.True(DisposableVerificationContract.ValidHistory([initial], false));
        Assert.True(DisposableVerificationContract.ValidHistory([initial, target], true));
        foreach (var history in new[] { Array.Empty<string>(), new[] { target }, new[] { initial, initial },
                     new[] { target, initial }, new[] { initial, target, "20990101000000_Unexpected" } })
        {
            Assert.False(DisposableVerificationContract.ValidHistory(history, false));
            Assert.False(DisposableVerificationContract.ValidHistory(history, true));
        }
        Assert.False(DisposableVerificationContract.ValidHistory([initial], true));
        Assert.False(DisposableVerificationContract.ValidHistory([initial, target], false));
    }
}
