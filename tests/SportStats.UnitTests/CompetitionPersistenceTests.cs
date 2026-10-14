using System.Data;
using Microsoft.EntityFrameworkCore;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Platform;
using SportStats.Domain.Football;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;
using Xunit;

namespace SportStats.UnitTests;

public sealed class CompetitionPersistenceTests
{
    private static readonly Guid SportId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ExistingId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static ProviderIdentity Identity(string id = "  opaque  ") => new("provider", "scope", id);
    private static IdentityResolutionBatch Batch() => new([new(SportId, new(Identity(), "PL", "Premier League"))], [], [], []);
    private static FootballDiscovery Empty() => new([new(SportId, "football")], [], [], [], [], [], []);
    private static FootballDiscovery Mapped() => Empty() with { Competitions = [new(ExistingId, SportId, "PL")], Mappings = [new(IdentityLockEntity.Competition, Identity(), ExistingId)] };

    [Fact]
    public async Task NewCompetitionUsesAllocatedIdsSportAndExactIdentityAfterLockedRevalidation()
    {
        var h = new Harness();
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run());
        var competition = Assert.Single(h.Competitions);
        var mapping = Assert.Single(h.Mappings);
        Assert.Same(h.Sport, competition.Sport);
        Assert.Equal(SportId, competition.SportId);
        Assert.Equal("PL", competition.Code);
        Assert.Equal("Premier League", competition.Name);
        Assert.Equal(h.Allocated[0], competition.Id);
        Assert.Equal(h.Allocated[1], mapping.Id);
        Assert.Equal(competition.Id, mapping.CompetitionId);
        Assert.Equal(Identity().ProviderKey, mapping.ProviderKey);
        Assert.Equal(Identity().IdentityScope, mapping.IdentityScope);
        Assert.Equal(Identity().ExternalId, mapping.ExternalId);
        Assert.Equal(competition.Id, Assert.Single(result.Competitions).EntityId);
        Assert.Equal(IdentityResolutionDisposition.CreatedEntity, result.Competitions[0].Disposition);
        Assert.Equal(Identity(), result.Competitions[0].Identity);
        Assert.Equal(1, h.Saves);
        var plan = new FootballResolutionPlanner().Plan(Batch(), Empty()).Plan!;
        Assert.Equal(new[] { $"lock:{plan.IndividualDataLocks.CoordinationKey}", "read:1" }
            .Concat(plan.IndividualDataLocks.OrderedKeys.Select(k => $"lock:{k}"))
            .Concat(["create", "read:2", "allocate", "allocate", "load", "competition", "mapping", "save", "postsave", "clear", "release"]), h.Events);
    }

    [Fact]
    public async Task ExistingAliasesReuseIdsWithoutLoadingOrUpdatingMetadata()
    {
        var alias = Identity("alias");
        var state = Mapped() with { Mappings = [new(IdentityLockEntity.Competition, Identity(), ExistingId), new(IdentityLockEntity.Competition, alias, ExistingId)] };
        var h = new Harness { Read = (_, _) => Task.FromResult(state) };
        var batch = new IdentityResolutionBatch([new(SportId, new(alias, "PL", "Changed name")), Batch().Competitions[0]], [], [], []);
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(batch));
        Assert.Equal(new[] { alias, Identity() }, result.Competitions.Select(x => x.Identity));
        Assert.All(result.Competitions, x => { Assert.Equal(ExistingId, x.EntityId); Assert.Equal(IdentityResolutionDisposition.ExistingMapping, x.Disposition); });
        Assert.Empty(h.Competitions); Assert.Empty(h.Mappings); Assert.Empty(h.Allocated);
        Assert.DoesNotContain("load", h.Events); Assert.Equal(0, h.Saves);
    }

    [Theory]
    [InlineData("duplicate", IdentityResolutionFailureCode.DuplicateMappings)]
    [InlineData("ambiguous", IdentityResolutionFailureCode.AmbiguousMappings)]
    [InlineData("natural", IdentityResolutionFailureCode.NaturalKeyConflict)]
    [InlineData("missing", IdentityResolutionFailureCode.MissingReference)]
    public async Task PlanningFailuresNeverStageOrSave(string scenario, IdentityResolutionFailureCode code)
    {
        var state = scenario switch
        {
            "duplicate" => Mapped() with { Mappings = [new(IdentityLockEntity.Competition, Identity(), ExistingId), new(IdentityLockEntity.Competition, Identity(), ExistingId)] },
            "ambiguous" => Mapped() with { Mappings = [new(IdentityLockEntity.Competition, Identity(), ExistingId), new(IdentityLockEntity.Competition, Identity(), Guid.NewGuid())] },
            "natural" => Mapped() with { Mappings = [] },
            _ => Empty() with { Sports = [] }
        };
        var h = new Harness { Read = (_, _) => Task.FromResult(state) };
        Assert.Equal(code, Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()).Failure.Code);
        Assert.Empty(h.Allocated); Assert.Empty(h.Competitions); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
    }

    [Theory]
    [InlineData("Seasons")]
    [InlineData("Teams")]
    [InlineData("Matches")]
    public async Task ValidMixedRequestsAreExplicitlyRejectedBeforeTransactionInspection(string field)
    {
        var season = new NormalizedSeason(Identity("season"), 2026, "2026", new(2026, 8, 1), new(2027, 5, 31));
        var batch = Batch() with
        {
            Seasons = field == "Seasons" ? [new(new EntityReference<Competition>.External(Identity()), season)] : [],
            Teams = field == "Teams" ? [new(new EntityReference<Season>.External(season.Identity), new(Identity("team"), "Team", null, null))] : [],
            Matches = field == "Matches" ? [new(new EntityReference<Competition>.External(Identity()), new(Identity("match"), season, Identity("home"), Identity("away"), DateTime.UtcNow, FootballMatchStatus.Scheduled, "SCHEDULED", 1, new(ScoreFieldPresence.Missing, null), new(ScoreFieldPresence.Missing, null)))] : []
        };
        var h = new Harness();
        var failure = Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run(batch)).Failure;
        Assert.Equal(IdentityResolutionFailureCode.InvalidInput, failure.Code); Assert.Equal(field, failure.Field);
        Assert.Equal(0, h.Transaction.Inspections); Assert.Empty(h.Events);
    }

    [Fact]
    public async Task EmptyBatchSucceedsWithoutStagingOrSaving()
    {
        var h = new Harness();
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(new([], [], [], [])));
        Assert.Empty(result.Competitions); Assert.Empty(h.Competitions); Assert.Empty(h.Mappings); Assert.Empty(h.Allocated); Assert.Equal(0, h.Saves);
    }

    [Fact]
    public async Task ChangedLockedPlanStopsBeforeAllocationsOrWrites()
    {
        var h = new Harness { Read = (n, _) => Task.FromResult(n == 1 ? Empty() : Mapped()) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        Assert.Empty(h.Allocated); Assert.Equal(0, h.Saves); Assert.Empty(h.Competitions);
        Assert.Contains("rollback", h.Events);
    }

    [Fact]
    public async Task MissingLoadedSportReturnsDomainFailureAndClearsTracking()
    {
        var h = new Harness { Sport = null };
        var failure = Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()).Failure;
        Assert.Equal(IdentityResolutionFailureCode.MissingReference, failure.Code); Assert.Equal("Competitions[0].SportId", failure.Field);
        Assert.Empty(h.Competitions); Assert.Equal(0, h.Saves); Assert.Contains("rollback", h.Events);
    }

    [Theory]
    [InlineData("load")]
    [InlineData("competition")]
    [InlineData("mapping")]
    [InlineData("save")]
    [InlineData("postsave")]
    public async Task CancellationAtPersistenceStagesRollsBackAndClears(string stage)
    {
        using var cts = new CancellationTokenSource();
        var h = new Harness { OnStage = value => { if (value == stage) cts.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token));
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
        Assert.False(h.Shell.RequiresCallerRollback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailureRollsBackAndCleanupFailureQuarantines(bool failCleanup)
    {
        var h = new Harness { SaveError = new IOException("save failed") };
        h.Transaction.FailRollback = failCleanup;
        if (failCleanup)
        {
            var error = await Assert.ThrowsAsync<FootballResolutionCleanupException>(() => h.Run());
            Assert.Same(h.SaveError, error.OperationException);
        }
        else Assert.Same(h.SaveError, await Assert.ThrowsAsync<IOException>(() => h.Run()));
        Assert.Equal(failCleanup, h.Shell.RequiresCallerRollback);
        Assert.Contains("rollback", h.Events); Assert.Contains("clear", h.Events);
    }

    [Fact]
    public async Task OriginalOrchestrationRemainsReadOnly()
    {
        var h = new Harness();
        Assert.IsType<FootballResolutionOrchestrationResult.Completed>(await h.Orchestrator.ExecuteAsync(Batch()));
        Assert.Empty(h.Competitions); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves); Assert.DoesNotContain("load", h.Events);
    }

    [Fact]
    public void EfStagingPreservesExistingPrincipalAndMaterializedIdsWithoutDatabaseAccess()
    {
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var sport = new Sport(SportId, "football", "Football");
        context.Attach(sport);
        var competition = new Competition(ExistingId, sport, "PL", "Premier League");
        var mapping = new CompetitionExternalIdentity(Guid.NewGuid(), "provider", "scope", " opaque ", competition.Id);
        var adapter = new EfCompetitionPersistenceSession(context);
        adapter.StageCompetition(competition); adapter.StageMapping(mapping);
        Assert.Equal(EntityState.Unchanged, context.Entry(sport).State);
        Assert.Equal(EntityState.Added, context.Entry(competition).State);
        Assert.Equal(EntityState.Added, context.Entry(mapping).State);
        Assert.Equal(ExistingId, competition.Id); Assert.Equal(competition.Id, mapping.CompetitionId);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task ReplayWithinCallerTransactionReusesSavedCompetitionAndMapping()
    {
        var h = new Harness();
        h.Read = (_, _) => Task.FromResult(h.Saves == 0 ? Empty() : Empty() with
        {
            Competitions = h.Competitions.Select(c => new DiscoveredCompetition(c.Id, c.SportId, c.Code)).ToArray(),
            Mappings = h.Mappings.Select(m => new DiscoveredMapping(IdentityLockEntity.Competition,
                new ProviderIdentity(m.ProviderKey, m.IdentityScope, m.ExternalId), m.CompetitionId)).ToArray()
        });
        var first = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run());
        var second = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run());
        Assert.Equal(first.Competitions[0].EntityId, second.Competitions[0].EntityId);
        Assert.Equal(IdentityResolutionDisposition.ExistingMapping, second.Competitions[0].Disposition);
        Assert.Single(h.Competitions); Assert.Single(h.Mappings); Assert.Equal(1, h.Saves);
        Assert.Equal(2, h.Allocated.Count);
    }

    [Fact]
    public async Task RefreshedDomainFailureReturnsContractBeforeMaterializationAndWrites()
    {
        var h = new Harness { Read = (n, _) => Task.FromResult(n == 1 ? Empty() : Empty() with { Sports = [] }) };
        var failure = Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()).Failure;
        Assert.Equal(IdentityResolutionFailureCode.MissingReference, failure.Code);
        Assert.Equal("Competitions[0].SportId", failure.Field);
        Assert.Empty(h.Allocated); Assert.Empty(h.Competitions); Assert.Equal(0, h.Saves);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
    }
    private sealed class Harness : IFootballResolutionPreparationSession, IAdvisoryLockSession, ICompetitionPersistenceSession
    {
        public List<string> Events { get; } = [];
        public List<Competition> Competitions { get; } = [];
        public List<CompetitionExternalIdentity> Mappings { get; } = [];
        public List<Guid> Allocated { get; } = [];
        public Sport? Sport { get; set; } = new(SportId, "football", "Football");
        public TransactionSession Transaction { get; }
        public FootballResolutionTransaction Shell { get; }
        public FootballResolutionOrchestrator Orchestrator { get; }
        public int Saves { get; private set; }
        private int reads;
        public Func<int, CancellationToken, Task<FootballDiscovery>> Read { get; set; } = (_, _) => Task.FromResult(Empty());
        public Action<string>? OnStage { get; set; }
        public Exception? SaveError { get; set; }
        public Harness()
        {
            Transaction = new(Events); Shell = new(Transaction, TimeSpan.FromSeconds(5));
            Orchestrator = new(Shell, this, new FootballIdentityMaterializer(() => { Events.Add("allocate"); var id = Guid.NewGuid(); Allocated.Add(id); return id; }), this);
        }
        public Task<IdentityResolutionBatchResult> Run(IdentityResolutionBatch? batch = null, CancellationToken token = default) => Orchestrator.PersistCompetitionsAsync(batch ?? Batch(), token);
        public Task<FootballDiscovery> ReadAsync(CancellationToken token) { Events.Add($"read:{++reads}"); return Read(reads, token); }
        public Task AcquireAsync(Func<CancellationToken, Task<FootballAdvisoryLockPlan>> discover, CancellationToken token)
            => PostgreSqlAdvisoryLockAcquirer.AcquireDiscoveredAsync(this, new AdvisoryLockKeys().FootballCoordination(), discover, TimeSpan.FromSeconds(5), token);
        public Task<string> GetLockTimeoutAsync(CancellationToken token) => Task.FromResult("0");
        public Task SetLockTimeoutAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task AcquireLockAsync(long key, CancellationToken token) { token.ThrowIfCancellationRequested(); Events.Add($"lock:{key}"); return Task.CompletedTask; }
        private void Stage(string value) { Events.Add(value); OnStage?.Invoke(value); }
        public Task<Sport?> LoadSportAsync(Guid id, CancellationToken token) { Stage("load"); token.ThrowIfCancellationRequested(); return Task.FromResult(Sport); }
        public void StageCompetition(Competition competition) { Competitions.Add(competition); Stage("competition"); }
        public void StageMapping(CompetitionExternalIdentity mapping) { Mappings.Add(mapping); Stage("mapping"); }
        public Task SaveChangesAsync(CancellationToken token) { Saves++; Stage("save"); token.ThrowIfCancellationRequested(); if (SaveError is not null) throw SaveError; Stage("postsave"); return Task.CompletedTask; }
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
