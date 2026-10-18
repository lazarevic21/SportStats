using Npgsql;
using SportStats.InitialImport;
using Xunit;

namespace SportStats.UnitTests;

public sealed class InitialImportConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Password=synthetic-secret;Database=sportstats_dev;Username=sportstats_app;Host=localhost")]
    [InlineData("Password=synthetic-secret;Database=sportstats_test;Username=sportstats_migrator;Host=localhost")]
    [InlineData("Password=synthetic-secret;Database=SPORTSTATS_TEST;Username=sportstats_app;Host=localhost")]
    [InlineData("invalid synthetic-secret connection string")]
    public void InvalidTargetFailsWithoutLeakingConfiguration(string? value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ImportDatabaseConfiguration.Validate(value));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public void GuardedTargetDisablesSessionReuseEnlistmentAndDiagnosticSecrets()
    {
        var safe = new NpgsqlConnectionStringBuilder(ImportDatabaseConfiguration.Validate(
            "Host=localhost;Database=sportstats_test;Username=sportstats_app;Password=synthetic-secret;Pooling=true;Enlist=true;Include Error Detail=true;Log Parameters=true;Persist Security Info=true"));
        Assert.False(safe.Pooling);
        Assert.False(safe.Enlist);
        Assert.False(safe.IncludeErrorDetail);
        Assert.False(safe.LogParameters);
        Assert.False(safe.PersistSecurityInfo);
        Assert.Equal("sportstats_test", safe.Database);
        Assert.Equal("sportstats_app", safe.Username);
    }
}
