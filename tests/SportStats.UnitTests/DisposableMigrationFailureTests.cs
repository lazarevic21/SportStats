using Microsoft.EntityFrameworkCore;
using Npgsql;
using SportStats.Infrastructure.Persistence;
using SportStats.InitialImport.Verification;
using Xunit;

namespace SportStats.UnitTests;

public sealed class DisposableMigrationFailureTests
{
    private const string Secret = "Password=DO-NOT-PRINT; private SQL and identifiers";
    private static PostgresException Error(string code, string message = Secret) => new(message, "ERROR", "ERROR", code);

    [Fact]
    public async Task InstalledProviderWrapsLockTimeoutAndExpectationAcceptsItWithoutRetryOrConnection()
    {
        // Runs the actual installed provider strategy, with a synthetic database error.
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>()
            .UseNpgsql().Options);
        var strategy = context.Database.CreateExecutionStrategy();
        Assert.False(strategy.RetriesOnFailure);
        var calls = 0;
        var postgres = Error("55P03");
        Task Execute() => strategy.ExecuteAsync(() => { calls++; return Task.FromException(postgres); });
        var wrapper = await Assert.ThrowsAsync<InvalidOperationException>(Execute);
        Assert.Same(postgres, wrapper.InnerException);
        Assert.Equal("sqlstate=55P03; shape=ef-transient-wrapper", DisposableFailureDiagnostics.Describe(wrapper));
        await DisposableMigrationVerification.ExpectFailureAsync(Execute, "55P03");
        Assert.Equal(2, calls);
        Assert.Null(context.Database.GetConnectionString());
    }

    [Theory]
    [InlineData("55P03")]
    [InlineData("42P07")]
    [InlineData("P0001")]
    public async Task DirectExpectedErrorsRemainAccepted(string code)
    {
        var error = Error(code, code == "P0001" ? "External identity uniqueness audit failed; no mappings were changed." : Secret);
        await DisposableMigrationVerification.ExpectFailureAsync(() => Task.FromException(error), code);
    }

    [Fact]
    public async Task SuccessWrongErrorCancellationAndUnrelatedWrappersFailClosed()
    {
        await Assert.ThrowsAsync<DisposableAssertionException>(() =>
            DisposableMigrationVerification.ExpectFailureAsync(() => Task.CompletedTask, "55P03"));
        Exception[] rejected = [Error("57014"), new InvalidOperationException(Secret),
            new InvalidOperationException(Secret, Error("P0001")),
            new Exception(Secret, Error("55P03")), new OperationCanceledException(Secret)];
        foreach (var error in rejected)
        {
            var observed = await Record.ExceptionAsync(() => DisposableMigrationVerification.ExpectFailureAsync(
                () => Task.FromException(error), "55P03"));
            Assert.Same(error, observed);
        }
        await Assert.ThrowsAsync<DisposableAssertionException>(() =>
            DisposableMigrationVerification.ExpectFailureAsync(() => Task.FromException(Error("P0001")), "P0001"));
    }

    [Fact]
    public void DiagnosticsEmitOnlyCodesShapesAndAssertionLocation()
    {
        Assert.Equal("sqlstate=other; shape=direct", DisposableFailureDiagnostics.Describe(Error(Secret)));
        Assert.Equal("sqlstate=55P03; shape=direct", DisposableFailureDiagnostics.Describe(Error("55P03")));
        Assert.Equal("assertion=DisposableMigrationVerification:123", DisposableFailureDiagnostics.Describe(new DisposableAssertionException(123)));
        Assert.Equal("database-error=none-or-unrecognized-wrapper", DisposableFailureDiagnostics.Describe(new Exception(Secret)));
    }
}
