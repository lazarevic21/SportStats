using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.IdentityResolution;
using Xunit;

namespace SportStats.UnitTests;

public sealed class FootballResolutionTransactionTests
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private static FootballResolutionTransaction Shell(RecordingSession session, TimeSpan? timeout = null) => new(session, timeout ?? CleanupTimeout);
    private static Task<int> Success(CancellationToken _) => Task.FromResult(42);
    private static bool IsSuccess(int result) => result == 42;

    [Fact]
    public async Task SuccessAcquiresLocksBeforeSavepointAndReturnsProvisionalResultWithCleanTracker()
    {
        using var session = new RecordingSession();
        var shell = Shell(session);
        var result = await shell.ExecuteAsync(token =>
        {
            Assert.False(token.IsCancellationRequested);
            session.Context.Attach(new Sport("football", "Football"));
            session.Events.Add("operation");
            return Task.FromResult(42);
        }, IsSuccess, _ => { session.Events.Add("locks"); return Task.CompletedTask; });
        Assert.Equal(42, result);
        Assert.Equal(new[] { "locks", "create", "operation", "clear", "release" }, session.Events);
        Assert.Single(session.CreatedNames);
        Assert.Equal(session.CreatedNames, session.ReleasedNames);
        Assert.Empty(session.Context.ChangeTracker.Entries());
        Assert.False(shell.RequiresCallerRollback);
        Assert.Equal(ConnectionState.Closed, session.OfflineConnection.State);
    }

    [Fact]
    public async Task ReturnedContractFailureRollsBackBeforeReturningAndAllowsSequentialReuse()
    {
        using var session = new RecordingSession();
        var shell = Shell(session);
        var failure = new IdentityResolutionBatchResult.Failed(new(IdentityResolutionFailureCode.MissingReference, "Season", "Missing season."));
        var result = await shell.ExecuteAsync(_ =>
        {
            session.Context.Add(new Sport("football", "Football"));
            return Task.FromResult<IdentityResolutionBatchResult>(failure);
        }, value => value is IdentityResolutionBatchResult.Resolved);
        Assert.Same(failure, result);
        Assert.Equal(new[] { "create", "rollback", "release", "clear" }, session.Events);
        Assert.Empty(session.Context.ChangeTracker.Entries());
        Assert.False(shell.RequiresCallerRollback);
        Assert.Equal(42, await shell.ExecuteAsync(Success, IsSuccess));
        Assert.Equal(2, session.CreatedNames.Distinct().Count());
    }

    [Fact]
    public async Task ExceptionPreservesOriginalAndClearsAddedModifiedDeletedAndReattachedEntities()
    {
        using var session = new RecordingSession();
        var exception = new InvalidOperationException("Synthetic operation failure.");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(session).ExecuteAsync<int>(_ =>
        {
            session.Context.Add(new Sport("added", "Added"));
            var modified = new Sport("modified", "Modified");
            session.Context.Attach(modified);
            session.Context.Entry(modified).State = EntityState.Modified;
            var deleted = new Sport("deleted", "Deleted");
            session.Context.Attach(deleted);
            session.Context.Remove(deleted);
            var reattached = new Sport("reattached", "Reattached");
            session.Context.Attach(reattached);
            session.Context.Entry(reattached).State = EntityState.Detached;
            session.Context.Add(reattached);
            return Task.FromException<int>(exception);
        }, IsSuccess));
        Assert.Same(exception, actual);
        Assert.Empty(session.Context.ChangeTracker.Entries());
        Assert.Equal(session.CreatedNames, session.RolledBackNames);
    }

    [Fact]
    public async Task ClaimedSuccessWithUnsavedChangesIsRolledBackAndRejected()
    {
        using var session = new RecordingSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(session).ExecuteAsync(_ =>
        {
            session.Context.Add(new Sport("football", "Football"));
            return Task.FromResult(42);
        }, IsSuccess));
        Assert.Single(session.RolledBackNames);
        Assert.Empty(session.Context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task SuccessPredicateFailureAlsoRollsBack()
    {
        using var session = new RecordingSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(session).ExecuteAsync(Success,
            _ => throw new InvalidOperationException("Synthetic predicate failure.")));
        Assert.Single(session.RolledBackNames);
    }

    [Fact]
    public async Task AlreadyCancelledRequestPerformsNoTransactionOrTrackingWork()
    {
        using var session = new RecordingSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Shell(session).ExecuteAsync(Success, IsSuccess, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(session.Events);
        Assert.Equal(0, session.InspectionCount);
    }

    [Fact]
    public async Task CancellationAfterOperationReturnsStillRollsBackUsingIndependentBoundedToken()
    {
        using var session = new RecordingSession();
        using var cancellation = new CancellationTokenSource();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Shell(session).ExecuteAsync(_ =>
        {
            session.Context.Add(new Sport("football", "Football"));
            cancellation.Cancel();
            return Task.FromResult(42);
        }, IsSuccess, cancellationToken: cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(session.RollbackTokens.Single().CanBeCanceled);
        Assert.False(session.RollbackTokens.Single().IsCancellationRequested);
        Assert.NotEqual(cancellation.Token, session.RollbackTokens.Single());
        Assert.Empty(session.Context.ChangeTracker.Entries());
        Assert.False(Shell(session).RequiresCallerRollback);
    }

    [Fact]
    public async Task CancellationDuringSavepointReleaseDoesNotTurnCompletedWorkIntoFailure()
    {
        using var session = new RecordingSession();
        using var cancellation = new CancellationTokenSource();
        session.OnRelease = (_, _, token) =>
        {
            cancellation.Cancel();
            Assert.False(token.IsCancellationRequested);
            Assert.NotEqual(cancellation.Token, token);
            return Task.CompletedTask;
        };
        Assert.Equal(42, await Shell(session).ExecuteAsync(Success, IsSuccess, cancellationToken: cancellation.Token));
        Assert.Empty(session.RolledBackNames);
    }

    [Fact]
    public async Task RollbackFailureQuarantinesTransactionAcrossNewShellInstancesAndRetainsBothErrors()
    {
        using var session = new RecordingSession();
        var operationError = new InvalidOperationException("Synthetic operation failure.");
        var rollbackError = new InvalidOperationException("Synthetic rollback failure.");
        session.OnRollback = (_, _, _) => Task.FromException(rollbackError);
        var exception = await Assert.ThrowsAsync<FootballResolutionCleanupException>(() => Shell(session).ExecuteAsync<int>(_ =>
        {
            session.Context.Add(new Sport("football", "Football"));
            return Task.FromException<int>(operationError);
        }, IsSuccess));
        Assert.Same(operationError, exception.OperationException);
        Assert.Same(rollbackError, exception.InnerException);
        Assert.Empty(session.Context.ChangeTracker.Entries());
        Assert.Empty(session.ReleasedNames);
        Assert.True(Shell(session).RequiresCallerRollback);
        var eventCount = session.Events.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(session).ExecuteAsync(Success, IsSuccess));
        Assert.Equal(eventCount, session.Events.Count);

        using var otherContext = new RecordingSession { Snapshot = session.Snapshot };
        Assert.True(Shell(otherContext).RequiresCallerRollback);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(otherContext).ExecuteAsync(Success, IsSuccess));
    }

    [Fact]
    public async Task RollbackDeadlineIsBoundedAndQuarantinesTransaction()
    {
        using var session = new RecordingSession();
        session.OnRollback = (_, _, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var shell = Shell(session, TimeSpan.FromMilliseconds(25));
        var exception = await Assert.ThrowsAsync<FootballResolutionCleanupException>(() => shell.ExecuteAsync(
            _ => Task.FromResult(0), IsSuccess));
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.True(shell.RequiresCallerRollback);
        Assert.True(session.RollbackTokens.Single().IsCancellationRequested);
        Assert.Empty(session.Context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task CleanupDeadlineAlsoCoversReleaseAfterRollback()
    {
        using var session = new RecordingSession();
        session.OnRelease = (_, _, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var shell = Shell(session, TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAsync<FootballResolutionCleanupException>(() => shell.ExecuteAsync(_ => Task.FromResult(0), IsSuccess));
        Assert.True(shell.RequiresCallerRollback);
        Assert.Single(session.RolledBackNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockPhaseFailureOrCancellationQuarantinesWithoutIssuingSavepointSql(bool cancel)
    {
        using var session = new RecordingSession();
        using var cancellation = new CancellationTokenSource();
        var shell = Shell(session);
        Task Locks(CancellationToken token)
        {
            session.Context.Attach(new Sport("football", "Football"));
            if (cancel) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new TimeoutException("Synthetic lock failure.");
        }
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shell.ExecuteAsync(Success, IsSuccess, Locks, cancellation.Token));
        else
            await Assert.ThrowsAsync<TimeoutException>(() => shell.ExecuteAsync(Success, IsSuccess, Locks));
        Assert.True(shell.RequiresCallerRollback);
        Assert.Empty(session.CreatedNames);
        Assert.Empty(session.RolledBackNames);
        Assert.Empty(session.Context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task LockPhaseTrackingIsRejectedBeforeOperationAndQuarantined()
    {
        using var session = new RecordingSession();
        var shell = Shell(session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.ExecuteAsync(Success, IsSuccess, _ =>
        {
            session.Context.Attach(new Sport("football", "Football"));
            return Task.CompletedTask;
        }));
        Assert.True(shell.RequiresCallerRollback);
        Assert.Empty(session.CreatedNames);
        Assert.Empty(session.Context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task SavepointCreationFailureIsConservativelyQuarantined()
    {
        using var session = new RecordingSession();
        session.OnCreate = (_, _, _) => Task.FromException(new InvalidOperationException("Synthetic create failure."));
        var shell = Shell(session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.ExecuteAsync(Success, IsSuccess));
        Assert.True(shell.RequiresCallerRollback);
        Assert.Empty(session.RolledBackNames);
        Assert.Empty(session.ReleasedNames);
    }

    [Fact]
    public async Task SuccessReleaseFailureAttemptsRecoveryButRemainsQuarantined()
    {
        using var session = new RecordingSession();
        var exception = new InvalidOperationException("Synthetic release failure.");
        var releases = 0;
        session.OnRelease = (_, _, _) => ++releases == 1 ? Task.FromException(exception) : Task.CompletedTask;
        var shell = Shell(session);
        Assert.Same(exception, await Assert.ThrowsAsync<InvalidOperationException>(() => shell.ExecuteAsync(Success, IsSuccess)));
        Assert.True(shell.RequiresCallerRollback);
        Assert.Single(session.RolledBackNames);
    }

    [Fact]
    public async Task SuccessfulReleaseDeadlineFailureRequiresCallerRollback()
    {
        using var session = new RecordingSession();
        session.OnRelease = (_, _, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var shell = Shell(session, TimeSpan.FromMilliseconds(25));
        await Assert.ThrowsAsync<FootballResolutionCleanupException>(() => shell.ExecuteAsync(Success, IsSuccess));
        Assert.True(shell.RequiresCallerRollback);
    }

    [Fact]
    public async Task TrackerCleanupFailureIsReportedAndQuarantined()
    {
        using var session = new RecordingSession();
        session.OnClear = () => throw new InvalidOperationException("Synthetic tracker failure.");
        var shell = Shell(session);
        var exception = await Assert.ThrowsAsync<FootballResolutionCleanupException>(() => shell.ExecuteAsync(_ => Task.FromResult(0), IsSuccess));
        Assert.Null(exception.OperationException);
        Assert.True(shell.RequiresCallerRollback);
    }

    [Fact]
    public async Task ChangingTransactionDuringOperationCannotRedirectCleanupToReplacement()
    {
        using var session = new RecordingSession();
        var original = session.Snapshot;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(session).ExecuteAsync(_ =>
        {
            session.Snapshot = original with { Handle = new object(), TransactionIdentity = new object() };
            return Task.FromResult(42);
        }, IsSuccess));
        Assert.All(session.CommandHandles, handle => Assert.Same(original.Handle, handle));
        session.Snapshot = original;
        Assert.True(Shell(session).RequiresCallerRollback);
    }

    [Fact]
    public async Task ChangedOrClosedConnectionDuringOperationIsQuarantined()
    {
        using var session = new RecordingSession();
        var shell = Shell(session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.ExecuteAsync(_ =>
        {
            session.Snapshot = session.Snapshot with { ConnectionState = ConnectionState.Closed };
            return Task.FromResult(42);
        }, IsSuccess));
        Assert.True(shell.RequiresCallerRollback);
    }

    [Fact]
    public async Task GenuineNewTransactionDoesNotInheritPreviousQuarantine()
    {
        using var session = new RecordingSession();
        var shell = Shell(session);
        await Assert.ThrowsAsync<TimeoutException>(() => shell.ExecuteAsync(Success, IsSuccess,
            _ => Task.FromException(new TimeoutException("Synthetic lock failure."))));
        Assert.True(shell.RequiresCallerRollback);
        session.Snapshot = session.Snapshot with { Handle = new object(), TransactionIdentity = new object() };
        Assert.False(shell.RequiresCallerRollback);
        Assert.Equal(42, await shell.ExecuteAsync(Success, IsSuccess));
    }

    [Fact]
    public async Task GenuineNewEfLifetimeCanReuseProviderObjectWithoutInheritingQuarantine()
    {
        using var session = new RecordingSession();
        var shell = Shell(session);
        await Assert.ThrowsAsync<TimeoutException>(() => shell.ExecuteAsync(Success, IsSuccess,
            _ => Task.FromException(new TimeoutException("Synthetic lock failure."))));
        Assert.True(shell.RequiresCallerRollback);
        // Models the caller ending the old transaction and EF beginning a new lifetime, while
        // Npgsql recycles its provider object. This is not re-enlistment of an unfinished transaction.
        session.Snapshot = session.Snapshot with { Handle = new object() };
        Assert.False(shell.RequiresCallerRollback);
        Assert.Equal(42, await shell.ExecuteAsync(Success, IsSuccess));
    }

    [Fact]
    public async Task ConcurrentOrNestedCallsAreRejectedWithoutQuarantiningActiveOperation()
    {
        using var session = new RecordingSession();
        var shell = Shell(session);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = shell.ExecuteAsync(async _ => { entered.SetResult(); await finish.Task; return 42; }, IsSuccess);
        await entered.Task;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(session).ExecuteAsync(Success, IsSuccess));
            using var secondWrapper = new RecordingSession
            { Snapshot = session.Snapshot with { Handle = new object() } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(secondWrapper).ExecuteAsync(Success, IsSuccess));
            Assert.False(shell.RequiresCallerRollback);
        }
        finally { finish.SetResult(); }
        Assert.Equal(42, await active);
        Assert.Single(session.CreatedNames);
    }

    [Theory]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task PreexistingTrackingIsRejectedAndPreserved(EntityState state)
    {
        using var session = new RecordingSession();
        var sport = new Sport("football", "Football");
        session.Context.Entry(sport).State = state;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(session).ExecuteAsync(Success, IsSuccess));
        Assert.Equal(state, session.Context.Entry(sport).State);
        Assert.Empty(session.Events);
        Assert.False(Shell(session).RequiresCallerRollback);
    }

    [Theory]
    [InlineData(IsolationLevel.Unspecified)]
    [InlineData(IsolationLevel.ReadUncommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    [InlineData(IsolationLevel.Snapshot)]
    public async Task UnsupportedIsolationLevelsFailBeforeCommands(IsolationLevel isolation)
    {
        using var session = new RecordingSession();
        session.Snapshot = session.Snapshot with { IsolationLevel = isolation };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(session).ExecuteAsync(Success, IsSuccess));
        Assert.Empty(session.Events);
    }

    [Theory]
    [InlineData("missing-handle")]
    [InlineData("missing-transaction")]
    [InlineData("wrong-provider")]
    [InlineData("closed")]
    [InlineData("different-connection")]
    [InlineData("missing-connection")]
    [InlineData("unsupported-savepoints")]
    public async Task InvalidTransactionMetadataFailsBeforeCommands(string invalid)
    {
        using var session = new RecordingSession();
        session.Snapshot = invalid switch
        {
            "missing-handle" => session.Snapshot with { Handle = null },
            "missing-transaction" => session.Snapshot with { TransactionIdentity = null },
            "wrong-provider" => session.Snapshot with { IsNpgsql = false },
            "closed" => session.Snapshot with { ConnectionState = ConnectionState.Closed },
            "different-connection" => session.Snapshot with { TransactionConnection = new object() },
            "missing-connection" => session.Snapshot with { ContextConnection = null },
            _ => session.Snapshot with { SupportsSavepoints = false }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Shell(session).ExecuteAsync(Success, IsSuccess));
        Assert.Empty(session.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(2147483648)]
    public void InvalidCleanupBudgetIsRejected(double milliseconds)
    {
        using var session = new RecordingSession();
        Assert.Throws<ArgumentOutOfRangeException>(() => Shell(session, TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task ProductionAdapterRejectsMissingTransactionWithoutOpeningConnectionOrClearingTracking()
    {
        using var connection = new NpgsqlConnection();
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql(connection).Options);
        var sport = new Sport("football", "Football");
        context.Add(sport);
        var shell = new FootballResolutionTransaction(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.ExecuteAsync(Success, IsSuccess));
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(EntityState.Added, context.Entry(sport).State);
    }

    private sealed class RecordingSession : IResolutionTransactionSession, IDisposable
    {
        public NpgsqlConnection OfflineConnection { get; } = new();
        public SportStatsDbContext Context { get; }
        public ResolutionTransactionSnapshot Snapshot { get; set; }
        public int InspectionCount { get; private set; }
        public List<string> Events { get; } = [];
        public List<string> CreatedNames { get; } = [];
        public List<string> RolledBackNames { get; } = [];
        public List<string> ReleasedNames { get; } = [];
        public List<object> CommandHandles { get; } = [];
        public List<CancellationToken> RollbackTokens { get; } = [];
        public Func<object, string, CancellationToken, Task>? OnCreate { get; set; }
        public Func<object, string, CancellationToken, Task>? OnRollback { get; set; }
        public Func<object, string, CancellationToken, Task>? OnRelease { get; set; }
        public Action? OnClear { get; set; }

        public RecordingSession()
        {
            Context = new(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql(OfflineConnection).Options);
            var connection = new object();
            Snapshot = new(new object(), new object(), connection, connection, ConnectionState.Open,
                true, IsolationLevel.ReadCommitted, true);
        }

        public ResolutionTransactionSnapshot Inspect() { InspectionCount++; return Snapshot; }
        public bool HasTrackedEntries => Context.ChangeTracker.Entries().Any();
        public bool HasPendingChanges => Context.ChangeTracker.HasChanges();
        public void ClearTracking() { Events.Add("clear"); OnClear?.Invoke(); Context.ChangeTracker.Clear(); }

        public Task CreateSavepointAsync(object transaction, string name, CancellationToken cancellationToken)
        {
            Events.Add("create"); CreatedNames.Add(name); CommandHandles.Add(transaction);
            cancellationToken.ThrowIfCancellationRequested();
            return OnCreate?.Invoke(transaction, name, cancellationToken) ?? Task.CompletedTask;
        }

        public Task RollbackToSavepointAsync(object transaction, string name, CancellationToken cancellationToken)
        {
            Events.Add("rollback"); RolledBackNames.Add(name); CommandHandles.Add(transaction); RollbackTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return OnRollback?.Invoke(transaction, name, cancellationToken) ?? Task.CompletedTask;
        }

        public Task ReleaseSavepointAsync(object transaction, string name, CancellationToken cancellationToken)
        {
            Events.Add("release"); ReleasedNames.Add(name); CommandHandles.Add(transaction);
            cancellationToken.ThrowIfCancellationRequested();
            return OnRelease?.Invoke(transaction, name, cancellationToken) ?? Task.CompletedTask;
        }

        public void Dispose() { Context.Dispose(); OfflineConnection.Dispose(); }
    }
}
