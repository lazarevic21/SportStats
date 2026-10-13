using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SportStats.Infrastructure;
using Xunit;

namespace SportStats.UnitTests;

public sealed class DatabaseConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Password=synthetic-secret;invalid-setting=synthetic-secret")]
    [InlineData("Host=localhost;Username=sportstats_app;Password=synthetic-secret")]
    [InlineData("Database=sportstats_test;Username=sportstats_app;Password=synthetic-secret")]
    [InlineData("Host=localhost;Database=sportstats_test;Password=synthetic-secret")]
    [InlineData("Host=localhost;Database=sportstats_test;Username=sportstats_app;Port=synthetic-secret")]
    [InlineData("Host=localhost;Database=sportstats_test;Username=sportstats_app;Port=999999999999999999999;Password=synthetic-secret")]
    [InlineData("Host=localhost;Database=sportstats_test;Username=sportstats_app;Include Error Detail=synthetic-secret")]
    public void InvalidConfigurationFailsOnlyWhenContextIsResolvedWithoutExposingInput(string? connectionString)
    {
        using var provider = CreateProvider(connectionString);
        using var scope = provider.CreateScope();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<TestDbContext>());

        Assert.Contains("ConnectionStrings:SportStats", exception.Message);
        Assert.DoesNotContain("synthetic-secret", exception.ToString());
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void ValidConfigurationDisablesSensitiveDiagnosticsWithoutConnecting()
    {
        const string connectionString = "Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app;Password=synthetic-secret;Include Error Detail=true;Log Parameters=true;Persist Security Info=true";
        using var provider = CreateProvider(connectionString);
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        var parsed = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        var options = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>();

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
        Assert.Equal("sportstats_app", parsed.Username);
        Assert.Equal("synthetic-secret", parsed.Password);
        Assert.False(parsed.IncludeErrorDetail);
        Assert.False(parsed.LogParameters);
        Assert.False(parsed.PersistSecurityInfo);
        Assert.NotNull(options);
        Assert.False(options.IsSensitiveDataLoggingEnabled);
        Assert.False(options.DetailedErrorsEnabled);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    private static ServiceProvider CreateProvider(string? connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:SportStats"] = connectionString }).Build();
        return new ServiceCollection().AddSportStatsDbContext<TestDbContext>(configuration).BuildServiceProvider();
    }

    public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options);
}
