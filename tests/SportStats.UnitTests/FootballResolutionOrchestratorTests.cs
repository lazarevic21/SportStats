using System.Data;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;
using Xunit;

namespace SportStats.UnitTests;

public sealed class FootballResolutionOrchestratorTests
{
    private static readonly Guid SportId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid CompetitionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid SeasonId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid TeamId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static ProviderIdentity Identity(string id) => new("provider", "scope", id);
    private static IdentityResolutionBatch Batch() => new([new(SportId, new(Identity("competition"), "PL", "Premier League"))], [], [], []);
    private static FootballDiscovery Empty() => new([new(SportId, "football")], [], [], [], [], [], []);

    [Fact]
    public async Task SuccessAcquiresCoordinationOnceAndOrderedDataLocksBeforeSavepointAndReread()
    {
        var h = new Harness();
        var plan = new FootballResolutionPlanner().Plan(Batch(), Empty()).Plan!;
        Assert.IsType<FootballResolutionOrchestrationResult.Completed>(await h.Run());
        Assert.Equal(new[] { $"lock:{plan.IndividualDataLocks.CoordinationKey}", "read:1" }
            .Concat(plan.IndividualDataLocks.OrderedKeys.Select(k => $"lock:{k}"))
            .Concat(["create", "read:2", "allocate", "allocate", "clear", "release"]), h.Events);
        Assert.Equal(1, h.Events.Count(x => x == $"lock:{plan.IndividualDataLocks.CoordinationKey}"));
        Assert.False(h.Shell.RequiresCallerRollback);
        Assert.DoesNotContain(typeof(FootballResolutionOrchestrationResult.Completed).GetProperties(),
            x => x.PropertyType == typeof(Guid) || x.PropertyType == typeof(FootballResolutionPlan)
                || x.PropertyType == typeof(FootballIdentityMaterialization));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DomainFailureReturnsContractWithoutAllocationsAndRollsBack(int failedRead)
    {
        var h = new Harness { Read = (n, _) => Task.FromResult(n == failedRead ? Empty() with { Sports = [] } : Empty()) };
        var result = Assert.IsType<FootballResolutionOrchestrationResult.Failed>(await h.Run());
        Assert.Equal(IdentityResolutionFailureCode.MissingReference, result.Failure.Code);
        Assert.Equal("Competitions[0].SportId", result.Failure.Field);
        Assert.DoesNotContain("allocate", h.Events);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
        Assert.Equal(failedRead, h.Reads);
        Assert.False(h.Shell.RequiresCallerRollback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MappingDecisionDriftThrowsBeforeAllocationAndRollbackIsRecoverable(bool initialMapped)
    {
        var mapped = Empty() with { Competitions = [new(CompetitionId, SportId, "PL")],
            Mappings = [new(IdentityLockEntity.Competition, Identity("competition"), CompetitionId)] };
        var h = new Harness { Read = (n, _) => Task.FromResult((n == 1) == initialMapped ? mapped : Empty()) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        Assert.DoesNotContain("allocate", h.Events);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
        Assert.False(h.Shell.RequiresCallerRollback);
        h.Read = (_, _) => Task.FromResult(Empty());
        Assert.IsType<FootballResolutionOrchestrationResult.Completed>(await h.Run());
    }

    [Fact]
    public async Task MembershipExistenceDriftIsAnInvariantFailure()
    {
        var batch = new IdentityResolutionBatch([], [], [new(new EntityReference<Season>.Internal(SeasonId), new(Identity("team"), "Team", null, null))], []);
        var state = Empty() with { Competitions = [new(CompetitionId, SportId, "PL")], Seasons = [new(SeasonId, CompetitionId, "2026")],
            Teams = [TeamId], Mappings = [new(IdentityLockEntity.FootballTeam, Identity("team"), TeamId)] };
        var h = new Harness { Read = (n, _) => Task.FromResult(n == 1 ? state : state with { Memberships = [(SeasonId, TeamId)] }) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run(batch));
        Assert.DoesNotContain("allocate", h.Events);
        Assert.Contains("rollback", h.Events);
    }

    [Theory]
    [InlineData("refresh")]
    [InlineData("allocate")]
    public async Task OperationCancellationRollsBackWithoutQuarantine(string stage)
    {
        using var cts = new CancellationTokenSource();
        var h = new Harness();
        if (stage == "refresh") h.Read = (n, token) => { if (n == 2) cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Empty()); };
        else h.OnAllocate = cts.Cancel;
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
        Assert.False(h.Shell.RequiresCallerRollback);
    }

    [Fact]
    public async Task PreCancellationDoesNotInspectTransactionOrPerformIo()
    {
        var h = new Harness();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: new CancellationToken(true)));
        Assert.Equal(0, h.Transaction.Inspections);
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task AcquisitionDeadlineExpiresDuringDiscoveryAndQuarantinesSubsequentCalls()
    {
        var h = new Harness { MaxWait = TimeSpan.FromMilliseconds(25), Read = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Empty(); } };
        await Assert.ThrowsAsync<TimeoutException>(() => h.Run());
        Assert.True(h.Shell.RequiresCallerRollback);
        Assert.DoesNotContain("create", h.Events);
        Assert.DoesNotContain("allocate", h.Events);
        var reads = h.Reads;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        Assert.Equal(reads, h.Reads);
    }

    [Fact]
    public async Task LockPhaseCancellationQuarantinesAndDoesNotCreateSavepoint()
    {
        using var cts = new CancellationTokenSource();
        var h = new Harness { Read = (_, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Empty()); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token));
        Assert.True(h.Shell.RequiresCallerRollback);
        Assert.DoesNotContain("create", h.Events);
    }

    [Fact]
    public async Task RollbackFailureQuarantinesAndRetainsOriginalException()
    {
        var h = new Harness { Read = (n, _) => n == 2 ? throw new IOException("refresh failed") : Task.FromResult(Empty()) };
        h.Transaction.FailRollback = true;
        var error = await Assert.ThrowsAsync<FootballResolutionCleanupException>(() => h.Run());
        Assert.IsType<IOException>(error.OperationException);
        Assert.True(h.Shell.RequiresCallerRollback);
        Assert.Contains("clear", h.Events);
        Assert.DoesNotContain("allocate", h.Events);
    }

    [Fact]
    public async Task CallerMutationAfterInitialDiscoveryCannotChangeRevalidationInput()
    {
        var requests = Batch().Competitions.ToList();
        var h = new Harness { Read = (n, _) => { if (n == 1) requests[0] = new(Guid.Empty, new(Identity("bad\0"), "bad", "bad")); return Task.FromResult(Empty()); } };
        Assert.IsType<FootballResolutionOrchestrationResult.Completed>(await h.Run(new(requests, [], [], [])));
        Assert.Equal(2, h.Reads);
    }

    [Fact]
    public async Task InvalidInputFailsBeforeTransactionInspection()
    {
        var h = new Harness();
        var batch = new IdentityResolutionBatch([new(SportId, new(Identity("bad\0"), "PL", "Premier League"))], [], [], []);
        Assert.Equal(IdentityResolutionFailureCode.InvalidInput, Assert.IsType<FootballResolutionOrchestrationResult.Failed>(await h.Run(batch)).Failure.Code);
        Assert.Equal(0, h.Transaction.Inspections);
        Assert.Empty(h.Events);
    }

    private sealed class Harness : IFootballResolutionPreparationSession, IAdvisoryLockSession
    {
        public List<string> Events { get; } = [];
        public TransactionSession Transaction { get; }
        public FootballResolutionTransaction Shell { get; }
        private readonly FootballResolutionOrchestrator orchestrator;
        public int Reads { get; private set; }
        public Func<int, CancellationToken, Task<FootballDiscovery>> Read { get; set; } = (_, _) => Task.FromResult(Empty());
        public Action? OnAllocate { get; set; }
        public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(5);
        public Harness()
        {
            Transaction = new(Events);
            Shell = new(Transaction, TimeSpan.FromSeconds(5));
            orchestrator = new(Shell, this, new FootballIdentityMaterializer(() => { Events.Add("allocate"); OnAllocate?.Invoke(); return Guid.NewGuid(); }));
        }
        public Task<FootballResolutionOrchestrationResult> Run(IdentityResolutionBatch? batch = null, CancellationToken token = default) => orchestrator.ExecuteAsync(batch ?? Batch(), token);
        public Task<FootballDiscovery> ReadAsync(CancellationToken token) { Events.Add($"read:{++Reads}"); return Read(Reads, token); }
        public Task AcquireAsync(Func<CancellationToken, Task<FootballAdvisoryLockPlan>> discover, CancellationToken token)
            => PostgreSqlAdvisoryLockAcquirer.AcquireDiscoveredAsync(this, new AdvisoryLockKeys().FootballCoordination(), discover, MaxWait, token);
        public Task<string> GetLockTimeoutAsync(CancellationToken token) => Task.FromResult("0");
        public Task SetLockTimeoutAsync(string timeout, CancellationToken token) => Task.CompletedTask;
        public Task AcquireLockAsync(long key, CancellationToken token) { token.ThrowIfCancellationRequested(); Events.Add($"lock:{key}"); return Task.CompletedTask; }
    }

    private sealed class TransactionSession(List<string> events) : IResolutionTransactionSession
    {
        private readonly object handle = new(), identity = new(), connection = new();
        public int Inspections { get; private set; }
        public bool FailRollback { get; set; }
        public ResolutionTransactionSnapshot Inspect() { Inspections++; return new(handle, identity, connection, connection, ConnectionState.Open, true, IsolationLevel.ReadCommitted, true); }
        public bool HasTrackedEntries => false;
        public bool HasPendingChanges => false;
        public void ClearTracking() => events.Add("clear");
        public Task CreateSavepointAsync(object transaction, string name, CancellationToken token) { events.Add("create"); return Task.CompletedTask; }
        public Task RollbackToSavepointAsync(object transaction, string name, CancellationToken token) { events.Add("rollback"); if (FailRollback) throw new IOException("rollback failed"); return Task.CompletedTask; }
        public Task ReleaseSavepointAsync(object transaction, string name, CancellationToken token) { events.Add("release"); return Task.CompletedTask; }
    }
}
