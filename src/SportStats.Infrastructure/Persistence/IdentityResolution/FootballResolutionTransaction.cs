using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace SportStats.Infrastructure.Persistence.IdentityResolution;

/// <summary>
/// Orchestrates work within a caller-owned EF-enlisted Npgsql transaction at ReadCommitted.
/// Requires an empty, dedicated tracker and exclusive use of the context/connection for the call.
/// Never opens connections, saves changes, or begins/commits/rolls back/disposes the whole transaction.
/// The operation is responsible for saving its changes; successful results remain provisional.
/// The optional lock phase may acquire locks and perform read-only planning, but must not write
/// or track entities. Its failures require caller rollback, even if no resolver writes occurred.
/// The caller must end a failed transaction before creating a new EF transaction lifetime;
/// re-enlisting an unfinished transaction under another EF handle is outside this ownership contract.
/// </summary>
public sealed class FootballResolutionTransaction
{
    private static readonly ConditionalWeakTable<object, TransactionState> States = new();
    private static readonly ConditionalWeakTable<object, ExecutionState> Executions = new();
    private readonly IResolutionTransactionSession session;
    private readonly TimeSpan cleanupTimeout;

    public FootballResolutionTransaction(SportStatsDbContext context, TimeSpan? cleanupTimeout = null)
        : this(new EfResolutionTransactionSession(context), cleanupTimeout ?? TimeSpan.FromSeconds(5)) { }

    internal FootballResolutionTransaction(IResolutionTransactionSession session, TimeSpan cleanupTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (cleanupTimeout < TimeSpan.FromMilliseconds(1) || cleanupTimeout > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
        this.session = session;
        this.cleanupTimeout = cleanupTimeout;
    }

    /// <summary>Sticky for the current EF transaction lifetime, including across shell instances.
    /// Npgsql may recycle its provider transaction object, so provider reference alone is not a
    /// lifetime identifier. A failed transaction must be ended by its owner, never re-enlisted.</summary>
    public bool RequiresCallerRollback
    {
        get
        {
            var identity = session.Inspect().Handle;
            return identity is not null && States.TryGetValue(identity, out var state) && Volatile.Read(ref state.Unusable) != 0;
        }
    }

    /// <summary>
    /// A false isSuccess result is rolled back before it is returned. Exceptions and cancellation
    /// likewise roll back and clear tracking. Cleanup uses a separate bounded cancellation token;
    /// provider cancellation acknowledgement may add transport delay. Cleanup failure quarantines
    /// the transaction and throws FootballResolutionCleanupException instead of claiming rollback.
    /// Request cancellation is honored through the final check before savepoint release. Once
    /// release starts, its bounded completion determines the outcome; late cancellation cannot
    /// turn completed work into a failed result. This boundary does not commit anything.
    /// </summary>
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        Func<TResult, bool> isSuccess,
        Func<CancellationToken, Task>? acquireLocks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(isSuccess);
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = session.Inspect();
        Validate(transaction);
        var state = States.GetValue(transaction.Handle!, _ => new TransactionState());
        var execution = Executions.GetValue(transaction.TransactionIdentity!, _ => new ExecutionState());
        if (Interlocked.CompareExchange(ref execution.Executing, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent or nested resolver operations on one transaction are not supported.");
        try
        {
            if (Volatile.Read(ref state.Unusable) != 0)
                throw new InvalidOperationException("The transaction is unusable for resolver operations; its owner must roll it back.");
            if (session.HasTrackedEntries)
                throw new InvalidOperationException("Resolver operations require an empty, dedicated change tracker.");

            if (acquireLocks is not null)
            {
                try
                {
                    await acquireLocks(cancellationToken);
                    EnsureSameTransaction(transaction, state);
                    if (session.HasTrackedEntries)
                        throw new InvalidOperationException("The lock phase must not track entities.");
                }
                catch (Exception exception)
                {
                    MarkUnusable(state);
                    ClearTrackingOrThrow(state, exception);
                    throw;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var savepoint = "sportstats_resolver_" + Guid.NewGuid().ToString("N");
            try
            {
                await session.CreateSavepointAsync(transaction.Handle!, savepoint, cancellationToken);
            }
            catch (Exception exception)
            {
                // The server may have created it despite a canceled/lost response. Do not guess.
                MarkUnusable(state);
                ClearTrackingOrThrow(state, exception);
                throw;
            }

            TResult result;
            bool successful;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureSameTransaction(transaction, state);
                result = await operation(cancellationToken);
                EnsureSameTransaction(transaction, state);
                cancellationToken.ThrowIfCancellationRequested();
                successful = isSuccess(result);
                if (successful && session.HasPendingChanges)
                    throw new InvalidOperationException("A successful resolver operation must not leave unsaved tracked changes.");
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                await RollbackAndClearAsync(transaction, savepoint, state, exception);
                throw;
            }

            if (!successful)
            {
                await RollbackAndClearAsync(transaction, savepoint, state, null);
                return result;
            }

            try
            {
                session.ClearTracking();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                await RollbackAndClearAsync(transaction, savepoint, state, exception);
                throw;
            }
            // Completion boundary: never cancel release with an already canceled request token.
            try
            {
                using var cleanup = new CancellationTokenSource(cleanupTimeout);
                await session.ReleaseSavepointAsync(transaction.Handle!, savepoint, cleanup.Token);
                cleanup.Token.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
            {
                // Release may have succeeded at the server; recovery cannot be assumed.
                MarkUnusable(state);
                await RollbackAndClearAsync(transaction, savepoint, state, exception);
                throw;
            }
            return result;
        }
        finally { Volatile.Write(ref execution.Executing, 0); }
    }

    private static void Validate(ResolutionTransactionSnapshot transaction)
    {
        if (transaction.Handle is null || transaction.TransactionIdentity is null || !transaction.IsNpgsql)
            throw new InvalidOperationException("An existing EF-enlisted Npgsql transaction is required.");
        if (transaction.ConnectionState != ConnectionState.Open || transaction.ContextConnection is null ||
            !ReferenceEquals(transaction.ContextConnection, transaction.TransactionConnection))
            throw new InvalidOperationException("The transaction must own the context's existing open connection.");
        if (transaction.IsolationLevel != IsolationLevel.ReadCommitted)
            throw new InvalidOperationException("Resolver transactions must use ReadCommitted isolation.");
        if (!transaction.SupportsSavepoints)
            throw new InvalidOperationException("Resolver transactions must support savepoints.");
    }

    private void EnsureSameTransaction(ResolutionTransactionSnapshot original, TransactionState state)
    {
        try
        {
            var current = session.Inspect();
            Validate(current);
            if (!ReferenceEquals(original.TransactionIdentity, current.TransactionIdentity) ||
                !ReferenceEquals(original.Handle, current.Handle) ||
                !ReferenceEquals(original.ContextConnection, current.ContextConnection))
                throw new InvalidOperationException("The caller's transaction or connection changed during resolver execution.");
        }
        catch { MarkUnusable(state); throw; }
    }

    private async Task RollbackAndClearAsync(ResolutionTransactionSnapshot transaction, string savepoint,
        TransactionState state, Exception? operationException)
    {
        Exception? cleanupException = null;
        try
        {
            using var cleanup = new CancellationTokenSource(cleanupTimeout);
            await session.RollbackToSavepointAsync(transaction.Handle!, savepoint, cleanup.Token);
            cleanup.Token.ThrowIfCancellationRequested();
            await session.ReleaseSavepointAsync(transaction.Handle!, savepoint, cleanup.Token);
            cleanup.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception) { cleanupException = exception; }
        try { session.ClearTracking(); }
        catch (Exception exception)
        {
            cleanupException = cleanupException is null ? exception : new AggregateException(cleanupException, exception);
        }
        if (cleanupException is not null)
        {
            MarkUnusable(state);
            throw new FootballResolutionCleanupException(operationException, cleanupException);
        }
    }

    private void ClearTrackingOrThrow(TransactionState state, Exception operationException)
    {
        try { session.ClearTracking(); }
        catch (Exception exception)
        {
            MarkUnusable(state);
            throw new FootballResolutionCleanupException(operationException, exception);
        }
    }

    private static void MarkUnusable(TransactionState state) => Volatile.Write(ref state.Unusable, 1);
    private sealed class TransactionState { public int Unusable; }
    private sealed class ExecutionState { public int Executing; }
}

/// <summary>Rollback/tracking cleanup was not confirmed. The caller must end the transaction.
/// Original exceptions are retained for diagnostics; consumers must sanitize provider details.</summary>
public sealed class FootballResolutionCleanupException : InvalidOperationException
{
    internal FootballResolutionCleanupException(Exception? operationException, Exception cleanupException)
        : base("Resolver cleanup failed; the transaction is unusable and requires caller rollback.", cleanupException)
        => OperationException = operationException;

    public Exception? OperationException { get; }
}

internal sealed record ResolutionTransactionSnapshot(object? Handle, object? TransactionIdentity,
    object? ContextConnection, object? TransactionConnection, ConnectionState ConnectionState,
    bool IsNpgsql, IsolationLevel IsolationLevel, bool SupportsSavepoints);

// Offline test seam. The production adapter derives every property from EF/Npgsql, never caller claims.
internal interface IResolutionTransactionSession
{
    ResolutionTransactionSnapshot Inspect();
    bool HasTrackedEntries { get; }
    bool HasPendingChanges { get; }
    void ClearTracking();
    Task CreateSavepointAsync(object transaction, string name, CancellationToken cancellationToken);
    Task RollbackToSavepointAsync(object transaction, string name, CancellationToken cancellationToken);
    Task ReleaseSavepointAsync(object transaction, string name, CancellationToken cancellationToken);
}

internal sealed class EfResolutionTransactionSession : IResolutionTransactionSession
{
    private readonly SportStatsDbContext context;
    public EfResolutionTransactionSession(SportStatsDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    public ResolutionTransactionSnapshot Inspect()
    {
        var enlisted = context.Database.CurrentTransaction;
        var transaction = enlisted?.GetDbTransaction();
        var connection = context.Database.GetDbConnection();
        return new(enlisted, transaction, connection, transaction?.Connection, connection.State,
            transaction is NpgsqlTransaction, transaction?.IsolationLevel ?? IsolationLevel.Unspecified,
            enlisted?.SupportsSavepoints ?? false);
    }

    public bool HasTrackedEntries => context.ChangeTracker.Entries().Any();
    public bool HasPendingChanges => context.ChangeTracker.HasChanges();
    public void ClearTracking() => context.ChangeTracker.Clear();
    public Task CreateSavepointAsync(object transaction, string name, CancellationToken cancellationToken)
        => ((IDbContextTransaction)transaction).CreateSavepointAsync(name, cancellationToken);
    public Task RollbackToSavepointAsync(object transaction, string name, CancellationToken cancellationToken)
        => ((IDbContextTransaction)transaction).RollbackToSavepointAsync(name, cancellationToken);
    public Task ReleaseSavepointAsync(object transaction, string name, CancellationToken cancellationToken)
        => ((IDbContextTransaction)transaction).ReleaseSavepointAsync(name, cancellationToken);
}
