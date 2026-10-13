using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SportStats.Infrastructure.Persistence;
using System.Data.Common;
using Npgsql;
using Xunit;

namespace SportStats.IntegrationTests;

/// <summary>Offline tests: no connection is opened for valid configurations.</summary>
public sealed class PostgreSqlTestDatabaseGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Password=synthetic-secret;invalid-setting=synthetic-secret")]
    [InlineData("Host=127.0.0.1;Database=other_database;Username=sportstats_app")]
    [InlineData("Host=127.0.0.1;Database=SPORTSTATS_TEST;Username=sportstats_app")]
    [InlineData("Host=127.0.0.1;Database=sportstats_test;Username=other_role")]
    [InlineData("Host=127.0.0.1;Database=sportstats_test")]
    [InlineData("Database=sportstats_test;Username=sportstats_app")]
    public async Task InvalidConfigurationFailsBeforeConnecting(string? value)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlTestDatabaseFixture.OpenAsync(value));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    [Fact]
    public void ValidConfigurationDisablesSensitiveDiagnosticsAndImplicitEnlistment()
    {
        var settings = PostgreSqlTestDatabaseFixture.ValidateConfiguration(
            "Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app;Password=synthetic-secret;Include Error Detail=true;Log Parameters=true;Persist Security Info=true;Pooling=true;Enlist=true");
        Assert.False(settings.IncludeErrorDetail);
        Assert.False(settings.LogParameters);
        Assert.False(settings.PersistSecurityInfo);
        Assert.False(settings.Pooling);
        Assert.False(settings.Enlist);
        using var connection = new NpgsqlConnection(settings.ConnectionString);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void ClosedSessionCannotBeReopenedByGuard()
    {
        using var connection = new NpgsqlConnection(
            "Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        Assert.Throws<InvalidOperationException>(() =>
            PostgreSqlTestDatabaseFixture.RequireUnchangedOpenSession(connection, connection.ConnectionString));
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData("other_database", "sportstats_app")]
    [InlineData("sportstats_test", "other_role")]
    public void RetargetedSessionFailsWithoutOpening(string database, string user)
    {
        using var connection = new NpgsqlConnection(
            "Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        var original = connection.ConnectionString;
        var replacement = new NpgsqlConnectionStringBuilder(original) { Database = database, Username = user };
        connection.ConnectionString = replacement.ConnectionString;
        Assert.Throws<InvalidOperationException>(() =>
            PostgreSqlTestDatabaseFixture.RequireUnchangedOpenSession(connection, original));
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void FixtureExposesNoContextConnectionOrOptionsFactory()
    {
        var type = typeof(PostgreSqlTestDatabaseFixture);
        Assert.Empty(type.GetConstructors());
        Assert.Empty(type.GetProperties(BindingFlags.Public | BindingFlags.Instance));
        var factory = type.GetMethod("CreateOptions", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(factory);
        Assert.True(factory.IsPrivate);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            Assert.False(typeof(DbContext).IsAssignableFrom(method.ReturnType));
            Assert.False(typeof(DbConnection).IsAssignableFrom(method.ReturnType));
            Assert.False(typeof(IQueryable).IsAssignableFrom(method.ReturnType));
        }
    }

    [Fact]
    public async Task PrivateFixtureWithClosedSessionRefusesReadsAndWrites()
    {
        // Reflection is used only to exercise an otherwise inaccessible invalid state offline.
        var connection = new NpgsqlConnection(
            "Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        var constructor = typeof(PostgreSqlTestDatabaseFixture).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(NpgsqlConnection)], null)!;
        await using var fixture = (PostgreSqlTestDatabaseFixture)constructor.Invoke([connection]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SaveChangesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ReadAsync<SportStats.Domain.Platform.Sport>(x => true));
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task PrivateOptionsDisableDiagnosticsAndBlockEfReopening()
    {
        using var connection = new NpgsqlConnection(
            "Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        var factory = typeof(PostgreSqlTestDatabaseFixture).GetMethod("CreateOptions",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var options = (DbContextOptions<SportStatsDbContext>)factory.Invoke(null, [connection])!;
        var diagnostics = options.FindExtension<CoreOptionsExtension>()!;
        Assert.False(diagnostics.IsSensitiveDataLoggingEnabled);
        Assert.False(diagnostics.DetailedErrorsEnabled);
        await using var context = new SportStatsDbContext(options);
        Assert.Throws<InvalidOperationException>(() => context.Database.OpenConnection());
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.OpenConnectionAsync());
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Theory]
    [InlineData("other_database", "sportstats_app")]
    [InlineData("SPORTSTATS_TEST", "sportstats_app")]
    [InlineData("sportstats_test", "other_role")]
    [InlineData("sportstats_test", "SPORTSTATS_APP")]
    public void ServerIdentityMismatchFailsClosed(string database, string user) =>
        Assert.Throws<InvalidOperationException>(() =>
            PostgreSqlTestDatabaseFixture.VerifyServerIdentity(database, user));

    [Fact]
    public void ExactServerIdentityIsAccepted() =>
        PostgreSqlTestDatabaseFixture.VerifyServerIdentity("sportstats_test", "sportstats_app");
}
