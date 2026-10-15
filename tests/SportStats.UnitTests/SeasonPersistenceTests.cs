using System.Data;
using Microsoft.EntityFrameworkCore;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;
using Xunit;

namespace SportStats.UnitTests;

public sealed class SeasonPersistenceTests
{
    private static readonly Guid SportId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid CompetitionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid SeasonId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static ProviderIdentity Identity(string id = " 000season opaque ") => new("provider", "scope", id);
    private static NormalizedSeason Input => new(Identity(), 2026, "2026", new(2026, 8, 1), new(2027, 5, 31));
    private static IdentityResolutionBatch Batch(bool external = false) => new([], [new(external
        ? new EntityReference<Competition>.External(Identity("competition"))
        : new EntityReference<Competition>.Internal(CompetitionId), Input)], [], []);
    private static FootballDiscovery Empty() => new([new(SportId, "football")], [new(CompetitionId, SportId, "PL")], [], [], [], [],
        [new(IdentityLockEntity.Competition, Identity("competition"), CompetitionId)]);
    private static FootballDiscovery Mapped() => Empty() with
    {
        Seasons = [new(SeasonId, CompetitionId, "2026")],
        Mappings = [.. Empty().Mappings, new(IdentityLockEntity.Season, Identity(), SeasonId)]
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatesSeasonWithAllocatedIdsAndExactMappingForExistingParent(bool external)
    {
        var h = new Harness();
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(Batch(external)));
        var season = Assert.Single(h.Seasons);
        var mapping = Assert.Single(h.Mappings);
        Assert.Same(h.Competition, season.Competition);
        Assert.Equal(CompetitionId, season.CompetitionId);
        Assert.Equal(Input.Key, season.Key);
        Assert.Equal(Input.StartDate, season.StartDate);
        Assert.Equal(Input.EndDate, season.EndDate);
        Assert.Equal(h.Allocated[0], season.Id);
        Assert.Equal(h.Allocated[1], mapping.Id);
        Assert.Equal(season.Id, mapping.SeasonId);
        Assert.Equal(Identity().ProviderKey, mapping.ProviderKey);
        Assert.Equal(Identity().IdentityScope, mapping.IdentityScope);
        Assert.Equal(Identity().ExternalId, mapping.ExternalId);
        var resolved = Assert.Single(result.Seasons);
        Assert.Equal(season.Id, resolved.EntityId);
        Assert.Equal(IdentityResolutionDisposition.CreatedEntity, resolved.Disposition);
        Assert.Empty(result.Competitions);
        Assert.Equal(1, h.Saves);
        Assert.True(h.Events.IndexOf("read:2") < h.Events.IndexOf("load"));
        Assert.Equal(new[] { "load", "season", "mapping", "save", "postsave", "clear", "release" }, h.Events.TakeLast(7));
    }

    [Fact]
    public async Task ExistingMappingPreservesMetadataWithoutLoadingAllocatingOrSaving()
    {
        var h = new Harness { Read = (_, _) => Task.FromResult(Mapped()) };
        var changed = Batch() with { Seasons = [Batch().Seasons[0] with { Season = Input with { StartDate = new(2026, 7, 1) } }] };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(changed));
        Assert.Equal(SeasonId, Assert.Single(result.Seasons).EntityId);
        Assert.Equal(IdentityResolutionDisposition.ExistingMapping, result.Seasons[0].Disposition);
        Assert.Empty(h.Seasons); Assert.Empty(h.Mappings); Assert.Empty(h.Allocated);
        Assert.DoesNotContain("load", h.Events); Assert.Equal(0, h.Saves);
    }

    [Fact]
    public async Task ExistingAliasesReturnCanonicalIdInRequestOrderWithoutWrites()
    {
        var alias = Identity("alias");
        var state = Mapped() with { Mappings = [.. Mapped().Mappings, new(IdentityLockEntity.Season, alias, SeasonId)] };
        var h = new Harness { Read = (_, _) => Task.FromResult(state) };
        var batch = Batch() with { Seasons = [Batch().Seasons[0] with { Season = Input with { Identity = alias } }, Batch().Seasons[0]] };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(batch));
        Assert.Equal(new[] { alias, Identity() }, result.Seasons.Select(s => s.Identity));
        Assert.All(result.Seasons, s => Assert.Equal(SeasonId, s.EntityId));
        Assert.Empty(h.Allocated); Assert.Empty(h.Seasons); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves);
    }

    [Fact]
    public async Task IncompatibleLoadedParentFailsBeforeStaging()
    {
        var h = new Harness { Competition = new Competition(CompetitionId, new Sport(SportId, "football", "Football"), "OTHER", "Other") };
        var failure = Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()).Failure;
        Assert.Equal(IdentityResolutionFailureCode.RelationshipConflict, failure.Code);
        Assert.Empty(h.Seasons); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
    }

    [Fact]
    public async Task ReplayWithinCallerTransactionReusesSeasonAndMapping()
    {
        var h = new Harness();
        h.Read = (_, _) => Task.FromResult(h.Saves == 0 ? Empty() : Empty() with
        {
            Seasons = h.Seasons.Select(s => new DiscoveredSeason(s.Id, s.CompetitionId, s.Key)).ToArray(),
            Mappings = [.. Empty().Mappings, .. h.Mappings.Select(m => new DiscoveredMapping(IdentityLockEntity.Season,
                new(m.ProviderKey, m.IdentityScope, m.ExternalId), m.SeasonId))]
        });
        var first = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run());
        var second = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run());
        Assert.Equal(first.Seasons[0].EntityId, second.Seasons[0].EntityId);
        Assert.Equal(IdentityResolutionDisposition.ExistingMapping, second.Seasons[0].Disposition);
        Assert.Single(h.Seasons); Assert.Single(h.Mappings); Assert.Equal(1, h.Saves); Assert.Equal(2, h.Allocated.Count);
    }

    [Theory]
    [InlineData("duplicate", IdentityResolutionFailureCode.DuplicateMappings)]
    [InlineData("ambiguous", IdentityResolutionFailureCode.AmbiguousMappings)]
    [InlineData("natural", IdentityResolutionFailureCode.NaturalKeyConflict)]
    [InlineData("missing", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("parent", IdentityResolutionFailureCode.RelationshipConflict)]
    public async Task ConflictsNeverAllocateStageOrSave(string scenario, IdentityResolutionFailureCode code)
    {
        var state = scenario switch
        {
            "duplicate" => Mapped() with { Mappings = [.. Mapped().Mappings, new(IdentityLockEntity.Season, Identity(), SeasonId)] },
            "ambiguous" => Mapped() with { Mappings = [.. Mapped().Mappings, new(IdentityLockEntity.Season, Identity(), Guid.NewGuid())] },
            "natural" => Mapped() with { Mappings = Empty().Mappings },
            "parent" => Mapped() with { Seasons = [new(SeasonId, Guid.NewGuid(), "2026")] },
            _ => Empty() with { Competitions = [] }
        };
        var h = new Harness { Read = (_, _) => Task.FromResult(state) };
        Assert.Equal(code, Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()).Failure.Code);
        Assert.Empty(h.Allocated); Assert.Empty(h.Seasons); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
    }

    [Theory]
    [InlineData("Competitions")]
    [InlineData("Teams")]
    [InlineData("Matches")]
    public async Task UnsupportedRequestsAreRejectedBeforeTransactionInspection(string field)
    {
        var batch = Batch() with
        {
            Competitions = field == "Competitions" ? [new(SportId, new(Identity("competition"), "PL", "Premier League"))] : [],
            Teams = field == "Teams" ? [new(new EntityReference<Season>.External(Identity()), new(Identity("team"), "Team", null, null))] : [],
            Matches = field == "Matches" ? [new(new EntityReference<Competition>.Internal(CompetitionId),
                new(Identity("match"), Input, Identity("home"), Identity("away"), DateTime.UtcNow,
                    FootballMatchStatus.Scheduled, "SCHEDULED", 1, new(ScoreFieldPresence.Missing, null), new(ScoreFieldPresence.Missing, null)))] : []
        };
        var h = new Harness();
        var failure = Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run(batch)).Failure;
        Assert.Equal(IdentityResolutionFailureCode.InvalidInput, failure.Code);
        Assert.Equal(field, failure.Field); Assert.Equal(0, h.Transaction.Inspections); Assert.Empty(h.Events);
    }

    [Fact]
    public async Task EmptyBatchSucceedsWithoutAllocatingLoadingOrSaving()
    {
        var h = new Harness();
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(new([], [], [], [])));
        Assert.Empty(result.Seasons); Assert.Empty(result.Competitions);
        Assert.Empty(h.Seasons); Assert.Empty(h.Mappings); Assert.Empty(h.Allocated);
        Assert.DoesNotContain("load", h.Events); Assert.Equal(0, h.Saves);
    }

    [Fact]
    public async Task PreCancellationDoesNotInspectTransactionOrWrite()
    {
        var h = new Harness();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token));
        Assert.Equal(0, h.Transaction.Inspections); Assert.Empty(h.Events);
    }

    [Fact]
    public async Task ChangedLockedPlanStopsBeforeAllocationOrWrites()
    {
        var h = new Harness { Read = (n, _) => Task.FromResult(n == 1 ? Empty() : Mapped()) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        Assert.Empty(h.Allocated); Assert.Empty(h.Seasons); Assert.Equal(0, h.Saves); Assert.Contains("rollback", h.Events);
    }

    [Fact]
    public async Task MissingLoadedParentFailsAndClearsTracking()
    {
        var h = new Harness { Competition = null };
        Assert.Equal(IdentityResolutionFailureCode.MissingReference, Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()).Failure.Code);
        Assert.Empty(h.Seasons); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
    }

    [Theory]
    [InlineData("load")]
    [InlineData("season")]
    [InlineData("mapping")]
    [InlineData("save")]
    [InlineData("postsave")]
    public async Task CancellationAtPersistenceStagesRollsBackAndClears(string stage)
    {
        using var cts = new CancellationTokenSource();
        var h = new Harness { OnStage = value => { if (stage == value) cts.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token));
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
        Assert.False(h.Shell.RequiresCallerRollback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailureRollsBackAndFailedCleanupQuarantines(bool cleanupFails)
    {
        var h = new Harness { SaveError = new IOException("save failed") };
        h.Transaction.FailRollback = cleanupFails;
        if (cleanupFails)
            Assert.Same(h.SaveError, (await Assert.ThrowsAsync<FootballResolutionCleanupException>(() => h.Run())).OperationException);
        else Assert.Same(h.SaveError, await Assert.ThrowsAsync<IOException>(() => h.Run()));
        Assert.Equal(cleanupFails, h.Shell.RequiresCallerRollback);
        Assert.Contains("rollback", h.Events); Assert.Contains("clear", h.Events);
    }

    [Fact]
    public void EfStagingPreservesExistingParentAndExactMappingOwnership()
    {
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var sport = new Sport(SportId, "football", "Football");
        var competition = new Competition(CompetitionId, sport, "PL", "Existing metadata");
        context.Attach(competition);
        var season = new Season(SeasonId, competition, Input.Key, Input.StartDate, Input.EndDate);
        var mapping = new SeasonExternalIdentity(Guid.NewGuid(), Identity().ProviderKey, Identity().IdentityScope, Identity().ExternalId, SeasonId);
        var adapter = new EfSeasonPersistenceSession(context);
        adapter.StageSeason(season); adapter.StageMapping(mapping);
        Assert.Equal(EntityState.Unchanged, context.Entry(sport).State);
        Assert.Equal(EntityState.Unchanged, context.Entry(competition).State);
        Assert.Equal(EntityState.Added, context.Entry(season).State);
        Assert.Equal(EntityState.Added, context.Entry(mapping).State);
        Assert.Equal(SeasonId, season.Id); Assert.Equal(CompetitionId, season.CompetitionId); Assert.Equal(SeasonId, mapping.SeasonId);
        Assert.Equal("Existing metadata", competition.Name);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    private sealed class Harness : IFootballResolutionPreparationSession, IAdvisoryLockSession, ISeasonPersistenceSession
    {
        public List<string> Events { get; } = [];
        public List<Season> Seasons { get; } = [];
        public List<SeasonExternalIdentity> Mappings { get; } = [];
        public List<Guid> Allocated { get; } = [];
        public Competition? Competition { get; set; } = new(CompetitionId, new Sport(SportId, "football", "Football"), "PL", "Existing metadata");
        public TransactionSession Transaction { get; }
        public FootballResolutionTransaction Shell { get; }
        private FootballResolutionOrchestrator Orchestrator { get; }
        public int Saves { get; private set; }
        private int reads;
        public Func<int, CancellationToken, Task<FootballDiscovery>> Read { get; set; } = (_, _) => Task.FromResult(Empty());
        public Action<string>? OnStage { get; set; }
        public Exception? SaveError { get; set; }
        public Harness()
        {
            Transaction = new(Events); Shell = new(Transaction, TimeSpan.FromSeconds(5));
            Orchestrator = new(Shell, this, new FootballIdentityMaterializer(() => { Events.Add("allocate"); var id = Guid.NewGuid(); Allocated.Add(id); return id; }), seasonPersistence: this);
        }
        public Task<IdentityResolutionBatchResult> Run(IdentityResolutionBatch? batch = null, CancellationToken token = default) => Orchestrator.PersistSeasonsAsync(batch ?? Batch(), token);
        public Task<FootballDiscovery> ReadAsync(CancellationToken token) { Events.Add($"read:{++reads}"); return Read(reads, token); }
        public Task AcquireAsync(Func<CancellationToken, Task<FootballAdvisoryLockPlan>> discover, CancellationToken token)
            => PostgreSqlAdvisoryLockAcquirer.AcquireDiscoveredAsync(this, new AdvisoryLockKeys().FootballCoordination(), discover, TimeSpan.FromSeconds(5), token);
        public Task<string> GetLockTimeoutAsync(CancellationToken token) => Task.FromResult("0");
        public Task SetLockTimeoutAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task AcquireLockAsync(long key, CancellationToken token) { token.ThrowIfCancellationRequested(); Events.Add($"lock:{key}"); return Task.CompletedTask; }
        private void Stage(string value) { Events.Add(value); OnStage?.Invoke(value); }
        public Task<Competition?> LoadCompetitionAsync(Guid id, CancellationToken token) { Assert.Equal(CompetitionId, id); Stage("load"); token.ThrowIfCancellationRequested(); return Task.FromResult(Competition); }
        public void StageSeason(Season season) { Seasons.Add(season); Stage("season"); }
        public void StageMapping(SeasonExternalIdentity mapping) { Mappings.Add(mapping); Stage("mapping"); }
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
