using System.Data;
using Npgsql;
using NpgsqlTypes;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using Xunit;

namespace SportStats.UnitTests;

public sealed class PostgreSqlAdvisoryLockAcquirerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AcquisitionUsesCoordinationFirstSignedOrderAndRestoresPriorTimeout()
    {
        var session = new RecordingSession();
        var plan = new FootballAdvisoryLockPlan([5, -2, 5, long.MinValue, 0], 99);
        await PostgreSqlAdvisoryLockAcquirer.AcquireAsync(session, plan, Wait, default);
        Assert.Equal(new long[] { 99, long.MinValue, -2, 0, 5 }, session.Acquired);
        Assert.Equal(new[] { "read-timeout", "timeout:5000ms", "lock:99", $"lock:{long.MinValue}", "lock:-2", "lock:0", "lock:5", "timeout:150ms" }, session.Events);
        Assert.All(session.Tokens, token => Assert.True(token.CanBeCanceled));
    }

    [Fact]
    public async Task EmptyOrFullyCollidingPlansStillAcquireCoordinationExactlyOnce()
    {
        foreach (var input in new long[][] { [], [99, 99] })
        {
            var session = new RecordingSession();
            await PostgreSqlAdvisoryLockAcquirer.AcquireAsync(session, new(input, 99), Wait, default);
            Assert.Equal(new long[] { 99 }, session.Acquired);
        }
    }

    [Fact]
    public async Task AlreadyCancelledCallIssuesNoCommands()
    {
        var session = new RecordingSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PostgreSqlAdvisoryLockAcquirer.AcquireAsync(session, new([], 99), Wait, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(session.Events);
    }

    [Fact]
    public async Task CallerCancellationDuringAcquisitionStopsAndDoesNotAttemptSqlCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new RecordingSession
        {
            OnAcquire = (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        };
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PostgreSqlAdvisoryLockAcquirer.AcquireAsync(session, new([1, 2], 99), Wait, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(new long[] { 99 }, session.Acquired);
        Assert.DoesNotContain("timeout:150ms", session.Events);
    }

    [Fact]
    public async Task TotalDeadlineBoundsWaitingForCoordination()
    {
        var session = new RecordingSession { OnAcquire = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token) };
        await Assert.ThrowsAsync<TimeoutException>(() => PostgreSqlAdvisoryLockAcquirer.AcquireAsync(
            session, new([1, 2], 99), TimeSpan.FromMilliseconds(25), default));
        Assert.DoesNotContain("timeout:150ms", session.Events);
    }

    [Fact]
    public async Task SameDeadlineAlsoBoundsWaitingAfterCoordination()
    {
        var session = new RecordingSession
        {
            OnAcquire = (key, token) => key == 99 ? Task.CompletedTask : Task.Delay(Timeout.InfiniteTimeSpan, token)
        };
        await Assert.ThrowsAsync<TimeoutException>(() => PostgreSqlAdvisoryLockAcquirer.AcquireAsync(
            session, new([1, 2], 99), TimeSpan.FromMilliseconds(50), default));
        Assert.DoesNotContain(2L, session.Acquired);
        Assert.DoesNotContain("timeout:150ms", session.Events);
    }

    [Fact]
    public async Task DeadlineIncludesTimeoutRestoration()
    {
        var session = new RecordingSession
        {
            OnSetTimeout = (value, token) => value == "150ms" ? Task.Delay(Timeout.InfiniteTimeSpan, token) : Task.CompletedTask
        };
        await Assert.ThrowsAsync<TimeoutException>(() => PostgreSqlAdvisoryLockAcquirer.AcquireAsync(
            session, new([], 99), TimeSpan.FromMilliseconds(50), default));
    }

    [Fact]
    public async Task ServerLockTimeoutBecomesTimeoutExceptionWithoutFurtherSql()
    {
        var failure = new PostgresException("synthetic lock wait timeout", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable);
        var session = new RecordingSession { OnAcquire = (_, _) => Task.FromException(failure) };
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            PostgreSqlAdvisoryLockAcquirer.AcquireAsync(session, new([1], 99), Wait, default));
        Assert.Same(failure, exception.InnerException);
        Assert.Equal(new long[] { 99 }, session.Acquired);
        Assert.DoesNotContain("timeout:150ms", session.Events);
    }

    [Fact]
    public async Task OtherFailuresPropagateAndDoNotAttemptCleanupOnAbortedTransaction()
    {
        var failure = new InvalidOperationException("synthetic failure");
        var session = new RecordingSession { OnAcquire = (_, _) => Task.FromException(failure) };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PostgreSqlAdvisoryLockAcquirer.AcquireAsync(session, new([1], 99), Wait, default)));
        Assert.DoesNotContain("timeout:150ms", session.Events);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(2147483648)]
    public async Task UnboundedOrOutOfRangeWaitIsRejectedBeforeCommands(double milliseconds)
    {
        var session = new RecordingSession();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PostgreSqlAdvisoryLockAcquirer.AcquireAsync(
            session, new([], 99), TimeSpan.FromMilliseconds(milliseconds), default));
        Assert.Empty(session.Events);
    }

    [Fact]
    public async Task FractionalMillisecondsRoundUpToPositiveServerTimeout()
    {
        var session = new RecordingSession();
        await PostgreSqlAdvisoryLockAcquirer.AcquireAsync(session, new([], 99), TimeSpan.FromMilliseconds(5000.5), default);
        Assert.Contains("timeout:5001ms", session.Events);
    }

    [Fact]
    public void LockAndTimeoutCommandsUseTypedParametersWithoutOpeningConnection()
    {
        using var connection = new NpgsqlConnection();
        using var command = PostgreSqlAdvisoryLockAcquirer.CreateLockCommand(connection, null, long.MinValue, 7);
        Assert.Equal("SELECT pg_catalog.pg_advisory_xact_lock($1)", command.CommandText);
        Assert.Equal(NpgsqlDbType.Bigint, Assert.Single(command.Parameters.Cast<NpgsqlParameter>()).NpgsqlDbType);
        Assert.Equal(long.MinValue, command.Parameters[0].Value);
        Assert.Equal(7, command.CommandTimeout);
        Assert.Same(connection, command.Connection);
        using var timeout = PostgreSqlAdvisoryLockAcquirer.CreateTimeoutCommand(connection, null, "50ms'; arbitrary SQL", 7);
        Assert.Equal("SELECT pg_catalog.set_config('lock_timeout', $1, true)", timeout.CommandText);
        Assert.Equal(NpgsqlDbType.Text, timeout.Parameters[0].NpgsqlDbType);
        Assert.Equal("50ms'; arbitrary SQL", timeout.Parameters[0].Value);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task PublicAcquirerRequiresExplicitTransaction()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new PostgreSqlAdvisoryLockAcquirer().AcquireAsync(null!, new([]), Wait));
    }

    private sealed class RecordingSession : IAdvisoryLockSession
    {
        public List<string> Events { get; } = [];
        public List<long> Acquired { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Func<long, CancellationToken, Task>? OnAcquire { get; init; }
        public Func<string, CancellationToken, Task>? OnSetTimeout { get; init; }

        public Task<string> GetLockTimeoutAsync(CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("read-timeout");
            return Task.FromResult("150ms");
        }

        public Task SetLockTimeoutAsync(string timeout, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("timeout:" + timeout);
            return OnSetTimeout?.Invoke(timeout, cancellationToken) ?? Task.CompletedTask;
        }

        public Task AcquireLockAsync(long key, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("lock:" + key);
            Acquired.Add(key);
            return OnAcquire?.Invoke(key, cancellationToken) ?? Task.CompletedTask;
        }
    }
}
