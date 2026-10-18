using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.InitialImport;
using SportStats.Infrastructure.Persistence.InitialImport;
using Xunit;

namespace SportStats.UnitTests;

public sealed class InitialImportAtomicOperationTests
{
    private static InitialImportResult Success => new(false, [new("Teams", 2, 1, 0, 0)], []);

    [Fact]
    public async Task Success_commits_once_after_operation_and_disposes()
    {
        var session = new Session();
        var result = await InitialImportAtomicOperation.ExecuteAsync(session, _ => { session.Events.Add("operation"); return Task.FromResult(Success); });
        Assert.True(result.Applied);
        Assert.Equal(2, result.Counts[0].Created);
        Assert.Equal(new[] { "begin", "operation", "commit", "clear", "dispose" }, session.Events);
    }

    [Fact]
    public async Task Conflict_rolls_back_complete_transaction_and_reports_zero_creates()
    {
        var session = new Session();
        var conflict = Success with { Conflicts = [new(IdentityResolutionFailureCode.RelationshipConflict, "Matches", "Conflict.")] };
        var result = await InitialImportAtomicOperation.ExecuteAsync(session, _ => Task.FromResult(conflict));
        Assert.False(result.Applied);
        Assert.Equal(0, result.Counts[0].Created);
        Assert.Equal(new[] { "begin", "rollback", "clear", "dispose" }, session.Events);
    }

    [Fact]
    public async Task Exception_rolls_back_and_preserves_original_failure()
    {
        var session = new Session();
        var failure = new FormatException("Safe failure.");
        var thrown = await Assert.ThrowsAsync<FormatException>(() => InitialImportAtomicOperation.ExecuteAsync(session, _ => throw failure));
        Assert.Same(failure, thrown);
        Assert.Equal(new[] { "begin", "rollback", "clear", "dispose" }, session.Events);
        Assert.True(session.RollbackToken.CanBeCanceled);
    }

    [Fact]
    public async Task Cancellation_uses_independent_cleanup_token_and_never_commits()
    {
        var session = new Session();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InitialImportAtomicOperation.ExecuteAsync(session, _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(Success);
        }, cancellation.Token));
        Assert.NotEqual(cancellation.Token, session.RollbackToken);
        Assert.False(session.RollbackToken.IsCancellationRequested);
        Assert.Equal(new[] { "begin", "rollback", "clear", "dispose" }, session.Events);
    }

    [Fact]
    public async Task Begin_failure_still_cleans_and_disposes_without_operation()
    {
        var session = new Session { FailBegin = true };
        await Assert.ThrowsAsync<FormatException>(() => InitialImportAtomicOperation.ExecuteAsync(session, _ => throw new Exception("Must not execute.")));
        Assert.Equal(new[] { "begin", "rollback", "clear", "dispose" }, session.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_failure_is_indeterminate_even_if_rollback_fails(bool rollbackFails)
    {
        var session = new Session { FailCommit = true, FailRollback = rollbackFails };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => InitialImportAtomicOperation.ExecuteAsync(session, _ => Task.FromResult(Success)));
        Assert.Contains("indeterminate", failure.Message);
        Assert.Equal(new[] { "begin", "commit", "rollback", "clear", "dispose" }, session.Events);
    }

    [Fact]
    public async Task Rollback_failure_does_not_claim_success_or_rollback()
    {
        var session = new Session { FailRollback = true };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => InitialImportAtomicOperation.ExecuteAsync(session, _ => throw new FormatException()));
        Assert.Contains("rollback was not confirmed", failure.Message);
        Assert.Equal(new[] { "begin", "rollback", "clear", "dispose" }, session.Events);
    }

    private sealed class Session : IInitialImportAtomicSession
    {
        public List<string> Events { get; } = [];
        public bool FailBegin { get; init; }
        public bool FailCommit { get; init; }
        public bool FailRollback { get; init; }
        public CancellationToken RollbackToken { get; private set; }
        public Task BeginAsync(CancellationToken token) { Events.Add("begin"); if (FailBegin) throw new FormatException(); return Task.CompletedTask; }
        public Task CommitAsync(CancellationToken token) { Events.Add("commit"); if (FailCommit) throw new FormatException(); return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken token) { Events.Add("rollback"); RollbackToken = token; if (FailRollback) throw new FormatException(); return Task.CompletedTask; }
        public void ClearTracking() => Events.Add("clear");
        public ValueTask DisposeAsync() { Events.Add("dispose"); return ValueTask.CompletedTask; }
    }
}
