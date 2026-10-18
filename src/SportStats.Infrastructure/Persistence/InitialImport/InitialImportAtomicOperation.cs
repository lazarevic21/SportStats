using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SportStats.Application.Football.InitialImport;

namespace SportStats.Infrastructure.Persistence.InitialImport;

internal interface IInitialImportAtomicSession
{
    Task BeginAsync(CancellationToken token);
    Task CommitAsync(CancellationToken token);
    Task RollbackAsync(CancellationToken token);
    ValueTask DisposeAsync();
    void ClearTracking();
}

internal static class InitialImportAtomicOperation
{
    internal static async Task<InitialImportResult> ExecuteAsync(IInitialImportAtomicSession session,
        Func<CancellationToken, Task<InitialImportResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(operation);
        var commitAttempted = false;
        var rollbackAttempted = false;
        var rollbackConfirmed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await session.BeginAsync(cancellationToken);
            var result = await operation(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                rollbackAttempted = true;
                await RollbackAsync(session);
                rollbackConfirmed = true;
                return result with { Applied = false, Counts = result.Counts.Select(x => x with { Created = 0 }).ToArray() };
            }
            commitAttempted = true;
            await session.CommitAsync(cancellationToken);
            return result with { Applied = true };
        }
        catch (Exception original)
        {
            Exception? rollbackFailure = null;
            if (!rollbackAttempted)
            {
                rollbackAttempted = true;
                try { await RollbackAsync(session); rollbackConfirmed = true; }
                catch (Exception cleanup) { rollbackFailure = cleanup; }
            }
            if (commitAttempted)
                throw new InvalidOperationException("Import commit outcome is indeterminate; inspect a fresh preview before retrying.",
                    rollbackFailure is null ? original : new AggregateException(original, rollbackFailure));
            if (!rollbackConfirmed)
                throw new InvalidOperationException("Import rollback was not confirmed; inspect the database before retrying.",
                    rollbackFailure is null ? original : new AggregateException(original, rollbackFailure));
            throw;
        }
        finally
        {
            try
            {
                try { session.ClearTracking(); }
                finally { await session.DisposeAsync(); }
            }
            catch (Exception cleanup)
            {
                throw new InvalidOperationException(commitAttempted
                    ? "Import commit outcome is indeterminate; inspect a fresh preview before retrying."
                    : "Import cleanup was not confirmed; inspect the database before retrying.", cleanup);
            }
        }
    }

    private static async Task RollbackAsync(IInitialImportAtomicSession session)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await session.RollbackAsync(cleanup.Token);
    }
}

internal sealed class EfInitialImportAtomicSession(SportStatsDbContext context) : IInitialImportAtomicSession
{
    private IDbContextTransaction? transaction;
    public async Task BeginAsync(CancellationToken token)
    {
        if (context.Database.CurrentTransaction is not null || context.ChangeTracker.Entries().Any())
            throw new InvalidOperationException("Import requires a clean dedicated context with no existing transaction.");
        transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
    }
    public Task CommitAsync(CancellationToken token) => (transaction ?? throw new InvalidOperationException("Import transaction is absent.")).CommitAsync(token);
    public Task RollbackAsync(CancellationToken token) => transaction?.RollbackAsync(token) ?? Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        if (transaction is not null) await transaction.DisposeAsync();
    }
    public void ClearTracking() => context.ChangeTracker.Clear();
}
