using Npgsql;
using SportStats.InitialImport.Verification;
using Xunit;

namespace SportStats.UnitTests;

public sealed class DisposableRaceTests
{
    private const string Secret = "Password=private; sensitive SQL";
    private static PostgresException Error(string code, string constraint = "expected") =>
        new(Secret, "ERROR", "ERROR", code, detail: Secret, constraintName: constraint);

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task CleanupRollsBackOnlyIncompleteTransactions(bool completed, int expectedCalls)
    {
        var calls = 0;
        await DisposableMigrationVerification.RollbackActiveAsync(completed, () => { calls++; return Task.CompletedTask; });
        Assert.Equal(expectedCalls, calls);
    }

    [Fact]
    public async Task CleanupFailuresAreNotSuppressed()
    {
        var failure = new InvalidOperationException(Secret);
        Assert.Same(failure, await Record.ExceptionAsync(() =>
            DisposableMigrationVerification.RollbackActiveAsync(false, () => Task.FromException(failure))));
    }

    [Fact]
    public void OnlyDirectUniqueViolationOnExpectedIndexPasses()
    {
        DisposableMigrationVerification.ValidateRaceOutcome(true, null, false, Error("23505"), "expected");
        Check(true, true, null, RaceOutcome.BothSucceeded);
        Check(false, false, Error("23505"), RaceOutcome.BothFailed);
        Check(false, true, null, RaceOutcome.UnexpectedWinner);
        Check(true, false, new InvalidOperationException(Secret, Error("23505")), RaceOutcome.ExactlyOneSucceededUnexpectedException);
        Check(true, false, new OperationCanceledException(Secret), RaceOutcome.ExactlyOneSucceededUnexpectedException);
        Check(true, false, Error("57014"), RaceOutcome.ExactlyOneSucceededUnexpectedSqlState);
        Check(true, false, Error("23505", Secret), RaceOutcome.ExactlyOneSucceededUnexpectedConstraint);
    }

    private static void Check(bool first, bool second, Exception? error, RaceOutcome expected)
    {
        var failure = Assert.Throws<DisposableRaceException>(() =>
            DisposableMigrationVerification.ValidateRaceOutcome(first, null, second, error, "expected"));
        Assert.Equal(expected, failure.Outcome);
        Assert.DoesNotContain(Secret, DisposableFailureDiagnostics.Describe(failure));
    }

    [Theory]
    [InlineData((int)RaceOutcome.RowCountZero)]
    [InlineData((int)RaceOutcome.RowCountMultiple)]
    public void RowCountDiagnosticsContainOnlyFixedOutcome(int value)
    {
        var outcome = (RaceOutcome)value;
        Assert.Equal($"race={outcome}; database-error=none",
            DisposableFailureDiagnostics.Describe(new DisposableRaceException(outcome)));
    }
}
