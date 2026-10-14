using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;
using Xunit;

namespace SportStats.UnitTests;

public sealed class FootballIdentityMaterializerTests
{
    private static readonly Guid SportId = Guid.NewGuid();
    private static ProviderIdentity Identity(string id) => new("Provider ", "Scope ", id);
    private static NormalizedSeason Season => new(Identity("season"), 2026, "2026", new(2026, 8, 1), new(2027, 5, 31));
    private static EntityReference<Competition> Competition => new EntityReference<Competition>.External(Identity("competition"));
    private static IdentityResolutionBatch Batch => new(
        [new(SportId, new(Identity("competition"), "PL", "Premier League"))],
        [new(Competition, Season)],
        [new(new EntityReference<Season>.External(Season.Identity), new(Identity("home"), "Home", null, null)),
         new(new EntityReference<Season>.External(Season.Identity), new(Identity("away"), "Away", null, null))],
        [new(Competition, new(Identity("match"), Season, Identity("home"), Identity("away"),
            new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc), FootballMatchStatus.Scheduled, "SCHEDULED", 1,
            new(ScoreFieldPresence.Missing, null), new(ScoreFieldPresence.Missing, null)))]);
    private static FootballDiscovery Empty => new([new(SportId, "football")], [], [], [], [], [], []);
    private static FootballResolutionPlan Plan(IdentityResolutionBatch? batch = null, FootballDiscovery? discovery = null)
    {
        var result = new FootballResolutionPlanner().Plan(batch ?? Batch, discovery ?? Empty);
        Assert.Null(result.Failure);
        return Assert.IsType<FootballResolutionPlan>(result.Plan);
    }

    [Fact]
    public void SymbolicReferencesAllocateOnceAndDependenciesShareCanonicalHandles()
    {
        var allocations = new List<Guid>();
        var result = new FootballIdentityMaterializer(() => { var id = Guid.NewGuid(); allocations.Add(id); return id; }).Materialize(Plan());
        Assert.Equal(5, result.Entities.Count);
        Assert.Equal(10, allocations.Count);
        Assert.Equal(10, allocations.Distinct().Count());
        Assert.Same(result.Competitions[0].Entity, result.Seasons[0].Parent);
        Assert.Same(result.Seasons[0].Entity, result.Matches[0].Parent);
        Assert.Same(result.Teams[0].Entity, result.Matches[0].Home);
        Assert.Same(result.Teams[1].Entity, result.Matches[0].Away);
        Assert.All(result.Memberships, item => Assert.Same(result.Seasons[0].Entity, item.Season));
        Assert.Equal(result.Teams.Select(t => t.Entity.Id), result.Memberships.Select(m => m.Team.Id));
        Assert.All(result.NewMappings, mapping => Assert.Contains(result.Entities.Values, e => e.Id == mapping.EntityId && e.Kind == mapping.Kind));
        Assert.Empty(result.NewMappings.Select(m => m.Id).Intersect(result.Entities.Values.Select(e => e.Id)));
    }

    [Fact]
    public void EqualSymbolicReferencesAndRepeatedMappingsAreDeduplicated()
    {
        var plan = Plan();
        var first = plan.Teams[0];
        plan = plan with { Teams = [first, first with { Entity = first.Entity with { } }, plan.Teams[1]] };
        var allocations = 0;
        var result = new FootballIdentityMaterializer(() => { allocations++; return Guid.NewGuid(); }).Materialize(plan);
        Assert.Same(result.Teams[0].Entity, result.Teams[1].Entity);
        Assert.Equal(5, result.Entities.Count);
        Assert.Equal(5, result.NewMappings.Count);
        Assert.Equal(10, allocations);
    }

    [Fact]
    public void ExistingAliasesRetainIdsAndCanonicalHandlesWithoutAllocations()
    {
        var id = Guid.NewGuid();
        var batch = Batch with { Seasons = [], Teams = [], Matches = [] };
        var alias = batch.Competitions[0] with { Competition = batch.Competitions[0].Competition with { Identity = Identity("alias") } };
        batch = batch with { Competitions = [batch.Competitions[0], alias] };
        var discovery = Empty with { Competitions = [new(id, SportId, "PL")], Mappings = [new(IdentityLockEntity.Competition, Identity("competition"), id), new(IdentityLockEntity.Competition, Identity("alias"), id)] };
        var result = new FootballIdentityMaterializer(() => throw new Xunit.Sdk.XunitException("Existing entities must not allocate IDs")).Materialize(Plan(batch, discovery));
        Assert.Single(result.Entities);
        Assert.Equal(id, result.Competitions[0].Entity.Id);
        Assert.Same(result.Competitions[0].Entity, result.Competitions[1].Entity);
        Assert.Empty(result.NewMappings);
    }

    [Fact]
    public void MembershipsDeduplicateCompositeIdsEvenWithSharedIdAcrossEntityTypes()
    {
        var id = Guid.NewGuid();
        var season = new PlannedEntity(IdentityLockEntity.Season, id, null);
        var team = new PlannedEntity(IdentityLockEntity.FootballTeam, id, null);
        var plan = Plan() with { Competitions = [], Seasons = [], Teams = [], Matches = [], Memberships = [new(season, team, true), new(season with { }, team with { }, true)] };
        var result = new FootballIdentityMaterializer().Materialize(plan);
        Assert.Equal(2, result.Entities.Count);
        var membership = Assert.Single(result.Memberships);
        Assert.NotSame(membership.Season, membership.Team);
        Assert.True(membership.Exists);
    }

    [Fact]
    public void ConflictingMembershipDecisionsAreRejected()
    {
        var plan = Plan();
        plan = plan with { Memberships = [plan.Memberships[0], plan.Memberships[0] with { Exists = true }] };
        Assert.Throws<InvalidOperationException>(() => new FootballIdentityMaterializer().Materialize(plan));
    }

    [Fact]
    public void OpaqueIdentitiesAndRequestOrderingArePreserved()
    {
        var batch = Batch;
        var identities = new[] { Identity(" 000Ab "), Identity("000ab") };
        batch = batch with { Teams = [batch.Teams[1] with { Team = batch.Teams[1].Team with { Identity = identities[0] } }, batch.Teams[0] with { Team = batch.Teams[0].Team with { Identity = identities[1] } }], Matches = [] };
        var result = new FootballIdentityMaterializer().Materialize(Plan(batch));
        Assert.Equal(identities, result.Teams.Select(t => t.Identity));
        Assert.Equal(identities, result.NewMappings.Where(m => m.Kind == IdentityLockEntity.FootballTeam).Select(m => m.Identity));
    }

    [Fact]
    public void ImplicitMatchSeasonUsesSameMaterializedParent()
    {
        var home = Guid.NewGuid();
        var away = Guid.NewGuid();
        var competition = Guid.NewGuid();
        var season = Guid.NewGuid();
        var discovery = Empty with
        {
            Competitions = [new(competition, SportId, "PL")], Seasons = [new(season, competition, "2026")],
            Teams = [home, away], Memberships = [(season, home), (season, away)],
            Mappings = [new(IdentityLockEntity.Competition, Identity("competition"), competition), new(IdentityLockEntity.Season, Identity("season"), season),
                new(IdentityLockEntity.FootballTeam, Identity("home"), home), new(IdentityLockEntity.FootballTeam, Identity("away"), away)]
        };
        var result = new FootballIdentityMaterializer().Materialize(Plan(Batch with { Seasons = [], Teams = [] }, discovery));
        Assert.Same(Assert.Single(result.Seasons).Entity, Assert.Single(result.Matches).Parent);
        Assert.Same(result.Competitions[0].Entity, result.Seasons[0].Parent);
    }

    [Fact]
    public void PlanIsUnchangedAndOutputsAreReadOnly()
    {
        var plan = Plan();
        var references = plan.Competitions.Concat(plan.Seasons).Concat(plan.Teams).Concat(plan.Matches).Select(r => r.Entity).ToArray();
        var result = new FootballIdentityMaterializer().Materialize(plan);
        Assert.All(references, reference => Assert.Null(reference.ExistingId));
        Assert.Equal(references, plan.Competitions.Concat(plan.Seasons).Concat(plan.Teams).Concat(plan.Matches).Select(r => r.Entity));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<PlannedEntity, MaterializedEntity>)result.Entities).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<MaterializedResolution>)result.Teams).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<MaterializedMembership>)result.Memberships).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<MaterializedExternalMapping>)result.NewMappings).Clear());
    }

    [Fact]
    public void IndependentAttemptsNeverReuseAllocations()
    {
        var component = new FootballIdentityMaterializer();
        var plan = Plan();
        var first = component.Materialize(plan);
        var second = component.Materialize(plan);
        Assert.Empty(first.Entities.Values.Select(e => e.Id).Concat(first.NewMappings.Select(m => m.Id)).Intersect(second.Entities.Values.Select(e => e.Id).Concat(second.NewMappings.Select(m => m.Id))));
    }

    [Fact]
    public void AlreadyCancelledAttemptAllocatesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() => new FootballIdentityMaterializer(() => { calls++; return Guid.NewGuid(); }).Materialize(Plan(), cancellation.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void CancellationDuringAllocationDiscardsAttemptAndAllowsFreshRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var allocated = new List<Guid>();
        var component = new FootballIdentityMaterializer(() => { var id = Guid.NewGuid(); allocated.Add(id); if (++calls == 3) cancellation.Cancel(); return id; });
        var plan = Plan();
        Assert.Throws<OperationCanceledException>(() => component.Materialize(plan, cancellation.Token));
        Assert.Equal(3, calls);
        var discarded = allocated.ToArray();
        var retry = component.Materialize(plan);
        Assert.Empty(discarded.Intersect(retry.Entities.Values.Select(e => e.Id).Concat(retry.NewMappings.Select(m => m.Id))));
        Assert.Equal(5, retry.Entities.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyOrRepeatedGeneratedIdsFailWithSafeMessage(bool empty)
    {
        var id = empty ? Guid.Empty : Guid.NewGuid();
        var error = Assert.Throws<InvalidOperationException>(() => new FootballIdentityMaterializer(() => id).Materialize(Plan()));
        Assert.Equal("The identifier generator returned an empty or reused ID.", error.Message);
        Assert.DoesNotContain("Provider", error.Message);
    }

    [Fact]
    public void GeneratedIdCannotCollideWithAnExistingEntity()
    {
        var id = Guid.NewGuid();
        var plan = Plan();
        plan = plan with { Teams = [new(Identity("existing"), new(IdentityLockEntity.FootballTeam, id, null)), .. plan.Teams] };
        Assert.Throws<InvalidOperationException>(() => new FootballIdentityMaterializer(() => id).Materialize(plan));
    }
}
