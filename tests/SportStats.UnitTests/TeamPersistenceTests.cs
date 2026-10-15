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

public sealed class TeamPersistenceTests
{
    private static readonly Guid SportId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid CompetitionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid SeasonId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid TeamId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static ProviderIdentity Identity(string id = " 000team opaque ") => new("provider", "scope", id);
    private static NormalizedTeam Input => new(Identity(), "Team", "TM", "https://example.test/crest.png");
    private static IdentityResolutionBatch Batch(bool external = false) => new([], [], [new(external
        ? new EntityReference<Season>.External(Identity("season")) : new EntityReference<Season>.Internal(SeasonId), Input)], []);
    private static FootballDiscovery Empty() => new([new(SportId, "football")], [new(CompetitionId, SportId, "PL")],
        [new(SeasonId, CompetitionId, "2026")], [], [], [], [new(IdentityLockEntity.Season, Identity("season"), SeasonId)]);
    private static FootballDiscovery Mapped(bool membership = true) => Empty() with
    {
        Teams = [TeamId], Memberships = membership ? [(SeasonId, TeamId)] : [],
        Mappings = [.. Empty().Mappings, new(IdentityLockEntity.FootballTeam, Identity(), TeamId)]
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatesCanonicalTeamMappingAndMembershipWithExistingSeason(bool external)
    {
        var h = new Harness();
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(Batch(external)));
        var team = Assert.Single(h.Teams); var mapping = Assert.Single(h.Mappings); var membership = Assert.Single(h.Memberships);
        Assert.Equal(h.Allocated[0], team.Id); Assert.Equal(h.Allocated[1], mapping.Id);
        Assert.Equal(Input.Name, team.Name); Assert.Equal(Input.ShortName, team.ShortName); Assert.Equal(Input.CrestUrl, team.CrestUrl);
        Assert.Equal(team.Id, mapping.TeamId); Assert.Equal(Identity().ProviderKey, mapping.ProviderKey);
        Assert.Equal(Identity().IdentityScope, mapping.IdentityScope); Assert.Equal(Identity().ExternalId, mapping.ExternalId);
        Assert.Same(team, membership.Team); Assert.Same(h.Season, membership.Season);
        Assert.Equal(SeasonId, membership.SeasonId); Assert.Equal(team.Id, membership.TeamId);
        Assert.Equal(team.Id, Assert.Single(result.Teams).EntityId);
        Assert.Equal(IdentityResolutionDisposition.CreatedEntity, result.Teams[0].Disposition);
        Assert.Empty(result.Competitions); Assert.Empty(result.Seasons); Assert.Empty(result.Matches);
        Assert.Equal(1, h.Saves); Assert.True(h.Events.IndexOf("read:2") < h.Events.IndexOf("load-season"));
        Assert.Equal(new[] { "clear", "release" }, h.Events.TakeLast(2));
    }

    [Fact]
    public async Task ExactReplayPreservesMetadataWithoutLoadingAllocatingOrSaving()
    {
        var h = new Harness { Read = (_, _) => Task.FromResult(Mapped()) };
        var batch = Batch() with { Teams = [Batch().Teams[0] with { Team = Input with { Name = "Changed", ShortName = "NEW", CrestUrl = null } }] };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(batch));
        Assert.Equal(TeamId, Assert.Single(result.Teams).EntityId);
        Assert.Equal(IdentityResolutionDisposition.ExistingMapping, result.Teams[0].Disposition);
        Assert.Empty(h.Teams); Assert.Empty(h.Mappings); Assert.Empty(h.Memberships); Assert.Empty(h.Allocated);
        Assert.DoesNotContain("load-season", h.Events); Assert.DoesNotContain("load-team", h.Events); Assert.Equal(0, h.Saves);
        Assert.Equal("Existing team metadata", h.Team!.Name);
    }

    [Fact]
    public async Task ExistingMappingAddsOnlyMissingMembershipAndPreservesMetadata()
    {
        var h = new Harness { Read = (_, _) => Task.FromResult(Mapped(false)) };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run());
        Assert.Equal(TeamId, Assert.Single(result.Teams).EntityId);
        Assert.Equal(IdentityResolutionDisposition.ExistingMapping, result.Teams[0].Disposition);
        Assert.Same(h.Team, Assert.Single(h.Memberships).Team); Assert.Empty(h.Teams); Assert.Empty(h.Mappings); Assert.Empty(h.Allocated);
        Assert.Equal("Existing team metadata", h.Team!.Name); Assert.Equal("OLD", h.Team.ShortName); Assert.Equal("old-crest", h.Team.CrestUrl);
        Assert.Equal(1, h.Saves);
    }

    [Fact]
    public async Task TrustedAliasesReturnCanonicalIdInRequestOrderAndDeduplicateMembership()
    {
        var alias = new ProviderIdentity("other-provider", "other-scope", "alias");
        var state = Mapped(false) with { Mappings = [.. Mapped(false).Mappings, new(IdentityLockEntity.FootballTeam, alias, TeamId)] };
        var h = new Harness { Read = (_, _) => Task.FromResult(state) };
        var batch = Batch() with { Teams = [Batch().Teams[0] with { Team = Input with { Identity = alias } }, Batch().Teams[0]] };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(batch));
        Assert.Equal(new[] { alias, Identity() }, result.Teams.Select(x => x.Identity));
        Assert.All(result.Teams, x => Assert.Equal(TeamId, x.EntityId));
        Assert.Single(h.Memberships); Assert.Empty(h.Teams); Assert.Empty(h.Mappings); Assert.Empty(h.Allocated); Assert.Equal(1, h.Saves);
        Assert.Equal(1, h.Events.Count(x => x == "load-team"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("provider")]
    [InlineData("scope")]
    public async Task SameNameDoesNotMergeDifferentExternalIdentities(string difference)
    {
        var other = difference switch { "provider" => Identity() with { ProviderKey = "other" }, "scope" => Identity() with { IdentityScope = "other" }, _ => Identity("other") };
        var h = new Harness { Read = (_, _) => Task.FromResult(Mapped()) };
        var batch = Batch() with { Teams = [Batch().Teams[0], Batch().Teams[0] with { Team = Input with { Identity = other } }] };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(batch));
        Assert.Equal(TeamId, result.Teams[0].EntityId); Assert.NotEqual(TeamId, result.Teams[1].EntityId);
        Assert.Equal(result.Teams[1].EntityId, Assert.Single(h.Teams).Id); Assert.Single(h.Mappings); Assert.Single(h.Memberships); Assert.Equal(1, h.Saves);
    }

    [Fact]
    public async Task ReplayWithinCallerTransactionDoesNotWriteTwice()
    {
        var h = new Harness();
        h.Read = (_, _) => Task.FromResult(h.Saves == 0 ? Empty() : Empty() with
        {
            Teams = h.Teams.Select(t => t.Id).ToArray(), Memberships = h.Memberships.Select(m => (m.SeasonId, m.TeamId)).ToArray(),
            Mappings = [.. Empty().Mappings, .. h.Mappings.Select(m => new DiscoveredMapping(IdentityLockEntity.FootballTeam, new(m.ProviderKey, m.IdentityScope, m.ExternalId), m.TeamId))]
        });
        var first = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run());
        var second = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run());
        Assert.Equal(first.Teams[0].EntityId, second.Teams[0].EntityId); Assert.Equal(IdentityResolutionDisposition.ExistingMapping, second.Teams[0].Disposition);
        Assert.Single(h.Teams); Assert.Single(h.Mappings); Assert.Single(h.Memberships); Assert.Equal(1, h.Saves); Assert.Equal(2, h.Allocated.Count);
    }

    [Theory]
    [InlineData("duplicate", IdentityResolutionFailureCode.DuplicateMappings)]
    [InlineData("ambiguous", IdentityResolutionFailureCode.AmbiguousMappings)]
    [InlineData("dangling", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("season", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("sport", IdentityResolutionFailureCode.RelationshipConflict)]
    [InlineData("league", IdentityResolutionFailureCode.RelationshipConflict)]
    public async Task DiscoveryConflictsNeverAllocateStageOrSave(string scenario, IdentityResolutionFailureCode code)
    {
        var state = scenario switch
        {
            "duplicate" => Mapped() with { Mappings = [.. Mapped().Mappings, new(IdentityLockEntity.FootballTeam, Identity(), TeamId)] },
            "ambiguous" => Mapped() with { Mappings = [.. Mapped().Mappings, new(IdentityLockEntity.FootballTeam, Identity(), Guid.NewGuid())] },
            "dangling" => Mapped() with { Teams = [] }, "season" => Empty() with { Seasons = [] },
            "sport" => Empty() with { Sports = [new(SportId, "basketball")] },
            _ => Empty() with { Competitions = [new(CompetitionId, SportId, "OTHER")] }
        };
        var h = new Harness { Read = (_, _) => Task.FromResult(state) };
        Assert.Equal(code, Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()).Failure.Code);
        Assert.Empty(h.Allocated); Assert.Empty(h.Teams); Assert.Empty(h.Mappings); Assert.Empty(h.Memberships); Assert.Equal(0, h.Saves);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
    }

    [Theory]
    [InlineData("Competitions")]
    [InlineData("Seasons")]
    [InlineData("Matches")]
    public async Task UnsupportedRequestsFailBeforeTransactionInspection(string field)
    {
        var season = new NormalizedSeason(Identity("season"), 2026, "2026", new(2026, 8, 1), new(2027, 5, 31));
        var batch = Batch() with
        {
            Competitions = field == "Competitions" ? [new(SportId, new(Identity("competition"), "PL", "Premier League"))] : [],
            Seasons = field == "Seasons" ? [new(new EntityReference<Competition>.Internal(CompetitionId), season)] : [],
            Matches = field == "Matches" ? [new(new EntityReference<Competition>.Internal(CompetitionId), new(Identity("match"), season, Identity("home"), Identity("away"), DateTime.UtcNow,
                FootballMatchStatus.Scheduled, "SCHEDULED", 1, new(ScoreFieldPresence.Missing, null), new(ScoreFieldPresence.Missing, null)))] : []
        };
        var h = new Harness(); var failure = Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run(batch)).Failure;
        Assert.Equal(IdentityResolutionFailureCode.InvalidInput, failure.Code); Assert.Equal(field, failure.Field);
        Assert.Equal(0, h.Transaction.Inspections); Assert.Empty(h.Events);
    }

    [Fact]
    public async Task EmptyBatchDoesNotLoadAllocateOrSave()
    {
        var h = new Harness(); var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(new([], [], [], [])));
        Assert.Empty(result.Teams); Assert.Empty(h.Allocated); Assert.Empty(h.Teams); Assert.Empty(h.Memberships); Assert.Equal(0, h.Saves);
        Assert.DoesNotContain("load-season", h.Events);
    }

    [Fact]
    public async Task PreCancellationDoesNotInspectTransaction()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel(); var h = new Harness();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token));
        Assert.Equal(0, h.Transaction.Inspections); Assert.Empty(h.Events);
    }

    [Fact]
    public async Task ChangedLockedPlanStopsBeforeAllocationAndWrites()
    {
        var h = new Harness { Read = (n, _) => Task.FromResult(n == 1 ? Empty() : Mapped()) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Run());
        Assert.Empty(h.Allocated); Assert.Empty(h.Teams); Assert.Equal(0, h.Saves); Assert.Contains("rollback", h.Events);
    }

    [Theory]
    [InlineData("season", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("team", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("sport", IdentityResolutionFailureCode.RelationshipConflict)]
    [InlineData("league", IdentityResolutionFailureCode.RelationshipConflict)]
    [InlineData("season-id", IdentityResolutionFailureCode.RelationshipConflict)]
    [InlineData("missing-graph", IdentityResolutionFailureCode.RelationshipConflict)]
    public async Task MissingOrIncompatibleLoadedPrincipalsFailBeforeStaging(string scenario, IdentityResolutionFailureCode code)
    {
        var h = new Harness { Read = (_, _) => Task.FromResult(Mapped(false)) };
        if (scenario == "season") h.Season = null;
        else if (scenario == "team") h.Team = null;
        else if (scenario == "missing-graph")
        {
            h.Season = (Season)Activator.CreateInstance(typeof(Season), nonPublic: true)!;
            typeof(Season).GetProperty(nameof(Season.Id))!.SetValue(h.Season, SeasonId);
        }
        else h.Season = new Season(scenario == "season-id" ? Guid.NewGuid() : SeasonId, new Competition(CompetitionId,
            new Sport(SportId, scenario == "sport" ? "basketball" : "football", "Sport"), scenario == "league" ? "OTHER" : "PL", "Competition"), "2026", new(2026, 8, 1), new(2027, 5, 31));
        Assert.Equal(code, Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run()).Failure.Code);
        Assert.Empty(h.Teams); Assert.Empty(h.Mappings); Assert.Empty(h.Memberships); Assert.Equal(0, h.Saves);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
    }

    [Theory]
    [InlineData("load-season")]
    [InlineData("team")]
    [InlineData("mapping")]
    [InlineData("membership")]
    [InlineData("save")]
    [InlineData("postsave")]
    public async Task CancellationDuringPersistenceRollsBackAndClearsTracking(string stage)
    {
        using var cts = new CancellationTokenSource(); var h = new Harness { OnStage = value => { if (stage == value) cts.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token));
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3)); Assert.False(h.Shell.RequiresCallerRollback);
    }

    [Fact]
    public async Task CancellationWhileLoadingExistingTeamRollsBackAndClears()
    {
        using var cts = new CancellationTokenSource();
        var h = new Harness { Read = (_, _) => Task.FromResult(Mapped(false)), OnStage = stage => { if (stage == "load-team") cts.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token));
        Assert.Empty(h.Memberships); Assert.Equal(0, h.Saves);
        Assert.Equal(new[] { "rollback", "release", "clear" }, h.Events.TakeLast(3));
    }

    [Fact]
    public async Task UnmappedExternalSeasonFailsWithoutAllocatingOrStaging()
    {
        var h = new Harness { Read = (_, _) => Task.FromResult(Empty() with { Mappings = [] }) };
        Assert.Equal(IdentityResolutionFailureCode.MissingReference, Assert.IsType<IdentityResolutionBatchResult.Failed>(await h.Run(Batch(true))).Failure.Code);
        Assert.Empty(h.Allocated); Assert.Empty(h.Teams); Assert.Empty(h.Mappings); Assert.Empty(h.Memberships); Assert.Equal(0, h.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailureRollsBackAndCleanupFailureRequiresCallerRollback(bool cleanupFails)
    {
        var h = new Harness { SaveError = new IOException("save failed") }; h.Transaction.FailRollback = cleanupFails;
        if (cleanupFails) Assert.Same(h.SaveError, (await Assert.ThrowsAsync<FootballResolutionCleanupException>(() => h.Run())).OperationException);
        else Assert.Same(h.SaveError, await Assert.ThrowsAsync<IOException>(() => h.Run()));
        Assert.Equal(cleanupFails, h.Shell.RequiresCallerRollback); Assert.Contains("rollback", h.Events); Assert.Contains("clear", h.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledPersistenceActuallyClearsEfTracker(bool cancellation)
    {
        using var cts = new CancellationTokenSource();
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var h = new Harness { SaveError = new IOException("save failed") };
        h.Transaction.Context = context;
        h.OnStage = stage =>
        {
            if (stage != "team") return;
            context.Entry(Assert.Single(h.Teams)).State = EntityState.Added;
            if (cancellation) cts.Cancel();
        };
        if (cancellation) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Run(token: cts.Token));
        else await Assert.ThrowsAsync<IOException>(() => h.Run());
        Assert.Empty(context.ChangeTracker.Entries()); Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Contains("rollback", h.Events); Assert.Contains("clear", h.Events);
    }

    [Fact]
    public async Task SameMappedTeamParticipatesInSeparateSeasonsWithoutRecreation()
    {
        var h = new Harness { Read = (_, _) => Task.FromResult(Mapped(false)) };
        await h.Run();
        var nextSeasonId = Guid.NewGuid();
        h.Season = new Season(nextSeasonId, h.Season!.Competition, "2027", new(2027, 8, 1), new(2028, 5, 31));
        h.Read = (_, _) => Task.FromResult(Mapped(false) with
        {
            Seasons = [new(SeasonId, CompetitionId, "2026"), new(nextSeasonId, CompetitionId, "2027")],
            Memberships = [(SeasonId, TeamId)]
        });
        var batch = Batch() with { Teams = [Batch().Teams[0] with { Season = new EntityReference<Season>.Internal(nextSeasonId) }] };
        var result = Assert.IsType<IdentityResolutionBatchResult.Resolved>(await h.Run(batch));
        Assert.Equal(TeamId, Assert.Single(result.Teams).EntityId); Assert.Empty(h.Teams); Assert.Empty(h.Mappings); Assert.Empty(h.Allocated);
        Assert.Equal(new[] { SeasonId, nextSeasonId }, h.Memberships.Select(m => m.SeasonId));
        Assert.All(h.Memberships, m => Assert.Same(h.Team, m.Team)); Assert.Equal(2, h.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EfStagingKeepsExistingPrincipalsUnchangedAndConnectionClosed(bool existingTeam)
    {
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var sport = new Sport(SportId, "football", "Football"); var competition = new Competition(CompetitionId, sport, "PL", "Existing competition");
        var season = new Season(SeasonId, competition, "2026", new(2026, 8, 1), new(2027, 5, 31));
        var team = new FootballTeam(TeamId, "Existing team"); context.Attach(season); if (existingTeam) context.Attach(team);
        var session = new EfTeamPersistenceSession(context); if (!existingTeam) session.StageTeam(team);
        var mapping = new FootballTeamExternalIdentity(Guid.NewGuid(), "provider", "scope", "opaque", TeamId);
        var membership = new FootballSeasonTeam(season, team); session.StageMapping(mapping); session.StageMembership(membership);
        Assert.Equal(EntityState.Unchanged, context.Entry(sport).State); Assert.Equal(EntityState.Unchanged, context.Entry(competition).State);
        Assert.Equal(EntityState.Unchanged, context.Entry(season).State); Assert.Equal(existingTeam ? EntityState.Unchanged : EntityState.Added, context.Entry(team).State);
        Assert.Equal(EntityState.Added, context.Entry(mapping).State); Assert.Equal(EntityState.Added, context.Entry(membership).State);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    private sealed class Harness : IFootballResolutionPreparationSession, IAdvisoryLockSession, ITeamPersistenceSession
    {
        public List<string> Events { get; } = [];
        public List<FootballTeam> Teams { get; } = [];
        public List<FootballTeamExternalIdentity> Mappings { get; } = [];
        public List<FootballSeasonTeam> Memberships { get; } = [];
        public List<Guid> Allocated { get; } = [];
        public Season? Season { get; set; } = new(SeasonId, new Competition(CompetitionId, new Sport(SportId, "football", "Football"), "PL", "Existing competition"), "2026", new(2026, 8, 1), new(2027, 5, 31));
        public FootballTeam? Team { get; set; } = new(TeamId, "Existing team metadata", "OLD", "old-crest");
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
            Orchestrator = new(Shell, this, new FootballIdentityMaterializer(() => { Events.Add("allocate"); var id = Guid.NewGuid(); Allocated.Add(id); return id; }), teamPersistence: this);
        }
        public Task<IdentityResolutionBatchResult> Run(IdentityResolutionBatch? batch = null, CancellationToken token = default) => Orchestrator.PersistTeamsAsync(batch ?? Batch(), token);
        public Task<FootballDiscovery> ReadAsync(CancellationToken token) { Events.Add($"read:{++reads}"); return Read(reads, token); }
        public Task AcquireAsync(Func<CancellationToken, Task<FootballAdvisoryLockPlan>> discover, CancellationToken token)
            => PostgreSqlAdvisoryLockAcquirer.AcquireDiscoveredAsync(this, new AdvisoryLockKeys().FootballCoordination(), discover, TimeSpan.FromSeconds(5), token);
        public Task<string> GetLockTimeoutAsync(CancellationToken token) => Task.FromResult("0");
        public Task SetLockTimeoutAsync(string value, CancellationToken token) => Task.CompletedTask;
        public Task AcquireLockAsync(long key, CancellationToken token) { token.ThrowIfCancellationRequested(); Events.Add($"lock:{key}"); return Task.CompletedTask; }
        private void Stage(string value) { Events.Add(value); OnStage?.Invoke(value); }
        public Task<Season?> LoadSeasonAsync(Guid id, CancellationToken token) { Stage("load-season"); token.ThrowIfCancellationRequested(); return Task.FromResult(Season); }
        public Task<FootballTeam?> LoadTeamAsync(Guid id, CancellationToken token) { Assert.Equal(TeamId, id); Stage("load-team"); token.ThrowIfCancellationRequested(); return Task.FromResult(Team); }
        public void StageTeam(FootballTeam team) { Teams.Add(team); Stage("team"); }
        public void StageMapping(FootballTeamExternalIdentity mapping) { Mappings.Add(mapping); Stage("mapping"); }
        public void StageMembership(FootballSeasonTeam membership) { Memberships.Add(membership); Stage("membership"); }
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
