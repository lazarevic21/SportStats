using SportStats.Application.Football.InitialImport;
using SportStats.Application.Football.Ingestion;
using SportStats.Infrastructure.FootballData;
using Xunit;

namespace SportStats.UnitTests;

public sealed class InitialImportFetcherTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FirstFailureStopsWithoutRetryOrSensitiveDiagnostic(int failAt)
    {
        var client = new Client(failAt); var pauses = new List<TimeSpan>();
        var importer = Create(client, (delay, _) => { pauses.Add(delay); return Task.CompletedTask; });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => importer.FetchAsync(InitialImportProfile.PremierLeague, 2026));
        Assert.Equal(failAt, client.Calls.Count);
        Assert.Equal(failAt - 1, pauses.Count);
        Assert.All(pauses, pause => Assert.Equal(TimeSpan.FromSeconds(7), pause));
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "competition:PL", "teams:PL", "matches:PL" }.Take(failAt), client.Calls);
    }

    [Fact]
    public async Task ThreeResponsesAreCapturedBeforeValidationAndNeverRetried()
    {
        var client = new Client(0); var pauses = 0;
        var importer = Create(client, (_, _) => { pauses++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<FormatException>(() => importer.FetchAsync(InitialImportProfile.PremierLeague, 2026));
        Assert.Equal(3, client.Calls.Count); Assert.Equal(2, pauses);
    }

    [Fact]
    public async Task CancellationBeforeCaptureMakesNoRequests()
    {
        var client = new Client(0); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(client).FetchAsync(InitialImportProfile.PremierLeague, 2026, cancellation.Token));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task CancellationDuringPacingPreventsNextRequest()
    {
        var client = new Client(0); using var cancellation = new CancellationTokenSource();
        var importer = Create(client, (_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.FetchAsync(InitialImportProfile.PremierLeague, 2026, cancellation.Token));
        Assert.Single(client.Calls);
    }

    private static InitialImportFetcher Create(Client client, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var normalizer = new FootballDataNormalizer();
        return new(client, new(normalizer, normalizer), delay);
    }

    private sealed class Client(int failAt) : IFootballDataClient
    {
        public List<string> Calls { get; } = [];
        private Task<FootballDataResponse> Call(string name, string code, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add(name + ":" + code);
            return Task.FromResult(Calls.Count == failAt
                ? new FootballDataResponse(null, new(FootballDataFailureKind.Unauthorized, 401))
                : new FootballDataResponse("{}", null));
        }
        public Task<FootballDataResponse> GetCompetitionAsync(string code, CancellationToken cancellationToken = default) => Call("competition", code, cancellationToken);
        public Task<FootballDataResponse> GetTeamsAsync(string code, CancellationToken cancellationToken = default) => Call("teams", code, cancellationToken);
        public Task<FootballDataResponse> GetMatchesAsync(string code, CancellationToken cancellationToken = default) => Call("matches", code, cancellationToken);
    }
}
