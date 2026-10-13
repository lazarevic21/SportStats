using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Domain.Football;
using SportStats.Domain.Platform;
using Xunit;

namespace SportStats.UnitTests;

public sealed class IdentityResolutionContractTests
{
    private static ProviderIdentity Identity(string id = "001") => new("Provider", "Scope ", id);
    private static EntityReference<Competition> Competition => new EntityReference<Competition>.External(Identity("PL"));
    private static NormalizedSeason Season => new(Identity("2026"), 2026, "2026", new(2026, 8, 1), new(2027, 5, 31));
    private static CompetitionResolutionRequest Request(string id = "001", string code = "PL") =>
        new(Guid.NewGuid(), new(Identity(id), code, "Premier League"));
    private static NormalizedMatch Match => new(Identity("match"), Season, Identity("home"), Identity("away"),
        new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc), FootballMatchStatus.Scheduled, "SCHEDULED", null,
        new(ScoreFieldPresence.Missing, null), new(ScoreFieldPresence.Value, 0));
    private static IdentityResolutionBatch Batch(params CompetitionResolutionRequest[] competitions) => new(competitions, [], [], []);

    [Fact]
    public void AllRequestTypesAcceptValidNormalizedDataAndTypedParents()
    {
        IIdentityResolutionRequest[] requests =
        [
            Request(), new SeasonResolutionRequest(Competition, Season),
            new TeamResolutionRequest(new EntityReference<Season>.Internal(Guid.NewGuid()), new(Identity("team"), "Team", null, null)),
            new MatchResolutionRequest(Competition, Match)
        ];
        Assert.All(requests, request => Assert.Empty(IdentityResolutionValidation.Validate(request)));
    }

    [Theory]
    [InlineData("PL", "pl")]
    [InlineData("PL", "PL ")]
    [InlineData("001", "1")]
    public void OpaqueIdentitiesArePreservedAndComparedExactly(string first, string second)
    {
        var request = Request(first);
        Assert.Empty(IdentityResolutionValidation.Validate(request));
        Assert.Equal(first, request.Identity.ExternalId);
        Assert.Equal("Scope ", request.Identity.IdentityScope);
        Assert.NotEqual(request.Identity, Identity(second));
        Assert.Empty(IdentityResolutionValidation.Validate(Batch(request, Request(second))));
    }

    [Fact]
    public void ProviderAndScopeAreAlsoOrdinalAndOpaque()
    {
        var identity = new ProviderIdentity("Provider ", "scope ", " id ");
        var request = Request() with { Competition = new(identity, "PL", "League") };
        Assert.Empty(IdentityResolutionValidation.Validate(request));
        Assert.Same(identity, request.Identity);
        Assert.NotEqual(identity, identity with { ProviderKey = "provider " });
        Assert.NotEqual(identity, identity with { IdentityScope = "scope" });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EveryIdentityComponentRequiresNonWhitespaceText(string? invalid)
    {
        var identity = Identity();
        ProviderIdentity[] identities =
        [identity with { ProviderKey = invalid! }, identity with { IdentityScope = invalid! }, identity with { ExternalId = invalid! }];
        Assert.All(identities, value => Assert.Contains(IdentityResolutionValidation.Validate(
            Request() with { Competition = new(value, "PL", "League") }), error => error.Code == IdentityResolutionFailureCode.InvalidInput));
    }

    [Theory]
    [InlineData(" PL")]
    [InlineData("PL ")]
    [InlineData(" ")]
    public void NaturalKeysRejectValuesDomainWouldSilentlyTrim(string code)
    {
        Assert.Contains(IdentityResolutionValidation.Validate(Request(code: code)), error => error.Field == "Competition.Code");
        Assert.Contains(IdentityResolutionValidation.Validate(new SeasonResolutionRequest(Competition, Season with { Key = code })),
            error => error.Field == "Season.Key");
    }

    [Fact]
    public void InvalidParentsNullModelsAndReversedDatesAreFailures()
    {
        IIdentityResolutionRequest?[] invalid =
        [
            null, Request() with { SportId = Guid.Empty }, Request() with { Competition = null! },
            Request() with { Competition = new(null!, "PL", "League") },
            new SeasonResolutionRequest(new EntityReference<Competition>.Internal(Guid.Empty), Season),
            new SeasonResolutionRequest(new EntityReference<Competition>.External(null!), Season),
            new SeasonResolutionRequest(null!, Season), new SeasonResolutionRequest(Competition, null!),
            new SeasonResolutionRequest(Competition, Season with { EndDate = new(2025, 1, 1) }),
            new SeasonResolutionRequest(Competition, Season with { QueryYear = 0 }),
            new TeamResolutionRequest(null!, null!), new MatchResolutionRequest(Competition, null!)
        ];
        Assert.All(invalid, request => Assert.NotEmpty(IdentityResolutionValidation.Validate(request)));
    }

    [Fact]
    public void MatchesRequireUtcDefinedStatusDistinctTeamsAndCoherentScorePresence()
    {
        NormalizedMatch[] invalid =
        [
            Match with { KickoffUtc = DateTime.SpecifyKind(Match.KickoffUtc, DateTimeKind.Unspecified) },
            Match with { Status = (FootballMatchStatus)999 }, Match with { HomeTeam = null! },
            Match with { Season = null! }, Match with { ProviderStatus = " " },
            Match with { HomeGoals = new(ScoreFieldPresence.Value, null) },
            Match with { HomeGoals = new(ScoreFieldPresence.Value, -1) },
            Match with { HomeGoals = new(ScoreFieldPresence.Null, 0) },
            Match with { HomeGoals = new(ScoreFieldPresence.Missing, 0) },
            Match with { HomeGoals = new((ScoreFieldPresence)999, null) }, Match with { AwayGoals = null! }
        ];
        Assert.All(invalid, match => Assert.Contains(IdentityResolutionValidation.Validate(new MatchResolutionRequest(Competition, match)),
            error => error.Code == IdentityResolutionFailureCode.InvalidInput));
        Assert.Contains(IdentityResolutionValidation.Validate(new MatchResolutionRequest(Competition, Match with { AwayTeam = Match.HomeTeam })),
            error => error.Code == IdentityResolutionFailureCode.RelationshipConflict);
        Assert.Empty(IdentityResolutionValidation.Validate(new MatchResolutionRequest(Competition, Match with { HomeGoals = new(ScoreFieldPresence.Null, null) })));
    }

    [Fact]
    public void BatchRejectsRepeatedIdentityWithinTypeAndReportsInputPath()
    {
        var failure = Assert.Single(IdentityResolutionValidation.Validate(Batch(Request(), Request())));
        Assert.Equal(IdentityResolutionFailureCode.InvalidInput, failure.Code);
        Assert.Equal("Competitions[1].Identity", failure.Field);
        Assert.NotEmpty(IdentityResolutionValidation.Validate((IdentityResolutionBatch?)null));
        Assert.NotEmpty(IdentityResolutionValidation.Validate(new IdentityResolutionBatch(null!, [], [], [])));
        Assert.NotEmpty(IdentityResolutionValidation.Validate(Batch(null!)));
        Assert.Empty(IdentityResolutionValidation.Validate(Batch()));
    }

    [Fact]
    public void BatchAllowsSameOpaqueIdentityAcrossTypesAndExternalParentDependencies()
    {
        var competition = Request("2026");
        var batch = new IdentityResolutionBatch([competition],
            [new(new EntityReference<Competition>.External(competition.Identity), Season)],
            [new(new EntityReference<Season>.External(Season.Identity), new(Identity("home"), "Home", null, null)),
             new(new EntityReference<Season>.External(Season.Identity), new(Identity("away"), "Away", null, null))],
            [new(Competition, Match)]);
        Assert.Empty(IdentityResolutionValidation.Validate(batch));
    }

    [Fact]
    public void EveryResolverOperationSupportsCancellationAndTypedResults()
    {
        var methods = typeof(IFootballIdentityResolver).GetMethods();
        Assert.Equal(5, methods.Length);
        Assert.All(methods, method =>
        {
            var parameter = method.GetParameters()[^1];
            Assert.Equal(typeof(CancellationToken), parameter.ParameterType);
            Assert.True(parameter.HasDefaultValue);
        });
        Assert.Equal(typeof(Task<IdentityResolutionResult<Competition>>),
            methods.Single(method => method.Name == nameof(IFootballIdentityResolver.ResolveCompetitionAsync)).ReturnType);
        Assert.Equal(6, Enum.GetValues<IdentityResolutionFailureCode>().Length);
        foreach (var code in Enum.GetValues<IdentityResolutionFailureCode>())
        {
            var failure = new IdentityResolutionFailure(code, "Identity", "Safe diagnostic");
            IdentityResolutionResult<Competition> result = new IdentityResolutionResult<Competition>.Failed(failure);
            Assert.Same(failure, Assert.IsType<IdentityResolutionResult<Competition>.Failed>(result).Failure);
            Assert.Same(failure, new IdentityResolutionBatchResult.Failed(failure).Failure);
        }
    }
}
