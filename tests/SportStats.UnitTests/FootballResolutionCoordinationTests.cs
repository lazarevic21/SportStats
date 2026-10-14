using System.Data;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;
using Xunit;

namespace SportStats.UnitTests;

public sealed class FootballResolutionCoordinationTests
{
    private static readonly Guid SportId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid CompetitionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid SeasonId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid TeamId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static ProviderIdentity Identity(string id) => new("provider", "scope", id);

    private static (IdentityResolutionBatch Batch, FootballDiscovery Discovery) Input(bool competitionExists, bool seasonExists, bool teamExists)
    {
        var competition = Identity("competition");
        var season = Identity("season");
        var team = Identity("team");
        var mappings = new List<DiscoveredMapping>();
        if (competitionExists) mappings.Add(new(IdentityLockEntity.Competition, competition, CompetitionId));
        if (seasonExists) mappings.Add(new(IdentityLockEntity.Season, season, SeasonId));
        if (teamExists) mappings.Add(new(IdentityLockEntity.FootballTeam, team, TeamId));
        return (new(
            [new(SportId, new(competition, "PL", "Premier League"))],
            [new(new EntityReference<Competition>.External(competition),
                new(season, 2026, "2026", new(2026, 8, 1), new(2027, 5, 31)))],
            [new(new EntityReference<Season>.External(season), new(team, "Team", null, null))], []),
            new([new(SportId, "football")],
                competitionExists ? [new(CompetitionId, SportId, "PL")] : [],
                seasonExists ? [new(SeasonId, CompetitionId, "2026")] : [],
                teamExists ? [TeamId] : [], [], [], mappings.AsReadOnly()));
    }

    [Theory]
    [InlineData(false, false, false, 1, 1)]
    [InlineData(true, false, false, 0, 1)]
    [InlineData(true, false, true, 0, 1)]
    [InlineData(true, true, false, 0, 1)]
    [InlineData(true, true, true, 0, 0)]
    public void PlanDistinguishesCoordinationProtectedNaturalKeysFromIndividualDataLocks(
        bool competitionExists, bool seasonExists, bool teamExists, int symbolicSeasons, int symbolicMemberships)
    {
        var (batch, discovery) = Input(competitionExists, seasonExists, teamExists);
        var result = new FootballResolutionPlanner().Plan(batch, discovery);
        Assert.Null(result.Failure);
        var plan = Assert.IsType<FootballResolutionPlan>(result.Plan);
        Assert.True(plan.RequiresCoordinationUntilTransactionEnd);
        Assert.Equal(symbolicSeasons, plan.CoordinationProtectedSeasonKeys.Count);
        Assert.Equal(symbolicMemberships, plan.CoordinationProtectedMembershipKeys.Count);
        if (symbolicSeasons == 1)
        {
            var key = Assert.Single(plan.CoordinationProtectedSeasonKeys);
            Assert.Equal(plan.Competitions[0].Entity, key.Competition);
            Assert.Equal("2026", key.Key);
            Assert.Null(key.Competition.ExistingId);
        }
        if (symbolicMemberships == 1)
        {
            var key = Assert.Single(plan.CoordinationProtectedMembershipKeys);
            Assert.Equal(plan.Seasons[0].Entity, key.Season);
            Assert.Equal(plan.Teams[0].Entity, key.Team);
            Assert.True(key.Season.ExistingId is null || key.Team.ExistingId is null);
        }
        var keys = new AdvisoryLockKeys();
        var expected = new List<long>
        {
            keys.Sport("football"), keys.Competition(SportId, "PL"),
            keys.Identity(IdentityLockEntity.Competition, Identity("competition")),
            keys.Identity(IdentityLockEntity.Season, Identity("season")),
            keys.Identity(IdentityLockEntity.FootballTeam, Identity("team"))
        };
        if (competitionExists) expected.Add(keys.Season(CompetitionId, "2026"));
        if (seasonExists && teamExists) expected.Add(keys.SeasonTeam(SeasonId, TeamId));
        Assert.Equal(expected.Distinct().Order(), plan.IndividualDataLocks.OrderedKeys);
        Assert.Equal(keys.FootballCoordination(), plan.IndividualDataLocks.CoordinationKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CoordinationAndDataLocksPrecedeSavepointAndRemainOutsideSavepointCleanup(bool success)
    {
        var events = new List<string>();
        var session = new TransactionSession(events);
        var locks = new LockSession(events);
        FootballPlanningResult? result = null;
        var (batch, discovery) = Input(false, false, false);
        await new FootballResolutionTransaction(session, TimeSpan.FromSeconds(5)).ExecuteAsync(
            _ => Task.FromResult(result!), _ => success,
            token => PostgreSqlAdvisoryLockAcquirer.AcquireDiscoveredAsync(locks,
                new AdvisoryLockKeys().FootballCoordination(), planningToken =>
                {
                    events.Add("discover");
                    result = new FootballResolutionPlanner().Plan(batch, discovery, planningToken);
                    return Task.FromResult(result.Plan!.IndividualDataLocks);
                }, TimeSpan.FromSeconds(5), token));
        var coordination = new AdvisoryLockKeys().FootballCoordination();
        Assert.Equal(coordination, locks.Acquired[0]);
        Assert.Equal(1, events.Count(e => e == $"lock:{coordination}"));
        Assert.True(events.IndexOf($"lock:{coordination}") < events.IndexOf("discover"));
        var create = events.IndexOf("create");
        Assert.True(events.FindLastIndex(e => e.StartsWith("lock:", StringComparison.Ordinal)) < create);
        Assert.Equal(result!.Plan!.IndividualDataLocks.OrderedKeys, locks.Acquired.Skip(1));
        Assert.Contains("release-savepoint", events);
        Assert.Equal(!success, events.Contains("rollback-savepoint"));
        // Cleanup uses only savepoint methods. Neither existing helper ends the caller transaction.
        Assert.DoesNotContain("commit", events);
        Assert.DoesNotContain("rollback-transaction", events);
    }

    [Fact]
    public async Task PlanningCancellationQuarantinesTransactionBeforeSavepoint()
    {
        var events = new List<string>();
        var session = new TransactionSession(events);
        var locks = new LockSession(events);
        var shell = new FootballResolutionTransaction(session, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var (batch, discovery) = Input(false, false, false);
        discovery = discovery with { Sports = new CancellingSports(cancellation) };
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shell.ExecuteAsync(
            _ => Task.FromResult(1), _ => true,
            token => PostgreSqlAdvisoryLockAcquirer.AcquireDiscoveredAsync(locks,
                new AdvisoryLockKeys().FootballCoordination(), planningToken =>
                {
                    events.Add("discover");
                    var result = new FootballResolutionPlanner().Plan(batch, discovery, planningToken);
                    return Task.FromResult(result.Plan!.IndividualDataLocks);
                }, TimeSpan.FromSeconds(5), token), cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(shell.RequiresCallerRollback);
        Assert.Single(locks.Acquired);
        Assert.DoesNotContain("create", events);
        Assert.DoesNotContain("rollback-savepoint", events);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FootballResolutionTransaction(session, TimeSpan.FromSeconds(5))
            .ExecuteAsync(_ => Task.FromResult(1), _ => true));
    }

    private sealed class CancellingSports(CancellationTokenSource cancellation) : IReadOnlyList<DiscoveredSport>
    {
        public int Count => 1;
        public DiscoveredSport this[int index] => new(SportId, "football");
        public IEnumerator<DiscoveredSport> GetEnumerator()
        {
            cancellation.Cancel();
            yield return new(SportId, "football");
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TransactionSession(List<string> events) : IResolutionTransactionSession
    {
        private readonly object handle = new();
        private readonly object identity = new();
        private readonly object connection = new();
        public ResolutionTransactionSnapshot Inspect() => new(handle, identity, connection, connection,
            ConnectionState.Open, true, IsolationLevel.ReadCommitted, true);
        public bool HasTrackedEntries => false;
        public bool HasPendingChanges => false;
        public void ClearTracking() => events.Add("clear");
        public Task CreateSavepointAsync(object transaction, string name, CancellationToken token)
        { events.Add("create"); return Task.CompletedTask; }
        public Task RollbackToSavepointAsync(object transaction, string name, CancellationToken token)
        { events.Add("rollback-savepoint"); return Task.CompletedTask; }
        public Task ReleaseSavepointAsync(object transaction, string name, CancellationToken token)
        { events.Add("release-savepoint"); return Task.CompletedTask; }
    }

    private sealed class LockSession(List<string> events) : IAdvisoryLockSession
    {
        public List<long> Acquired { get; } = [];
        public Task<string> GetLockTimeoutAsync(CancellationToken token) => Task.FromResult("0");
        public Task SetLockTimeoutAsync(string timeout, CancellationToken token) => Task.CompletedTask;
        public Task AcquireLockAsync(long key, CancellationToken token)
        { events.Add($"lock:{key}"); Acquired.Add(key); return Task.CompletedTask; }
    }
}
