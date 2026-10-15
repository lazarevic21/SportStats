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

public sealed class MatchPersistenceTests
{
    private static readonly Guid SportId = Guid.NewGuid(), CompetitionId = Guid.NewGuid(), SeasonId = Guid.NewGuid(), TeamId = Guid.NewGuid(), AwayId = Guid.NewGuid(), MatchId = Guid.NewGuid();
    private static ProviderIdentity Identity(string id = " opaque match ") => new("provider", "scope", id);
    private static NormalizedSeason InputSeason => new(Identity("season"), 2026, "2026", new(2026, 8, 1), new(2027, 5, 31));
    private static NormalizedMatch Input => new(Identity(), InputSeason, Identity("home"), Identity("away"), new DateTime(2026, 9, 1, 18, 0, 0, DateTimeKind.Utc), FootballMatchStatus.Finished, "FINISHED", 3, new(ScoreFieldPresence.Value, 0), new(ScoreFieldPresence.Value, 2));
    private static IdentityResolutionBatch Batch() => new([], [], [], [new(new EntityReference<Competition>.Internal(CompetitionId), Input)]);
    private static FootballDiscovery Empty() => new([new(SportId, "football")], [new(CompetitionId, SportId, "PL")], [new(SeasonId, CompetitionId, "2026")], [TeamId, AwayId], [(SeasonId, TeamId), (SeasonId, AwayId)], [], [new(IdentityLockEntity.Season, Identity("season"), SeasonId), new(IdentityLockEntity.FootballTeam, Identity("home"), TeamId), new(IdentityLockEntity.FootballTeam, Identity("away"), AwayId)]);
    private static FootballDiscovery Mapped() => Empty() with { Matches = [new(MatchId, SeasonId, TeamId, AwayId)], Mappings = [.. Empty().Mappings, new(IdentityLockEntity.FootballMatch, Identity(), MatchId)] };

    [Fact]
    public async Task CreatesMatchesAndMappingsInRequestOrderWithInitialMetadata()
    {
        var h = new Harness(); var second = Input with { Identity = Identity("second"), Matchday = 4 };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(Batch() with { Matches = [Batch().Matches[0], Batch().Matches[0] with { Match = second }] }));
        Assert.Equal(new[] { Identity(), second.Identity }, result.Matches.Select(x => x.Identity));
        Assert.Equal(h.Matches.Select(x => x.Id), result.Matches.Select(x => x.EntityId));
        Assert.All(result.Matches, x => Assert.Equal(IdentityResolutionDisposition.CreatedEntity, x.Disposition));
        Assert.Equal(2, h.Mappings.Count); Assert.Equal(4, h.Allocated.Count); Assert.Equal(1, h.Saves);
        var match = h.Matches[0]; Assert.Equal(Input.KickoffUtc, match.KickoffUtc); Assert.Equal(Input.Status, match.Status); Assert.Equal(3, match.Matchday);
        Assert.Equal(0, match.HomeGoals); Assert.Equal(2, match.AwayGoals); Assert.Same(h.Season, match.Season);
        Assert.Same(h.Memberships[TeamId]!.Team, match.HomeTeam); Assert.Same(h.Memberships[AwayId]!.Team, match.AwayTeam);
        Assert.Equal(match.Id, h.Mappings[0].MatchId); Assert.Equal(Identity().ExternalId, h.Mappings[0].ExternalId);
        Assert.True(h.Events.IndexOf("read:2") < h.Events.IndexOf("load-season")); Assert.Equal(new[] { "clear", "release" }, h.Events.TakeLast(2));
    }

    [Theory]
    [InlineData(ScoreFieldPresence.Missing)]
    [InlineData(ScoreFieldPresence.Null)]
    public async Task MissingAndNullScoresRemainNull(ScoreFieldPresence presence)
    {
        var h = new Harness(); await h.Run(Batch() with { Matches = [Batch().Matches[0] with { Match = Input with { HomeGoals = new(presence, null), AwayGoals = new(presence, null) } }] });
        Assert.Null(Assert.Single(h.Matches).HomeGoals); Assert.Null(h.Matches[0].AwayGoals);
    }

    [Fact]
    public async Task ExactReplayPreservesMetadataWithoutLoadsAllocationsOrWrites()
    {
        var h = new Harness { Read = (_, _) => Task.FromResult(Mapped()) };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(Batch() with { Matches = [Batch().Matches[0] with { Match = Input with { KickoffUtc = Input.KickoffUtc.AddDays(9), Status = FootballMatchStatus.Scheduled, Matchday = 99, HomeGoals = new(ScoreFieldPresence.Value, 9) } }] }));
        Assert.Equal(MatchId, Assert.Single(result.Matches).EntityId); Assert.Equal(IdentityResolutionDisposition.ExistingMapping, result.Matches[0].Disposition);
        Assert.Empty(h.Allocated); Assert.Empty(h.Matches); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves); Assert.DoesNotContain("load-season", h.Events); Assert.DoesNotContain("load-membership", h.Events);
    }

    [Fact]
    public async Task TrustedAliasesReuseCanonicalMatch()
    {
        var alias = new ProviderIdentity("other", "other", "alias"); var state = Mapped() with { Mappings = [.. Mapped().Mappings, new(IdentityLockEntity.FootballMatch, alias, MatchId)] };
        var h = new Harness { Read = (_, _) => Task.FromResult(state) };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(Batch() with { Matches = [Batch().Matches[0] with { Match = Input with { Identity = alias } }, Batch().Matches[0]] }));
        Assert.Equal(new[] { alias, Identity() }, result.Matches.Select(x => x.Identity)); Assert.All(result.Matches, x => Assert.Equal(MatchId, x.EntityId)); Assert.Empty(h.Allocated); Assert.Equal(0, h.Saves);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("scope")]
    [InlineData("id")]
    public async Task IdenticalFixtureMetadataDoesNotMergeDifferentExternalIdentities(string part)
    {
        var identity = part switch { "provider" => Identity() with { ProviderKey = "other" }, "scope" => Identity() with { IdentityScope = "other" }, _ => Identity("other") };
        var h = new Harness { Read = (_, _) => Task.FromResult(Mapped()) };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(Batch() with { Matches = [Batch().Matches[0], Batch().Matches[0] with { Match = Input with { Identity = identity } }] }));
        Assert.Equal(MatchId, result.Matches[0].EntityId); Assert.NotEqual(MatchId, result.Matches[1].EntityId); Assert.Single(h.Matches); Assert.Single(h.Mappings); Assert.Equal(1, h.Saves);
    }

    [Theory]
    [InlineData("duplicate", IdentityResolutionFailureCode.DuplicateMappings)]
    [InlineData("ambiguous", IdentityResolutionFailureCode.AmbiguousMappings)]
    [InlineData("dangling", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("season", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("team", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("membership", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("same-team", IdentityResolutionFailureCode.RelationshipConflict)]
    [InlineData("match-season", IdentityResolutionFailureCode.RelationshipConflict)]
    [InlineData("home", IdentityResolutionFailureCode.RelationshipConflict)]
    [InlineData("away", IdentityResolutionFailureCode.RelationshipConflict)]
    [InlineData("implicit-parent", IdentityResolutionFailureCode.MissingReference)]
    public async Task InvalidDiscoveryNeverAllocatesOrWrites(string scenario, IdentityResolutionFailureCode code)
    {
        var state = scenario switch
        {
            "duplicate" => Mapped() with { Mappings = [.. Mapped().Mappings, new(IdentityLockEntity.FootballMatch, Identity(), MatchId)] },
            "ambiguous" => Mapped() with { Mappings = [.. Mapped().Mappings, new(IdentityLockEntity.FootballMatch, Identity(), Guid.NewGuid())] },
            "dangling" => Mapped() with { Matches = [] }, "season" => Empty() with { Seasons = [] }, "team" => Empty() with { Teams = [TeamId] },
            "membership" => Empty() with { Memberships = [(SeasonId, TeamId)] },
            "same-team" => Empty() with { Mappings = Empty().Mappings.Select(x => x.Identity == Identity("away") ? x with { TargetId = TeamId } : x).ToArray() },
            "match-season" => Mapped() with { Matches = [new(MatchId, Guid.NewGuid(), TeamId, AwayId)] },
            "home" => Mapped() with { Matches = [new(MatchId, SeasonId, AwayId, AwayId)] },
            "away" => Mapped() with { Matches = [new(MatchId, SeasonId, TeamId, TeamId)] },
            _ => Empty() with { Seasons = [], Mappings = Empty().Mappings.Where(x => x.Kind != IdentityLockEntity.Season).ToArray() }
        };
        var h = new Harness { Read = (_, _) => Task.FromResult(state) };
        Assert.Equal(code, Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()).Failure.Code); Assert.Empty(h.Allocated); Assert.Empty(h.Matches); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves);
    }

    [Theory]
    [InlineData("season")]
    [InlineData("home")]
    [InlineData("away")]
    [InlineData("wrong-season")]
    [InlineData("wrong-team")]
    public async Task MissingOrWrongLoadedPrincipalsFailBeforeStaging(string scenario)
    {
        var h = new Harness();
        if (scenario == "season") h.Season = null;
        else if (scenario == "home") h.Memberships[TeamId] = null;
        else if (scenario == "away") h.Memberships[AwayId] = null;
        else if (scenario == "wrong-team") h.Memberships[TeamId] = h.Memberships[AwayId];
        else h.Memberships[TeamId] = new(new Season(Guid.NewGuid(), h.Season!.Competition, "other", new(2026, 1, 1), new(2027, 1, 1)), new FootballTeam(TeamId, "Home"));
        Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()); Assert.Empty(h.Matches); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves);
    }

    [Theory]
    [InlineData("Competitions")]
    [InlineData("Seasons")]
    [InlineData("Teams")]
    public async Task UnsupportedRequestsFailBeforeTransactionInspection(string field)
    {
        var batch = Batch() with { Competitions = field == "Competitions" ? [new(SportId, new(Identity("competition"), "PL", "League"))] : [], Seasons = field == "Seasons" ? [new(new EntityReference<Competition>.Internal(CompetitionId), InputSeason)] : [], Teams = field == "Teams" ? [new(new EntityReference<Season>.Internal(SeasonId), new(Identity("team"), "Team", null, null))] : [] };
        var h = new Harness(); var failure = Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run(batch)).Failure; Assert.Equal(IdentityResolutionFailureCode.InvalidInput, failure.Code); Assert.Equal(field, failure.Field); Assert.Equal(0, h.Transaction.Inspections); Assert.Empty(h.Events);
    }

    [Fact]
    public async Task ChangedLockedPlanStopsBeforeAllocation()
    {
        var h = new Harness { Read = (n, _) => Task.FromResult(n == 1 ? Empty() : Mapped()) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run()); Assert.Empty(h.Allocated); Assert.Empty(h.Matches); Assert.Contains("rollback", h.Events);
    }

    [Theory]
    [InlineData("load-season")]
    [InlineData("load-membership")]
    [InlineData("match")]
    [InlineData("mapping")]
    [InlineData("save")]
    [InlineData("postsave")]
    public async Task CancellationRollsBackAndClearsTracking(string stage)
    {
        using var cts = new CancellationTokenSource(); var h = new Harness { OnStage = s => { if (s == stage) cts.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token)); Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3)); Assert.False(h.Shell.RequiresCallerRollback);
    }

    [Fact]
    public async Task PreCancellationDoesNotInspectTransaction()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel(); var h = new Harness(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token)); Assert.Equal(0, h.Transaction.Inspections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailurePreservedAndCleanupFailureRequiresCallerRollback(bool cleanupFails)
    {
        var h = new Harness { SaveError = new IOException("save failed") }; h.Transaction.FailRollback = cleanupFails;
        if (cleanupFails) Assert.Same(h.SaveError, (await Assert.ThrowsAsync<FootballResolutionCleanupException>(() => h.Run())).OperationException);
        else Assert.Same(h.SaveError, await Assert.ThrowsAsync<IOException>(() => h.Run()));
        Assert.Equal(cleanupFails, h.Shell.RequiresCallerRollback); Assert.Contains("clear", h.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationActuallyClearsEfTracker(bool cancellation)
    {
        using var context = Context(); using var cts = new CancellationTokenSource(); var h = new Harness { SaveError = new IOException("save failed") }; h.Transaction.Context = context;
        h.OnStage = stage => { if (stage != "match") return; context.Entry(Assert.Single(h.Matches)).State = EntityState.Added; if (cancellation) cts.Cancel(); };
        if (cancellation) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token)); else await Assert.ThrowsAsync<IOException>(() => h.Run());
        Assert.Empty(context.ChangeTracker.Entries()); Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void EfStagingKeepsExistingGraphUnchangedAndConnectionClosed()
    {
        using var context = Context(); var h = new Harness(); var season = h.Season!; context.Attach(season); foreach (var member in h.Memberships.Values) context.Attach(member!);
        var match = new FootballMatch(Guid.NewGuid(), season, h.Memberships[TeamId]!, h.Memberships[AwayId]!); var mapping = new FootballMatchExternalIdentity(Guid.NewGuid(), "provider", "scope", "id", match.Id);
        var session = new EfMatchPersistenceSession(context); session.StageMatch(match); session.StageMapping(mapping);
        Assert.Equal(EntityState.Added, context.Entry(match).State); Assert.Equal(EntityState.Added, context.Entry(mapping).State);
        Assert.All(context.ChangeTracker.Entries().Where(x => x.Entity != match && x.Entity != mapping), x => Assert.Equal(EntityState.Unchanged, x.State)); Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }
    [Fact]
    public async Task DuplicateRequestIsInvalidBeforeTransactionInspection()
    {
        var h = new Harness(); var result = Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run(Batch() with { Matches = [Batch().Matches[0], Batch().Matches[0]] }));
        Assert.Equal(IdentityResolutionFailureCode.InvalidInput, result.Failure.Code); Assert.Equal(0, h.Transaction.Inspections); Assert.Empty(h.Events);
    }

    [Fact]
    public async Task EmptyBatchDoesNotAllocateLoadOrSave()
    {
        var h = new Harness(); var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(new([], [], [], [])));
        Assert.Empty(result.Matches); Assert.Empty(h.Allocated); Assert.DoesNotContain("load-season", h.Events); Assert.Equal(0, h.Saves);
    }

    [Fact]
    public async Task ReplayWithinCallerTransactionDoesNotWriteTwice()
    {
        var h = new Harness(); h.Read = (_, _) => Task.FromResult(h.Saves == 0 ? Empty() : Empty() with
        {
            Matches = h.Matches.Select(x => new DiscoveredMatch(x.Id, x.SeasonId, x.HomeTeamId, x.AwayTeamId)).ToArray(),
            Mappings = [.. Empty().Mappings, .. h.Mappings.Select(x => new DiscoveredMapping(IdentityLockEntity.FootballMatch, new(x.ProviderKey, x.IdentityScope, x.ExternalId), x.MatchId))]
        });
        var first = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run()); var second = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run());
        Assert.Equal(first.Matches[0].EntityId, second.Matches[0].EntityId); Assert.Equal(IdentityResolutionDisposition.ExistingMapping, second.Matches[0].Disposition); Assert.Single(h.Matches); Assert.Single(h.Mappings); Assert.Equal(1, h.Saves); Assert.Equal(2, h.Allocated.Count);
    }

    [Fact]
    public async Task InvalidLateMembershipLeavesEntireBatchUnstaged()
    {
        var third = Guid.NewGuid(); var identity = Identity("third"); var state = Empty() with { Teams = [TeamId, AwayId, third], Memberships = [(SeasonId, TeamId), (SeasonId, AwayId), (SeasonId, third)], Mappings = [.. Empty().Mappings, new(IdentityLockEntity.FootballTeam, identity, third)] };
        var h = new Harness { Read = (_, _) => Task.FromResult(state) };
        var result = Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run(Batch() with { Matches = [Batch().Matches[0], Batch().Matches[0] with { Match = Input with { Identity = Identity("second"), AwayTeam = identity } }] }));
        Assert.Equal(IdentityResolutionFailureCode.MissingReference, result.Failure.Code); Assert.Empty(h.Matches); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves);
    }

    [Theory]
    [InlineData("sport")]
    [InlineData("competition")]
    [InlineData("season-id")]
    [InlineData("missing-graph")]
    public async Task InvalidLoadedSeasonGraphIsRejected(string scenario)
    {
        var h = new Harness();
        if (scenario == "missing-graph")
        {
            h.Season = (Season)Activator.CreateInstance(typeof(Season), nonPublic: true)!; typeof(Season).GetProperty(nameof(Season.Id))!.SetValue(h.Season, SeasonId);
        }
        else h.Season = new(scenario == "season-id" ? Guid.NewGuid() : SeasonId, new Competition(scenario == "competition" ? Guid.NewGuid() : CompetitionId, new Sport(SportId, scenario == "sport" ? "basketball" : "football", "Sport"), "PL", "Competition"), "2026", new(2026, 8, 1), new(2027, 5, 31));
        Assert.Equal(IdentityResolutionFailureCode.RelationshipConflict, Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()).Failure.Code); Assert.Empty(h.Matches); Assert.Empty(h.Mappings); Assert.Equal(0, h.Saves);
    }
    private static SportStatsDbContext Context() => new(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
    private sealed class Harness : IFootballResolutionPreparationSession, IAdvisoryLockSession, IMatchPersistenceSession
    {
        public List<string> Events { get; } = [];
        public List<FootballMatch> Matches { get; } = [];
        public List<FootballMatchExternalIdentity> Mappings { get; } = [];
        public Dictionary<Guid, FootballSeasonTeam?> Memberships { get; } = [];
        public List<Guid> Allocated { get; } = [];
        public Season? Season { get; set; } = new(SeasonId, new Competition(CompetitionId, new Sport(SportId, "football", "Football"), "PL", "Existing competition"), "2026", new(2026, 8, 1), new(2027, 5, 31));
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
            Memberships[TeamId] = new(Season!, new FootballTeam(TeamId, "Home"));
            Memberships[AwayId] = new(Season!, new FootballTeam(AwayId, "Away"));
            Transaction = new(Events); Shell = new(Transaction, TimeSpan.FromSeconds(5));
            Orchestrator = new(Shell, this, new FootballIdentityMaterializer(() => { Events.Add("allocate"); var id = Guid.NewGuid(); Allocated.Add(id); return id; }), matchPersistence: this);
        }
        public Task<IdentityResolutionBatchResult> Run(IdentityResolutionBatch? batch = null, CancellationToken token = default) => Orchestrator.PersistMatchesAsync(batch ?? Batch(), token);
        public Task<FootballDiscovery> ReadAsync(CancellationToken token) { Events.Add($"read:{++reads}"); return Read(reads, token); }
        public Task AcquireAsync(Func<CancellationToken, Task<FootballAdvisoryLockPlan>> discover, CancellationToken token)
            => PostgreSqlAdvisoryLockAcquirer.AcquireDiscoveredAsync(this, new AdvisoryLockKeys().FootballCoordination(), discover, TimeSpan.FromSeconds(5), token);
        public Task<string> GetLockTimeoutAsync(CancellationToken token) => Task.FromResult("0");
        public Task SetLockTimeoutAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task AcquireLockAsync(long key, CancellationToken token) { token.ThrowIfCancellationRequested(); Events.Add($"lock:{key}"); return Task.CompletedTask; }
        private void Stage(string value) { Events.Add(value); OnStage?.Invoke(value); }
        public Task<Season?> LoadSeasonAsync(Guid id, CancellationToken token) { Stage("load-season"); token.ThrowIfCancellationRequested(); return Task.FromResult(Season); }
        public Task<FootballSeasonTeam?> LoadMembershipAsync(Guid seasonId, Guid teamId, CancellationToken token) { Stage("load-membership"); token.ThrowIfCancellationRequested(); return Task.FromResult(Memberships.GetValueOrDefault(teamId)); }
        public void StageMatch(FootballMatch match) { Matches.Add(match); Stage("match"); }
        public void StageMapping(FootballMatchExternalIdentity mapping) { Mappings.Add(mapping); Stage("mapping"); }
        public Task SaveChangesAsync(CancellationToken token) { Saves++; Stage("save"); token.ThrowIfCancellationRequested(); if (SaveError is not null) throw SaveError; Stage("postsave"); return Task.CompletedTask; }
    }

    private sealed class TransactionSession(List<string> events) : IResolutionTransactionSession
    {
        private readonly object handle = new(), identity = new(), connection = new();
        public int Inspections { get; private set; }
        public bool FailRollback { get; set; }
        public SportStatsDbContext? Context { get; set; }
        public ResolutionTransactionSnapshot Inspect() { Inspections++; return new(handle, identity, connection, connection, ConnectionState.Open, true, IsolationLevel.ReadCommitted, true); }
        public bool HasTrackedEntries => Context?.ChangeTracker.Entries().Any() ?? false;
        public bool HasPendingChanges => Context?.ChangeTracker.HasChanges() ?? false;
        public void ClearTracking() { Context?.ChangeTracker.Clear(); events.Add("clear"); }
        public Task CreateSavepointAsync(object transaction, string name, CancellationToken token) { events.Add("create"); return Task.CompletedTask; }
        public Task RollbackToSavepointAsync(object transaction, string name, CancellationToken token) { events.Add("rollback"); if (FailRollback) throw new IOException("rollback failed"); return Task.CompletedTask; }
        public Task ReleaseSavepointAsync(object transaction, string name, CancellationToken token) { events.Add("release"); return Task.CompletedTask; }
    }
}





