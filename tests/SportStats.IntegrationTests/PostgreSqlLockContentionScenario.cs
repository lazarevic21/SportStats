using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;

namespace SportStats.IntegrationTests;

public sealed record PostgreSqlLockContentionObservation(bool ServerObservedWait,
    bool AcquiredAfterCallerRollback, bool TrackingEmpty);

public sealed partial class PostgreSqlTestDatabaseFixture
{
    // Fixed, read-only scenario: both independently verified sessions own rollback-only
    // transactions. No connection, transaction, or other database capability escapes.
    public async Task<PostgreSqlLockContentionObservation> RunLockContentionScenarioAsync(
        CancellationToken token = default)
    {
        ValidateLiveOptIn(Environment.GetEnvironmentVariable("SPORTSTATS_RUN_POSTGRES_TESTS"));
        await VerifyOwnedSessionAsync(token);
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("Contention requires a clean fixture-owned session.");
        await using var contender = await OpenAsync(
            Environment.GetEnvironmentVariable("SPORTSTATS_TEST_CONNECTION_STRING"), token);
        await contender.VerifyOwnedSessionAsync(token);
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await using var ownerTransaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        IDbContextTransaction? contenderTransaction = null;
        Task? pending = null;
        var ownerReleased = false;
        try
        {
            contenderTransaction = await contender.context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            var acquirer = new PostgreSqlAdvisoryLockAcquirer();
            var plan = new FootballAdvisoryLockPlan([]);
            await acquirer.AcquireAsync((NpgsqlTransaction)ownerTransaction.GetDbTransaction(),
                plan, TimeSpan.FromSeconds(20), deadline.Token);
            pending = acquirer.AcquireAsync((NpgsqlTransaction)contenderTransaction.GetDbTransaction(),
                plan, TimeSpan.FromSeconds(20), acquisition.Token);
            var observed = false;
            while (!observed)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (pending.IsCompleted)
                {
                    await pending;
                    throw new InvalidOperationException("The competing acquisition completed before caller rollback.");
                }
                await VerifyOwnedSessionAsync(deadline.Token);
                await using var command = new NpgsqlCommand("""
                    SELECT $1 = ANY(pg_catalog.pg_blocking_pids($2)) AND EXISTS (
                        SELECT 1 FROM pg_catalog.pg_locks
                        WHERE pid = $2 AND locktype = 'advisory' AND NOT granted)
                    """, connection, (NpgsqlTransaction)ownerTransaction.GetDbTransaction())
                    { CommandTimeout = 5 };
                command.Parameters.AddWithValue(connection.ProcessID);
                command.Parameters.AddWithValue(contender.connection.ProcessID);
                observed = (bool)(await command.ExecuteScalarAsync(deadline.Token)
                    ?? throw new InvalidOperationException("PostgreSQL did not return contention status."));
                if (!observed) await Task.Delay(25, deadline.Token);
            }
            await RollBackContentionTransactionAsync(this, ownerTransaction);
            ownerReleased = true;
            await pending.WaitAsync(deadline.Token);
            return new(observed, pending.IsCompletedSuccessfully,
                !context.ChangeTracker.Entries().Any() && !contender.context.ChangeTracker.Entries().Any());
        }
        finally
        {
            acquisition.Cancel();
            try
            {
                if (pending is not null)
                {
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (OperationCanceledException) { }
                    catch (TimeoutException) when (pending.IsCompleted) { }
                    catch
                    {
                        contender.unusable = true;
                        await contender.connection.DisposeAsync();
                        throw new InvalidOperationException("Contention acquisition cleanup failed; the contender was closed.");
                    }
                }
            }
            finally
            {
                try
                {
                    if (contenderTransaction is not null)
                    {
                        try { await RollBackContentionTransactionAsync(contender, contenderTransaction); }
                        finally { await contenderTransaction.DisposeAsync(); }
                    }
                }
                finally
                {
                    if (!ownerReleased) await RollBackContentionTransactionAsync(this, ownerTransaction);
                }
            }
        }
    }

    private static async Task RollBackContentionTransactionAsync(PostgreSqlTestDatabaseFixture fixture,
        IDbContextTransaction transaction)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await transaction.RollbackAsync(cleanup.Token);
            cleanup.Token.ThrowIfCancellationRequested();
        }
        catch
        {
            fixture.unusable = true;
            await fixture.connection.DisposeAsync();
            throw new InvalidOperationException("Contention transaction rollback could not be confirmed; the fixture was closed.");
        }
        finally { fixture.context.ChangeTracker.Clear(); }
    }
}
