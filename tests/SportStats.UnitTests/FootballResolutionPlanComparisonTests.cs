using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using SportStats.Infrastructure.Persistence.AdvisoryLocks;
using SportStats.Infrastructure.Persistence.IdentityResolution;
using Xunit;

namespace SportStats.UnitTests;

public sealed class FootballResolutionPlanComparisonTests
{
    private static ProviderIdentity Identity(string id) => new("provider", "scope", id);
    private static (ValidatedFootballResolutionInput Input, FootballDiscovery Data) Input()
    {
        var sportId = Guid.NewGuid();
        var competition = new EntityReference<Competition>.External(Identity("competition"));
        var season = new NormalizedSeason(Identity("season"), 2026, "2026", new(2026, 8, 1), new(2027, 5, 31));
        var batch = new IdentityResolutionBatch(
            [new(sportId, new(Identity("competition"), "PL", "League"))], [new(competition, season)],
            [new(new EntityReference<Season>.External(season.Identity), new(Identity("home"), "Home", null, null)),
             new(new EntityReference<Season>.External(season.Identity), new(Identity("away"), "Away", null, null))],
            [new(competition, new(Identity("match"), season, Identity("home"), Identity("away"),
                DateTime.SpecifyKind(new DateTime(2026, 10, 14), DateTimeKind.Utc), FootballMatchStatus.Scheduled, "SCHEDULED", 1,
                new(ScoreFieldPresence.Missing, null), new(ScoreFieldPresence.Missing, null)))]);
        var (snapshot, failure) = FootballResolutionPlanner.SnapshotAndValidate(batch);
        Assert.Null(failure);
        return (snapshot!, new([new(sportId, "football")], [], [], [], [], [], []));
    }

    private static FootballResolutionPlan Plan()
    {
        var (input, data) = Input();
        return new FootballResolutionPlanner().PlanValidated(input, data).Plan!;
    }

    [Fact]
    public void IndependentlyReplannedCollectionsCompareByDecisionValues()
    {
        var (input, data) = Input();
        var original = new FootballResolutionPlanner().PlanValidated(input, data).Plan!;
        var refreshed = new FootballResolutionPlanner().PlanValidated(input, data).Plan!;
        Assert.NotSame(original.Competitions, refreshed.Competitions);
        Assert.True(FootballResolutionPlanComparison.Equivalent(original, refreshed));
    }

    [Theory]
    [InlineData("competition")]
    [InlineData("season")]
    [InlineData("team")]
    [InlineData("match")]
    [InlineData("parent")]
    [InlineData("home")]
    [InlineData("away")]
    [InlineData("membership")]
    [InlineData("individual-locks")]
    [InlineData("coordination-lock")]
    [InlineData("symbolic-season")]
    [InlineData("symbolic-membership")]
    [InlineData("order")]
    public void MaterialDecisionChangeIsDetected(string change)
    {
        var original = Plan();
        PlannedEntity Changed(PlannedEntity entity) => entity with { ExistingId = Guid.NewGuid(), NewIdentity = null };
        var refreshed = change switch
        {
            "competition" => original with { Competitions = [original.Competitions[0] with { Entity = Changed(original.Competitions[0].Entity) }] },
            "season" => original with { Seasons = [original.Seasons[0] with { Entity = Changed(original.Seasons[0].Entity) }] },
            "team" => original with { Teams = [original.Teams[0] with { Entity = Changed(original.Teams[0].Entity) }, original.Teams[1]] },
            "match" => original with { Matches = [original.Matches[0] with { Entity = Changed(original.Matches[0].Entity) }] },
            "parent" => original with { Seasons = [original.Seasons[0] with { Parent = Changed(original.Seasons[0].Parent!) }] },
            "home" => original with { Matches = [original.Matches[0] with { Home = original.Teams[1].Entity }] },
            "away" => original with { Matches = [original.Matches[0] with { Away = original.Teams[0].Entity }] },
            "membership" => original with { Memberships = [original.Memberships[0] with { Exists = true }, original.Memberships[1]] },
            "individual-locks" => original with { IndividualDataLocks = new(original.IndividualDataLocks.OrderedKeys.Append(123)) },
            "coordination-lock" => original with { IndividualDataLocks = new(original.IndividualDataLocks.OrderedKeys, 123) },
            "symbolic-season" => original with { CoordinationProtectedSeasonKeys = [] },
            "symbolic-membership" => original with { CoordinationProtectedMembershipKeys = [] },
            _ => original with { Teams = original.Teams.Reverse().ToArray() }
        };
        Assert.False(FootballResolutionPlanComparison.Equivalent(original, refreshed));
    }

    [Fact]
    public void SetOrderingDoesNotChangeMembershipOrCoverageDecisions()
    {
        var original = Plan();
        var refreshed = original with
        {
            Memberships = original.Memberships.Reverse().ToArray(),
            CoordinationProtectedMembershipKeys = original.CoordinationProtectedMembershipKeys.Reverse().ToArray(),
            IndividualDataLocks = new(original.IndividualDataLocks.OrderedKeys.Reverse())
        };
        Assert.True(FootballResolutionPlanComparison.Equivalent(original, refreshed));
    }

    [Fact]
    public void IndependentOwnedInputsCannotBeInterchanged()
    {
        var original = Plan();
        Assert.False(FootballResolutionPlanComparison.Equivalent(original, original with { Input = original.Input with { } }));
    }

    [Fact]
    public void ComparisonHonorsCancellation()
    {
        var plan = Plan();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FootballResolutionPlanComparison.Equivalent(plan, plan, cancellation.Token));
    }
}
