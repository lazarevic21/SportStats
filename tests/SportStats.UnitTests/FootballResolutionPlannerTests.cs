using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SportStats.Infrastructure.Persistence;
using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;
using Xunit;

namespace SportStats.UnitTests;

public sealed class FootballResolutionPlannerTests
{
    [Theory]
    [InlineData(true, "Matches[0].Match.HomeTeam")]
    [InlineData(false, "Matches[0].Match.AwayTeam")]
    public void DuplicateDirectMatchTeamIdentityReportsItsProperty(bool home, string field)
    {
        var row = Existing.Mappings.First(m => m.Identity == Identity(home ? "home" : "away"));
        var result = new FootballResolutionPlanner().Plan(Batch with { Teams = [] },
            Existing with { Mappings = Existing.Mappings.Append(row).ToArray() });
        Assert.Equal(IdentityResolutionFailureCode.DuplicateMappings, result.Failure?.Code);
        Assert.Equal(field, result.Failure?.Field);
    }

    [Theory]
    [InlineData("Batch")]
    [InlineData("Competitions")]
    [InlineData("Seasons")]
    [InlineData("Teams")]
    [InlineData("Matches")]
    public async Task NullBatchStructureIsRejectedBeforeTransactionInspection(string field)
    {
        IdentityResolutionBatch? batch = field switch
        {
            "Batch" => null,
            "Competitions" => Batch with { Competitions = null! },
            "Seasons" => Batch with { Seasons = null! },
            "Teams" => Batch with { Teams = null! },
            _ => Batch with { Matches = null! }
        };
        using var connection = new NpgsqlConnection();
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql(connection).Options);
        var result = await FootballResolutionDiscovery.PrepareAsync(context, batch!, TimeSpan.FromSeconds(1));
        Assert.Equal(IdentityResolutionFailureCode.InvalidInput, result.Failure?.Code);
        Assert.Equal(field, result.Failure?.Field);
        Assert.Null(result.Plan);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Null(context.Database.CurrentTransaction);
    }

    [Fact]
    public void ValidatedWrapperOwnsSnapshotRatherThanAcceptingMutableInput()
    {
        var teams = Batch.Teams.ToList();
        var (validated, failure) = ValidatedFootballResolutionInput.Create(Batch with { Teams = teams }, default);
        Assert.Null(failure);
        Assert.NotNull(validated);
        teams.Clear();
        teams.Add(Batch.Teams[0] with { Team = Batch.Teams[0].Team with { Name = "invalid\0metadata" } });
        var result = new FootballResolutionPlanner().PlanValidated(validated!, Empty);
        Assert.Null(result.Failure);
        Assert.Equal(2, result.Plan!.Teams.Count);
    }

    [Fact]
    public void ValidatedWrapperRejectsInvalidEnumerationDespiteValidIndexedMetadata()
    {
        var valid = Batch;
        var invalid = valid.Teams[0] with { Team = valid.Teams[0].Team with { Name = "invalid\0metadata" } };
        var batch = valid with { Teams = new EnumeratedList<TeamResolutionRequest>(valid.Teams, () => [invalid]) };
        var (validated, failure) = ValidatedFootballResolutionInput.Create(batch, default);
        Assert.Null(validated);
        Assert.Equal(IdentityResolutionFailureCode.InvalidInput, failure?.Code);
        Assert.Equal("Teams[0].Team.Name", failure?.Field);
    }
    private sealed class EnumeratedList<T>(IReadOnlyList<T> indexed, Func<IEnumerable<T>> enumerated) : IReadOnlyList<T>
    {
        public int Count => indexed.Count;
        public T this[int index] => indexed[index];
        public IEnumerator<T> GetEnumerator() => enumerated().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EnumerationSnapshotIsValidatedBeforeTransactionInspection(int variant)
    {
        var valid = Batch;
        var enumerationCount = 0;
        var invalid = variant switch
        {
            0 => new[] { valid.Teams[0] with { Team = valid.Teams[0].Team with { Name = "bad\0name" } } },
            1 => new[] { valid.Teams[0] with { Team = valid.Teams[0].Team with { Identity = Identity("bad\ud800") } } },
            2 => new[] { valid.Teams[0], valid.Teams[0] },
            _ => new[] { valid.Teams[0] with { Season = new EntityReference<Season>.Internal(Guid.Empty) } }
        };
        var batch = valid with { Teams = new EnumeratedList<TeamResolutionRequest>(valid.Teams, () => { enumerationCount++; return invalid; }) };
        using var connection = new NpgsqlConnection();
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql(connection).Options);
        var result = await FootballResolutionDiscovery.PrepareAsync(context, batch, TimeSpan.FromSeconds(1));
        Assert.Equal(IdentityResolutionFailureCode.InvalidInput, result.Failure?.Code);
        Assert.Null(result.Plan);
        Assert.Equal(1, enumerationCount);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Null(context.Database.CurrentTransaction);
    }

    [Fact]
    public void MutationAfterValidationCannotChangePlannedInput()
    {
        var teams = Batch.Teams.ToList();
        var batch = Batch with { Teams = teams };
        var validated = FootballResolutionPlanner.SnapshotAndValidate(batch);
        Assert.Null(validated.Failure);
        teams.Clear();
        teams.Add(Batch.Teams[0] with { Team = Batch.Teams[0].Team with { Name = "bad\0name" } });
        var result = new FootballResolutionPlanner().PlanValidated(validated.Snapshot!, Empty);
        Assert.Null(result.Failure);
        Assert.Equal(2, result.Plan!.Teams.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<TeamResolutionRequest>)validated.Snapshot!.Input.Teams).Clear());
    }

    [Fact]
    public void CancelledPlanningRejectsBeforeReadingDiscovery()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var data = Empty with { Mappings = new EnumeratedList<DiscoveredMapping>([], () => throw new InvalidOperationException("Must not read discovery.")) };
        var error = Assert.ThrowsAny<OperationCanceledException>(() => new FootballResolutionPlanner().Plan(Batch, data, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public void CancellationDuringDiscoveryIndexingStopsPlanning()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<DiscoveredMapping> Rows()
        {
            yield return Existing.Mappings[0];
            cancellation.Cancel();
            yield return Existing.Mappings[1];
            throw new InvalidOperationException("Cancellation should stop enumeration.");
        }
        var data = Existing with { Mappings = new EnumeratedList<DiscoveredMapping>(Existing.Mappings, Rows) };
        Assert.ThrowsAny<OperationCanceledException>(() => new FootballResolutionPlanner().Plan(Batch, data, cancellation.Token));
    }

    [Theory]
    [InlineData(IdentityLockEntity.Competition, "Competitions[0].Competition.Identity")]
    [InlineData(IdentityLockEntity.FootballTeam, "Teams[0].Team.Identity")]
    [InlineData(IdentityLockEntity.FootballMatch, "Matches[0].Match.Identity")]
    public void DuplicateMappingReportsExactRequestIdentityField(IdentityLockEntity kind, string field)
    {
        var row = Existing.Mappings.First(m => m.Kind == kind);
        var result = new FootballResolutionPlanner().Plan(Batch, Existing with { Mappings = Existing.Mappings.Append(row).ToArray() });
        Assert.Equal(IdentityResolutionFailureCode.DuplicateMappings, result.Failure?.Code);
        Assert.Equal(field, result.Failure?.Field);
    }

    [Fact]
    public void ImplicitMatchSeasonConflictPreservesNestedField()
    {
        var result = new FootballResolutionPlanner().Plan(Batch with { Seasons = [], Teams = [] }, Existing with
        { Seasons = [new(SeasonId, CompetitionId, "wrong-key")] });
        Assert.Equal(IdentityResolutionFailureCode.NaturalKeyConflict, result.Failure?.Code);
        Assert.Equal("Matches[0].Match.Season.Key", result.Failure?.Field);
    }

    [Fact]
    public void ReparentedSeasonReportsCompetitionReference()
    {
        var result = new FootballResolutionPlanner().Plan(Batch, Existing with { Seasons = [new(SeasonId, HomeId, "2026")] });
        Assert.Equal(IdentityResolutionFailureCode.RelationshipConflict, result.Failure?.Code);
        Assert.Equal("Seasons[0].Competition", result.Failure?.Field);
    }

    [Fact]
    public void DuplicateImplicitMatchSeasonMappingPreservesNestedIdentityField()
    {
        var row = Existing.Mappings.First(m => m.Kind == IdentityLockEntity.Season);
        var result = new FootballResolutionPlanner().Plan(Batch with { Seasons = [], Teams = [] },
            Existing with { Mappings = Existing.Mappings.Append(row).ToArray() });
        Assert.Equal(IdentityResolutionFailureCode.DuplicateMappings, result.Failure?.Code);
        Assert.Equal("Matches[0].Match.Season.Identity", result.Failure?.Field);
    }

    [Fact]
    public void DuplicateExternalReferenceMappingReportsReferenceIdentity()
    {
        var row = Existing.Mappings.First(m => m.Kind == IdentityLockEntity.Competition);
        var result = new FootballResolutionPlanner().Plan(Batch with { Competitions = [] },
            Existing with { Mappings = Existing.Mappings.Append(row).ToArray() });
        Assert.Equal(IdentityResolutionFailureCode.DuplicateMappings, result.Failure?.Code);
        Assert.Equal("Seasons[0].Competition.Identity", result.Failure?.Field);
    }

    [Fact]
    public void CancellationDuringInputCopyStopsBeforeValidationOrDiscovery()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<TeamResolutionRequest> Rows()
        {
            yield return Batch.Teams[0];
            cancellation.Cancel();
            yield return Batch.Teams[1];
            throw new InvalidOperationException("Cancellation should stop copying.");
        }
        var batch = Batch with { Teams = new EnumeratedList<TeamResolutionRequest>(Batch.Teams, Rows) };
        Assert.ThrowsAny<OperationCanceledException>(() => new FootballResolutionPlanner().Plan(batch, Empty, cancellation.Token));
    }
    private static readonly Guid SportId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid CompetitionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid SeasonId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid HomeId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid AwayId = Guid.Parse("40000000-0000-0000-0000-000000000002");
    private static readonly Guid MatchId = Guid.Parse("50000000-0000-0000-0000-000000000001");
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
    private static DiscoveredMapping Map(IdentityLockEntity kind, string identity, Guid target) => new(kind, Identity(identity), target);
    private static FootballDiscovery Existing => Empty with
    {
        Competitions = [new(CompetitionId, SportId, "PL")], Seasons = [new(SeasonId, CompetitionId, "2026")],
        Teams = [HomeId, AwayId], Memberships = [(SeasonId, HomeId), (SeasonId, AwayId)],
        Matches = [new(MatchId, SeasonId, HomeId, AwayId)],
        Mappings = [Map(IdentityLockEntity.Competition, "competition", CompetitionId), Map(IdentityLockEntity.Season, "season", SeasonId),
            Map(IdentityLockEntity.FootballTeam, "home", HomeId), Map(IdentityLockEntity.FootballTeam, "away", AwayId),
            Map(IdentityLockEntity.FootballMatch, "match", MatchId)]
    };
    private static FootballResolutionPlan Success(IdentityResolutionBatch batch, FootballDiscovery data)
    {
        var result = new FootballResolutionPlanner().Plan(batch, data);
        Assert.Null(result.Failure);
        return Assert.IsType<FootballResolutionPlan>(result.Plan);
    }
    private static void Failure(IdentityResolutionBatch batch, FootballDiscovery data, IdentityResolutionFailureCode code)
    {
        var result = new FootballResolutionPlanner().Plan(batch, data);
        Assert.Null(result.Plan);
        Assert.Equal(code, Assert.IsType<IdentityResolutionFailure>(result.Failure).Code);
    }

    [Fact]
    public void NewBatchPlansEveryEntityAndMembershipWithSymbolicDependencies()
    {
        var plan = Success(Batch, Empty);
        var competition = Assert.Single(plan.Competitions);
        var season = Assert.Single(plan.Seasons);
        var match = Assert.Single(plan.Matches);
        Assert.Null(competition.Entity.ExistingId);
        Assert.Equal(competition.Identity, competition.Entity.NewIdentity);
        Assert.Equal(competition.Entity, season.Parent);
        Assert.Equal(2, plan.Teams.Count);
        Assert.Equal(2, plan.Memberships.Count);
        Assert.All(plan.Teams, team => { Assert.Null(team.Entity.ExistingId); Assert.Equal(season.Entity, team.Parent); });
        Assert.All(plan.Memberships, membership => { Assert.False(membership.Exists); Assert.Equal(season.Entity, membership.Season); });
        Assert.Equal(season.Entity, match.Parent);
        Assert.Equal(plan.Teams[0].Entity, match.Home);
        Assert.Equal(plan.Teams[1].Entity, match.Away);
    }

    [Fact]
    public void ExistingMappingsReuseTargetsAndExistingMemberships()
    {
        var plan = Success(Batch, Existing);
        Assert.Equal(CompetitionId, Assert.Single(plan.Competitions).Entity.ExistingId);
        Assert.Equal(SeasonId, Assert.Single(plan.Seasons).Entity.ExistingId);
        Assert.Equal(MatchId, Assert.Single(plan.Matches).Entity.ExistingId);
        Assert.All(plan.Memberships, membership => Assert.True(membership.Exists));
        Assert.All(plan.Teams, team => Assert.Null(team.Entity.NewIdentity));
    }

    [Theory]
    [InlineData(IdentityLockEntity.Competition)]
    [InlineData(IdentityLockEntity.Season)]
    public void UnmappedNaturalKeyOwnerCannotBeSilentlyLinked(IdentityLockEntity kind)
    {
        Failure(Batch, Existing with { Mappings = Existing.Mappings.Where(m => m.Kind != kind).ToArray() }, IdentityResolutionFailureCode.NaturalKeyConflict);
    }

    [Theory]
    [InlineData(false, IdentityResolutionFailureCode.DuplicateMappings)]
    [InlineData(true, IdentityResolutionFailureCode.AmbiguousMappings)]
    public void DuplicateMappingFailureDistinguishesConflictingTargets(bool differentTarget, IdentityResolutionFailureCode code)
    {
        var extra = Existing.Mappings[0] with { TargetId = differentTarget ? HomeId : CompetitionId };
        Failure(Batch, Existing with { Mappings = Existing.Mappings.Append(extra).ToArray() }, code);
    }

    [Fact]
    public void DanglingMappingFailsInsteadOfPlanningCreation()
    {
        Failure(Batch, Existing with { Teams = [AwayId] }, IdentityResolutionFailureCode.MissingReference);
    }

    [Fact]
    public void MissingExternalParentFails()
    {
        Failure(Batch with { Competitions = [] }, Empty, IdentityResolutionFailureCode.MissingReference);
    }

    [Fact]
    public void MissingInternalParentFails()
    {
        Failure(Batch with { Competitions = [], Seasons = [new(new EntityReference<Competition>.Internal(CompetitionId), Season)] }, Empty,
            IdentityResolutionFailureCode.MissingReference);
    }

    [Fact]
    public void MatchRequiresMembershipForReferencedExistingTeams()
    {
        Failure(Batch with { Teams = [] }, Existing with { Memberships = [] }, IdentityResolutionFailureCode.MissingReference);
    }

    [Fact]
    public void ExistingMatchCannotChangeParticipants()
    {
        Failure(Batch, Existing with { Matches = [new(MatchId, SeasonId, AwayId, HomeId)] }, IdentityResolutionFailureCode.RelationshipConflict);
    }

    [Fact]
    public void DifferentProviderAliasesForSameTeamCannotPlayEachOther()
    {
        var data = Existing with { Mappings = Existing.Mappings.Select(m => m.Identity == Identity("away") ? m with { TargetId = HomeId } : m).ToArray() };
        Failure(Batch, data, IdentityResolutionFailureCode.RelationshipConflict);
    }

    [Fact]
    public void SeasonCannotBeReparented()
    {
        Failure(Batch, Existing with { Seasons = [new(SeasonId, HomeId, "2026")] }, IdentityResolutionFailureCode.RelationshipConflict);
    }

    [Fact]
    public void RepeatedPlansKeepInputOrderAndProduceSortedUniqueCompleteLockKeys()
    {
        var first = Success(Batch, Existing);
        var second = Success(Batch, Existing);
        Assert.Equal(first.Competitions, second.Competitions);
        Assert.Equal(first.Seasons, second.Seasons);
        Assert.Equal(first.Teams, second.Teams);
        Assert.Equal(first.Matches, second.Matches);
        Assert.Equal(first.IndividualDataLocks.OrderedKeys, second.IndividualDataLocks.OrderedKeys);
        var keys = new AdvisoryLockKeys();
        var expected = Existing.Mappings.Select(m => keys.Identity(m.Kind, m.Identity))
            .Concat([keys.Sport("football"), keys.Competition(SportId, "PL"), keys.Season(CompetitionId, "2026"), keys.SeasonTeam(SeasonId, HomeId), keys.SeasonTeam(SeasonId, AwayId)]).Distinct().Order().ToArray();
        Assert.Equal(expected, first.IndividualDataLocks.OrderedKeys);
        Assert.Equal(keys.FootballCoordination(), first.IndividualDataLocks.CoordinationKey);
        var reordered = Success(Batch with { Teams = Batch.Teams.Reverse().ToArray() }, Existing);
        Assert.Equal(Batch.Teams.Reverse().Select(t => t.Identity), reordered.Teams.Select(t => t.Identity));
        Assert.Equal(first.IndividualDataLocks.OrderedKeys, reordered.IndividualDataLocks.OrderedKeys);
    }

    [Fact]
    public void OpaqueWhitespaceCaseAndLeadingZeroIdsStayDistinct()
    {
        var teams = new[] { "001", "1", " Team ", "team" }.Select(id => new TeamResolutionRequest(
            new EntityReference<Season>.External(Season.Identity), new(Identity(id), "Same metadata", null, null))).ToArray();
        var plan = Success(Batch with { Teams = teams, Matches = [] }, Empty);
        Assert.Equal(teams.Select(t => t.Identity), plan.Teams.Select(t => t.Entity.NewIdentity));
        Assert.Equal(4, plan.Teams.Select(t => t.Entity).Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidUnicodeInIdentityMetadataAndReferenceIsRejected(int variant)
    {
        var invalid = variant switch { 0 => "nul\0text", 1 => "bad\ud800", 2 => "bad\udc00", _ => "bad\ud800x" };
        var batch = Batch;
        var identity = Identity(invalid);
        IdentityResolutionBatch[] cases =
        [
            batch with { Competitions = [batch.Competitions[0] with { Competition = batch.Competitions[0].Competition with { Identity = identity } }] },
            batch with { Competitions = [batch.Competitions[0] with { Competition = batch.Competitions[0].Competition with { Name = invalid } }] },
            batch with { Seasons = [batch.Seasons[0] with { Competition = new EntityReference<Competition>.External(identity) }] },
            batch with { Teams = [batch.Teams[0] with { Team = batch.Teams[0].Team with { CrestUrl = invalid } }] },
            batch with { Matches = [batch.Matches[0] with { Match = batch.Matches[0].Match with { ProviderStatus = invalid } }] }
        ];
        Assert.All(cases, item => Failure(item, Empty, IdentityResolutionFailureCode.InvalidInput));
    }

    [Fact]
    public void ValidSurrogatePairIsPreserved()
    {
        var identity = Identity("team\ud83d\ude80");
        var plan = Success(Batch with { Teams = [new(new EntityReference<Season>.External(Season.Identity), new(identity, "Team \ud83d\ude80", null, null))], Matches = [] }, Empty);
        Assert.Equal(identity, Assert.Single(plan.Teams).Entity.NewIdentity);
    }

    [Fact]
    public void ProviderAliasesAlreadyMappedToSameCompetitionAndSeasonSucceed()
    {
        var aliasCompetition = Batch.Competitions[0] with { Competition = Batch.Competitions[0].Competition with { Identity = Identity("competition-alias") } };
        var aliasSeason = new SeasonResolutionRequest(new EntityReference<Competition>.External(aliasCompetition.Identity), Season with { Identity = Identity("season-alias") });
        var batch = Batch with { Competitions = [Batch.Competitions[0], aliasCompetition], Seasons = [Batch.Seasons[0], aliasSeason] };
        var data = Existing with { Mappings = Existing.Mappings.Concat([
            Map(IdentityLockEntity.Competition, "competition-alias", CompetitionId), Map(IdentityLockEntity.Season, "season-alias", SeasonId)]).ToArray() };
        var plan = Success(batch, data);
        Assert.Equal(2, plan.Competitions.Count);
        Assert.Equal(2, plan.Seasons.Count);
        Assert.Single(plan.Competitions.Select(c => c.Entity).Distinct());
        Assert.Single(plan.Seasons.Select(s => s.Entity).Distinct());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompetingUnmappedBatchIdentitiesCannotShareNaturalKeys(bool competition)
    {
        var batch = competition
            ? Batch with { Competitions = [Batch.Competitions[0], Batch.Competitions[0] with { Competition = Batch.Competitions[0].Competition with { Identity = Identity("other") } }] }
            : Batch with { Seasons = [Batch.Seasons[0], Batch.Seasons[0] with { Season = Season with { Identity = Identity("other") } }] };
        Failure(batch, Empty, IdentityResolutionFailureCode.NaturalKeyConflict);
    }

    [Fact]
    public void TwoDistinctRequestedSeasonsExceedSingleSeasonScope()
    {
        var other = Season with { Identity = Identity("next-season"), QueryYear = 2027, Key = "2027", StartDate = new(2027, 8, 1), EndDate = new(2028, 5, 31) };
        Failure(Batch with { Seasons = [Batch.Seasons[0], new(Competition, other)] }, Empty, IdentityResolutionFailureCode.RelationshipConflict);
    }

    [Fact]
    public void ExistingTeamRequestPlansMissingMembershipWithoutChangingTeamIdentity()
    {
        var plan = Success(Batch, Existing with { Memberships = [] });
        Assert.Equal(2, plan.Memberships.Count);
        Assert.All(plan.Memberships, membership => Assert.False(membership.Exists));
        Assert.Equal(HomeId, plan.Teams[0].Entity.ExistingId);
        Assert.Equal(AwayId, plan.Teams[1].Entity.ExistingId);
    }

    [Fact]
    public void RepeatedImplicitMatchSeasonMustHaveConsistentMetadata()
    {
        var first = Batch.Matches[0];
        var second = first with { Match = first.Match with { Identity = Identity("second-match"), Season = Season with { QueryYear = 2025 } } };
        Failure(Batch with { Seasons = [], Teams = [], Matches = [first, second] }, Existing, IdentityResolutionFailureCode.NaturalKeyConflict);
    }
    [Fact]
    public async Task InvalidInputIsRejectedBeforeTransactionInspectionOrOpeningConnection()
    {
        using var connection = new NpgsqlConnection();
        using var context = new SportStatsDbContext(new DbContextOptionsBuilder<SportStatsDbContext>().UseNpgsql(connection).Options);
        var batch = Batch with { Competitions = [new(SportId, new(Identity("bad\ud800"), "PL", "League"))] };
        var result = await FootballResolutionDiscovery.PrepareAsync(context, batch, TimeSpan.FromSeconds(1));
        Assert.Null(result.Plan);
        Assert.Equal(IdentityResolutionFailureCode.InvalidInput, result.Failure?.Code);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());
    }
    [Fact]
    public void RepeatedBatchIdentityIsInvalidRatherThanArbitrarilySelected()
    {
        Failure(Batch with { Teams = [Batch.Teams[0], Batch.Teams[0]] }, Empty, IdentityResolutionFailureCode.InvalidInput);
    }
}
