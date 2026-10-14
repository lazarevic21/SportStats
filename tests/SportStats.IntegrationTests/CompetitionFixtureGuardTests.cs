using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace SportStats.IntegrationTests;

/// <summary>Offline safety checks; these tests never open a PostgreSQL connection.</summary>
public sealed class CompetitionFixtureGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("01")]
    public void LiveOptInFailsClosed(string? value) =>
        Assert.Throws<InvalidOperationException>(() => PostgreSqlTestDatabaseFixture.ValidateLiveOptIn(value));

    [Fact]
    public void ExactLiveOptInIsAccepted() => PostgreSqlTestDatabaseFixture.ValidateLiveOptIn("1");

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, true, true, true, true)]
    public void ElevatedRoleFailsClosed(bool superuser, bool createDb, bool createRole, bool bypassRls, bool elevatedMembership) =>
        Assert.Throws<InvalidOperationException>(() => PostgreSqlTestDatabaseFixture.VerifyRestrictedRole(
            superuser, createDb, createRole, bypassRls, elevatedMembership));

    [Fact]
    public void RestrictedRoleIsAccepted() =>
        PostgreSqlTestDatabaseFixture.VerifyRestrictedRole(false, false, false, false, false);

    [Fact]
    public async Task CommitIsAlwaysRejectedSynchronouslyAndAsynchronously()
    {
        var type = typeof(PostgreSqlTestDatabaseFixture).GetNestedType("NeverCommitInterceptor", BindingFlags.NonPublic)!;
        Assert.NotNull(type);
        var interceptor = (DbTransactionInterceptor)Activator.CreateInstance(type, nonPublic: true)!;
        Assert.Throws<InvalidOperationException>(() => interceptor.TransactionCommitting(null!, null!, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await interceptor.TransactionCommittingAsync(null!, null!, default));
    }

    [Theory]
    [InlineData(CompetitionScenario.CreationReplay)]
    [InlineData(CompetitionScenario.ExistingReuseConflict)]
    [InlineData(CompetitionScenario.FailureRollback)]
    public async Task ClosedFixtureCannotStartAnyScenario(CompetitionScenario scenario)
    {
        var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app");
        var constructor = typeof(PostgreSqlTestDatabaseFixture).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(NpgsqlConnection)], null)!;
        await using var fixture = (PostgreSqlTestDatabaseFixture)constructor.Invoke([connection]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunCompetitionScenarioAsync(scenario));
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void PublicApiDoesNotExposeDatabaseCapabilitiesIncludingAsyncResults()
    {
        foreach (var method in typeof(PostgreSqlTestDatabaseFixture).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            AssertSafe(method.ReturnType);
        foreach (var property in typeof(PostgreSqlTestDatabaseFixture).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            AssertSafe(property.PropertyType);
    }

    private static void AssertSafe(Type type)
    {
        Assert.False(typeof(DbContext).IsAssignableFrom(type));
        Assert.False(typeof(DbConnection).IsAssignableFrom(type));
        Assert.False(typeof(DbTransaction).IsAssignableFrom(type));
        Assert.False(typeof(IQueryable).IsAssignableFrom(type));
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments()) AssertSafe(argument);
    }
}
