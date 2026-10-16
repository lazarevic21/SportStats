using SportStats.Application.Football.IdentityResolution;
using SportStats.Application.Football.Ingestion;
using SportStats.Application.Football.MatchMetadata;
using SportStats.Domain.Football;
using SportStats.Infrastructure.Persistence.MatchMetadata;
using Xunit;

namespace SportStats.UnitTests;

public sealed class MatchMetadataEvaluationServiceTests
{
    private static readonly Guid MatchId = Guid.NewGuid(), SeasonId = Guid.NewGuid(), HomeId = Guid.NewGuid(), AwayId = Guid.NewGuid();
    private static readonly DateTime Utc = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private static ProviderIdentity Identity(string id) => new("provider", "scope", id);
    private static MatchProviderObservation Observation => new(Identity("match"), new("provider", "/matches"),
        new(Identity("season"), 2026, "2026", new(2026, 8, 1), new(2027, 6, 1)), Identity("home"), Identity("away"),
        MatchObservationField<DateTime>.FromValue(Utc), MatchObservationField<int>.FromValue(1),
        MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Scheduled), "TIMED",
        MatchObservationField<int>.FromValue(1), MatchObservationField<int>.FromValue(2), default, Utc);
    private static MatchMetadataIdentitySnapshot Snapshot => new(
        [new(MatchId, SeasonId, HomeId, AwayId, Utc, 1, FootballMatchStatus.Scheduled, 1, 2)], [SeasonId], [HomeId, AwayId],
        [new(MatchMetadataIdentityKind.Match, Identity("match"), MatchId),
         new(MatchMetadataIdentityKind.Season, Identity("season"), SeasonId),
         new(MatchMetadataIdentityKind.Team, Identity("home"), HomeId),
         new(MatchMetadataIdentityKind.Team, Identity("away"), AwayId)]);

    private sealed class Reader(Func<IReadOnlyList<MatchProviderObservation>, CancellationToken, Task<MatchMetadataIdentitySnapshot>> load) : IMatchMetadataIdentityReader
    {
        public int Calls { get; private set; }
        public Task<MatchMetadataIdentitySnapshot> LoadAsync(IReadOnlyList<MatchProviderObservation> observations, CancellationToken cancellationToken = default)
        {
            Calls++;
            return load(observations, cancellationToken);
        }
    }
    private static MatchMetadataEvaluationService Service(Reader reader) => new(reader, new());
    private static Reader Fixed(MatchMetadataIdentitySnapshot snapshot) => new((_, _) => Task.FromResult(snapshot));

    [Fact]
    public async Task ExactIdentityReturnsAdvisoryUnchangedResult()
    {
        var reader = Fixed(Snapshot);
        var result = Assert.Single(await Service(reader).EvaluateBatchAsync([Observation]));
        Assert.Equal(1, reader.Calls);
        Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, result.Disposition);
        Assert.Equal(MatchId, result.MatchId);
        Assert.False(result.IsUpdateAuthorized);
        Assert.Equal(MatchMetadataFreshness.FreshnessUnverified, result.Freshness);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("scope")]
    [InlineData("external-id")]
    public async Task SimilarIdentityTupleNeverFallsBackToMetadata(string dimension)
    {
        var identity = dimension switch
        {
            "provider" => new ProviderIdentity("other", "scope", "match"),
            "scope" => new ProviderIdentity("provider", "other", "match"),
            _ => new ProviderIdentity("provider", "scope", "other")
        };
        var observation = Observation with { Identity = identity, Source = new(identity.ProviderKey, "/matches") };
        var result = Assert.Single(await Service(Fixed(Snapshot)).EvaluateBatchAsync([observation]));
        Assert.Equal(MatchMetadataEvaluationDisposition.Conflict, result.Disposition);
        Assert.Equal(IdentityResolutionFailureCode.MissingReference, result.Failure!.Code);
    }

    [Theory]
    [InlineData("missing", IdentityResolutionFailureCode.MissingReference)]
    [InlineData("duplicate", IdentityResolutionFailureCode.DuplicateMappings)]
    [InlineData("ambiguous", IdentityResolutionFailureCode.AmbiguousMappings)]
    [InlineData("dangling", IdentityResolutionFailureCode.MissingReference)]
    public async Task MappingFailuresRemainStrict(string scenario, IdentityResolutionFailureCode expected)
    {
        var snapshot = Snapshot;
        var rows = snapshot.Mappings.ToList();
        if (scenario == "missing") rows.RemoveAt(0);
        if (scenario == "duplicate") rows.Add(rows[0]);
        if (scenario == "ambiguous") rows.Add(rows[0] with { EntityId = Guid.NewGuid() });
        if (scenario == "dangling") rows[0] = rows[0] with { EntityId = Guid.NewGuid() };
        var result = Assert.Single(await Service(Fixed(snapshot with { Mappings = rows })).EvaluateBatchAsync([Observation]));
        Assert.Equal(MatchMetadataEvaluationDisposition.Conflict, result.Disposition);
        Assert.Equal(expected, result.Failure!.Code);
        Assert.Empty(result.Changes);
    }

    [Theory]
    [InlineData("season")]
    [InlineData("home")]
    [InlineData("away")]
    public async Task PersistedRelationshipsRejectConflictingAssertions(string relationship)
    {
        var snapshot = Snapshot;
        var other = Guid.NewGuid();
        snapshot = snapshot with {
            SeasonIds = snapshot.SeasonIds.Append(other).ToArray(),
            TeamIds = snapshot.TeamIds.Append(other).ToArray(),
            Mappings = snapshot.Mappings.Select(x => x.Identity.ExternalId == relationship ? x with { EntityId = other } : x).ToArray()
        };
        var result = Assert.Single(await Service(Fixed(snapshot)).EvaluateBatchAsync([Observation]));
        Assert.Equal(IdentityResolutionFailureCode.RelationshipConflict, result.Failure!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrustedAliasesAreEvaluatedTogether(bool conflicting)
    {
        var snapshot = Snapshot;
        snapshot = snapshot with { Mappings = snapshot.Mappings.Append(new(MatchMetadataIdentityKind.Match, Identity("alias"), MatchId)).ToArray() };
        var alias = Observation with { Identity = Identity("alias"), HomeGoals = MatchObservationField<int>.FromValue(conflicting ? 3 : 1) };
        var results = await Service(Fixed(snapshot)).EvaluateBatchAsync([Observation, alias, Observation]);
        Assert.Equal(3, results.Count);
        Assert.All(results, x => Assert.Equal(conflicting ? MatchMetadataEvaluationDisposition.Conflict : MatchMetadataEvaluationDisposition.Unchanged, x.Disposition));
    }

    [Fact]
    public async Task ResultsPreserveRequestOrderAndDoNotMutateSnapshot()
    {
        var snapshot = Snapshot;
        var changed = Observation with { HomeGoals = MatchObservationField<int>.FromValue(0) };
        var results = await Service(Fixed(snapshot)).EvaluateBatchAsync([Observation with { Identity = Identity("absent") }, changed]);
        Assert.Equal(MatchMetadataEvaluationDisposition.Conflict, results[0].Disposition);
        Assert.Equal(MatchMetadataEvaluationDisposition.ChangesDetected, results[1].Disposition);
        Assert.Equal(1, snapshot.Matches[0].HomeGoals);
        Assert.False(results[1].IsUpdateAuthorized);
        Assert.Equal(MatchMetadataFreshness.FreshnessUnverified, results[1].Freshness);
    }

    [Fact]
    public async Task CallerCollectionIsCapturedBeforeAwait()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader(async (input, _) => { entered.SetResult(); await release.Task; Assert.Single(input); Assert.Equal(Identity("match"), input[0].Identity); return Snapshot; });
        var input = new List<MatchProviderObservation> { Observation };
        var task = Service(reader).EvaluateBatchAsync(input);
        await entered.Task;
        input.Clear();
        release.SetResult();
        Assert.Equal(MatchMetadataEvaluationDisposition.Unchanged, Assert.Single(await task).Disposition);
    }

    [Fact]
    public async Task EmptyBatchDoesNotRead()
    {
        var reader = Fixed(Snapshot);
        Assert.Empty(await Service(reader).EvaluateBatchAsync([]));
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task CancellationBeforeReadDoesNotCallReader()
    {
        var reader = Fixed(Snapshot);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(reader).EvaluateBatchAsync([Observation], source.Token));
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task CancellationIsForwardedDuringRead()
    {
        using var source = new CancellationTokenSource();
        var reader = new Reader(async (_, token) => { Assert.Equal(source.Token, token); source.Cancel(); await Task.Delay(Timeout.Infinite, token); return Snapshot; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(reader).EvaluateBatchAsync([Observation], source.Token));
    }

    [Fact]
    public async Task CancellationAfterReaderCompletesPreventsEvaluation()
    {
        using var source = new CancellationTokenSource();
        var reader = new Reader((_, _) => { source.Cancel(); return Task.FromResult(Snapshot); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(reader).EvaluateBatchAsync([Observation], source.Token));
    }

    [Fact]
    public async Task DatabaseFailurePropagatesWithoutInventingResult()
    {
        var failure = new InvalidOperationException("database read failed");
        var reader = new Reader((_, _) => Task.FromException<MatchMetadataIdentitySnapshot>(failure));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Service(reader).EvaluateBatchAsync([Observation])));
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task UnsupportedStatusRemainsInvalid()
    {
        var result = Assert.Single(await Service(Fixed(Snapshot)).EvaluateBatchAsync([Observation with { Status = MatchObservationField<FootballMatchStatus>.FromValue(FootballMatchStatus.Unknown) }]));
        Assert.Equal(MatchMetadataEvaluationDisposition.InvalidInput, result.Disposition);
        Assert.Empty(result.Changes);
    }
}
