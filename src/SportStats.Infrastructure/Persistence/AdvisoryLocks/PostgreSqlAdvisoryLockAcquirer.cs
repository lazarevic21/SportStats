using System.Data;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace SportStats.Infrastructure.Persistence.AdvisoryLocks;

/// <summary>
/// Acquires transaction-scoped exclusive locks on an existing, open PostgreSQL transaction.
/// Never opens connections or begins, commits, rolls back, or disposes the caller's transaction.
/// maxWait bounds the entire acquisition, including coordination, with server lock_timeout
/// bounding each lock wait as well. Driver cancellation acknowledgement may add transport delay.
/// On success the prior lock_timeout is restored. On any failure the caller must roll back:
/// PostgreSQL may have aborted the transaction, and acquired locks remain until transaction end.
/// Do not use the connection concurrently or acquire other protected locks before this helper.
/// </summary>
public sealed class PostgreSqlAdvisoryLockAcquirer
{
    public Task AcquireAsync(NpgsqlTransaction transaction, FootballAdvisoryLockPlan plan,
        TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(plan);
        ValidateWait(maxWait);
        cancellationToken.ThrowIfCancellationRequested();
        var connection = transaction.Connection;
        if (connection is null || connection.State != ConnectionState.Open)
            throw new InvalidOperationException("An active transaction on an open PostgreSQL connection is required.");
        return AcquireAsync(new NpgsqlLockSession(connection, transaction, maxWait), plan, maxWait, cancellationToken);
    }

    internal static async Task AcquireAsync(IAdvisoryLockSession session, FootballAdvisoryLockPlan plan,
        TimeSpan maxWait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await AcquireDiscoveredAsync(session, plan.CoordinationKey, _ => Task.FromResult(plan), maxWait, cancellationToken);
    }

    // Discovery runs under coordination and the same bounded acquisition deadline.
    // A failed planning result can return an empty data-lock plan; it never authorizes writes.
    // Discovery, including synchronous planning, must observe the supplied deadline token.
    // Coordination is transaction-scoped and acquired before the resolver savepoint. Savepoint
    // cleanup cannot release it; its owner must complete the transaction to release protection
    // of symbolic season/membership natural keys. No executor may reuse a plan in another transaction.
    internal Task AcquireDiscoveredAsync(NpgsqlTransaction transaction,
        Func<CancellationToken, Task<FootballAdvisoryLockPlan>> discover,
        TimeSpan maxWait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateWait(maxWait);
        var connection = transaction.Connection;
        if (connection is null || connection.State != ConnectionState.Open)
            throw new InvalidOperationException("An active transaction on an open PostgreSQL connection is required.");
        return AcquireDiscoveredAsync(new NpgsqlLockSession(connection, transaction, maxWait),
            new AdvisoryLockKeys().FootballCoordination(), discover, maxWait, cancellationToken);
    }

    internal static async Task AcquireDiscoveredAsync(IAdvisoryLockSession session, long coordinationKey,
        Func<CancellationToken, Task<FootballAdvisoryLockPlan>> discover,
        TimeSpan maxWait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(discover);
        ValidateWait(maxWait);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(maxWait);
        var token = deadline.Token;
        try
        {
            var previousTimeout = await session.GetLockTimeoutAsync(token);
            var milliseconds = checked((int)Math.Ceiling(maxWait.TotalMilliseconds));
            await session.SetLockTimeoutAsync(milliseconds.ToString(CultureInfo.InvariantCulture) + "ms", token);
            await session.AcquireLockAsync(coordinationKey, token);
            var plan = await discover(token);
            if (plan.CoordinationKey != coordinationKey)
                throw new InvalidOperationException("Discovery changed the coordination lock key.");
            foreach (var key in plan.OrderedKeys)
            {
                token.ThrowIfCancellationRequested();
                await session.AcquireLockAsync(key, token);
            }
            // Failure paths do not issue further SQL on a possibly aborted transaction.
            await session.SetLockTimeoutAsync(previousTimeout, token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The advisory lock acquisition deadline expired; roll back the transaction.", exception);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            throw new TimeoutException("The PostgreSQL advisory lock wait expired; roll back the transaction.", exception);
        }
    }

    private static void ValidateWait(TimeSpan maxWait)
    {
        if (maxWait < TimeSpan.FromMilliseconds(1) || maxWait > TimeSpan.FromMilliseconds(int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(maxWait), "Lock waiting must be between 1 and Int32.MaxValue milliseconds.");
    }

    internal static NpgsqlCommand CreateLockCommand(NpgsqlConnection connection, NpgsqlTransaction? transaction, long key, int commandTimeout)
    {
        var command = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_xact_lock($1)", connection, transaction)
        { CommandTimeout = commandTimeout };
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = key });
        return command;
    }

    internal static NpgsqlCommand CreateTimeoutCommand(NpgsqlConnection connection, NpgsqlTransaction? transaction, string timeout, int commandTimeout)
    {
        var command = new NpgsqlCommand("SELECT pg_catalog.set_config('lock_timeout', $1, true)", connection, transaction)
        { CommandTimeout = commandTimeout };
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = timeout });
        return command;
    }

    private sealed class NpgsqlLockSession(NpgsqlConnection connection, NpgsqlTransaction transaction, TimeSpan maxWait) : IAdvisoryLockSession
    {
        private readonly int commandTimeout = checked((int)Math.Ceiling(maxWait.TotalSeconds));

        public async Task<string> GetLockTimeoutAsync(CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand("SELECT pg_catalog.current_setting('lock_timeout')", connection, transaction)
            { CommandTimeout = commandTimeout };
            return (string)(await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("PostgreSQL did not return lock_timeout."));
        }

        public async Task SetLockTimeoutAsync(string timeout, CancellationToken cancellationToken)
        {
            await using var command = CreateTimeoutCommand(connection, transaction, timeout, commandTimeout);
            await command.ExecuteScalarAsync(cancellationToken);
        }

        public async Task AcquireLockAsync(long key, CancellationToken cancellationToken)
        {
            await using var command = CreateLockCommand(connection, transaction, key, commandTimeout);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

// Internal I/O seam lets unit tests exercise ordering, cancellation and failure behavior without PostgreSQL.
internal interface IAdvisoryLockSession
{
    Task<string> GetLockTimeoutAsync(CancellationToken cancellationToken);
    Task SetLockTimeoutAsync(string timeout, CancellationToken cancellationToken);
    Task AcquireLockAsync(long key, CancellationToken cancellationToken);
}
