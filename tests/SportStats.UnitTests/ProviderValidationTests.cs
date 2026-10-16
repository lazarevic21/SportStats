using SportStats.Application.Football.Ingestion;
using SportStats.Infrastructure.FootballData;
using SportStats.ProviderValidation;
using Xunit;

namespace SportStats.UnitTests;

public sealed class ProviderValidationTests
{
    private const string Competition = """
        {"id":2021,"code":"PL","name":"Secret competition name","currentSeason":{"id":2502,"startDate":"2026-08-21","endDate":"2027-05-30"}}
        """;
    private const string Teams = """
        {"competition":{"id":2021,"code":"PL","name":"Secret competition name"},"count":2,"teams":[{"id":57,"name":"Secret team name"},{"id":65,"name":"Other team"}]}
        """;
    private const string Matches = """
        {"competition":{"id":2021,"code":"PL","name":"Secret competition name"},"resultSet":{"count":1},"matches":[{
        "id":560542,"competition":{"id":2021,"code":"PL","name":"Secret competition name"},
        "season":{"id":2502,"startDate":"2026-08-21","endDate":"2027-05-30"},
        "homeTeam":{"id":57},"awayTeam":{"id":65},
        "utcDate":"2026-08-21T19:00:00Z","matchday":1,"status":"TIMED",
        "lastUpdated":"2026-08-20T12:00:00Z","score":{"fullTime":{"home":0,"away":null}}}]}
        """;

    [Fact]
    public void CommandLineDefaultsRequireSeparateExplicitOptIn()
    {
        Assert.True(ValidationOptions.TryParse([], out var defaults));
        Assert.False(defaults.Live);
        Assert.Equal("PL", defaults.Competition);
        Assert.True(ValidationOptions.TryParse(["--live", "--competition", "BL1", "--year", "2026"], out var selected));
        Assert.True(selected.Live);
        Assert.Equal("BL1", selected.Competition);
        Assert.Equal(2026, selected.Year);
    }

    [Theory]
    [InlineData("--token", "credential")]
    [InlineData("--competition", "PL/../../secret")]
    [InlineData("--competition", "PL\nsecret")]
    [InlineData("--year", "invalid")]
    [InlineData("--year", "1999")]
    [InlineData("--spacing-seconds", "6")]
    [InlineData("--spacing-seconds", "301")]
    [InlineData("--live", "--live")]
    public void InvalidCommandLineIsRejected(string first, string second) =>
        Assert.False(ValidationOptions.TryParse([first, second], out _));

    [Fact]
    public async Task MissingOptInDoesNotCallProviderOrDelay()
    {
        var client = new FakeClient();
        var delays = new List<TimeSpan>();
        var runner = Runner(client, delays);
        var result = await runner.RunAsync(new(false), new StringWriter(), new StringReader(""));
        Assert.NotEqual(0, result);
        Assert.Empty(client.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task SuccessMakesOnlyThreeSequentialRequestsAndSanitizesPayload()
    {
        var client = SuccessClient();
        var delays = new List<TimeSpan>();
        var output = new StringWriter();
        var result = await Runner(client, delays).RunAsync(new(true), output, new StringReader(""));
        Assert.Equal(0, result);
        Assert.Equal(new[] { "competition:PL", "teams:PL", "matches:PL" }, client.Calls);
        Assert.Equal(2, delays.Count);
        Assert.All(delays, delay => Assert.True(delay >= TimeSpan.FromSeconds(7)));
        Assert.DoesNotContain("Secret", output.ToString());
        Assert.DoesNotContain("560542", output.ToString());
        Assert.DoesNotContain(Matches, output.ToString());
    }

    [Theory]
    [InlineData(FootballDataFailureKind.RateLimited)]
    [InlineData(FootballDataFailureKind.Unauthorized)]
    [InlineData(FootballDataFailureKind.ProviderFailure)]
    public async Task ProviderFailureStopsWithoutRetry(FootballDataFailureKind failure)
    {
        var client = new FakeClient();
        client.Responses.Enqueue(new(null, new(failure, 429, TimeSpan.FromSeconds(30))));
        var delays = new List<TimeSpan>();
        var output = new StringWriter();
        Assert.NotEqual(0, await Runner(client, delays).RunAsync(new(true), output, new StringReader("")));
        Assert.Single(client.Calls);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task CompareRequiresManualConfirmation()
    {
        var client = SuccessClient();
        await Runner(client, []).RunAsync(new(true, Compare: true), new StringWriter(), new StringReader(""));
        Assert.Equal(3, client.Calls.Count);
    }

    [Fact]
    public async Task ManuallyConfirmedComparisonFetchesExactlyOneAdditionalObservation()
    {
        var client = SuccessClient();
        foreach (var json in new[] { Competition, Teams, Matches }) client.Responses.Enqueue(new(json, null));
        var delays = new List<TimeSpan>();
        var output = new StringWriter();
        Assert.Equal(0, await Runner(client, delays).RunAsync(new(true, Compare: true), output, new StringReader("compare\ncompare\n")));
        Assert.Equal(6, client.Calls.Count);
        Assert.Equal(5, delays.Count);
        Assert.Contains("changed observations=0", output.ToString());
        Assert.Contains("no freshness or monotonicity conclusion", output.ToString());
    }

    [Fact]
    public async Task ConfigurableCompetitionDoesNotReceiveIncorrectPlProvenance()
    {
        var client = SuccessClient();
        var output = new StringWriter();
        Assert.Equal(0, await Runner(client, []).RunAsync(new(true, "BL1"), output, new StringReader("")));
        Assert.All(client.Calls, call => Assert.EndsWith(":BL1", call));
        Assert.Contains("normalization unsupported", output.ToString());
        Assert.DoesNotContain("normalized=", output.ToString());
    }

    [Fact]
    public async Task SummaryPreservesZeroNullAndMissingCountsWithoutRawUnknownStatus()
    {
        var json = Matches.Replace("\"utcDate\":\"2026-08-21T19:00:00Z\",", "")
            .Replace("\"matchday\":1", "\"matchday\":null")
            .Replace("TIMED", "Secret untrusted status");
        var client = SuccessClient();
        client.Responses.Clear();
        foreach (var payload in new[] { Competition, Teams, json }) client.Responses.Enqueue(new(payload, null));
        var output = new StringWriter();
        Assert.Equal(0, await Runner(client, []).RunAsync(new(true), output, new StringReader("")));
        var summary = output.ToString();
        Assert.Contains("kickoff: missing=1; null=0; value=0", summary);
        Assert.Contains("matchday: missing=0; null=1; value=0", summary);
        Assert.Contains("homeGoals: missing=0; null=0; value=1", summary);
        Assert.Contains("awayGoals: missing=0; null=1; value=0", summary);
        Assert.Contains("explicit zero home=1", summary);
        Assert.Contains("raw status other: 1", summary);
        Assert.Contains("canonical status Unknown: 1", summary);
        Assert.DoesNotContain("Secret", summary);
    }

    [Theory]
    [InlineData("not-a-timestamp")]
    [InlineData("2026-08-20T12:00:00+02:00")]
    [InlineData("2026-08-20T12:00:00")]
    public async Task InvalidLastUpdatedIsRejectedWithoutPrintingItsValue(string timestamp)
    {
        var client = new FakeClient();
        foreach (var json in new[] { Competition, Teams, Matches.Replace("2026-08-20T12:00:00Z", timestamp) })
            client.Responses.Enqueue(new(json, null));
        var output = new StringWriter();
        Assert.NotEqual(0, await Runner(client, []).RunAsync(new(true), output, new StringReader("")));
        Assert.Contains("invalid observation", output.ToString());
        Assert.DoesNotContain(timestamp, output.ToString());
    }

    [Fact]
    public async Task MalformedPayloadStopsBeforeOtherEndpointsAndSuppressesPayload()
    {
        var client = new FakeClient();
        client.Responses.Enqueue(new("{Secret credential and malformed payload", null));
        var output = new StringWriter();
        Assert.NotEqual(0, await Runner(client, []).RunAsync(new(true), output, new StringReader("")));
        Assert.Single(client.Calls);
        Assert.DoesNotContain("Secret", output.ToString());
        Assert.Contains("invalid observation", output.ToString());
    }

    [Fact]
    public async Task AlreadyCancelledRequestNeverCallsProvider()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = SuccessClient();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(client, []).RunAsync(
            new(true), new StringWriter(), new StringReader(""), cancellation.Token));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task CancellationDuringSpacingDoesNotStartNextRequest()
    {
        var client = SuccessClient();
        using var cancellation = new CancellationTokenSource();
        var runner = new ProviderValidationRunner(client, new FootballDataNormalizer(), (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new(true),
            new StringWriter(), new StringReader(""), cancellation.Token));
        Assert.Single(client.Calls);
    }

    private static ProviderValidationRunner Runner(FakeClient client, List<TimeSpan> delays) =>
        new(client, new FootballDataNormalizer(), (duration, token) =>
        {
            token.ThrowIfCancellationRequested();
            delays.Add(duration);
            return Task.CompletedTask;
        });

    private static FakeClient SuccessClient()
    {
        var client = new FakeClient();
        foreach (var json in new[] { Competition, Teams, Matches }) client.Responses.Enqueue(new(json, null));
        return client;
    }

    private sealed class FakeClient : IFootballDataClient
    {
        public Queue<FootballDataResponse> Responses { get; } = new();
        public List<string> Calls { get; } = [];
        private Task<FootballDataResponse> Get(string endpoint, string code, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add($"{endpoint}:{code}");
            return Task.FromResult(Responses.Dequeue());
        }
        public Task<FootballDataResponse> GetCompetitionAsync(string competitionCode, CancellationToken cancellationToken = default) => Get("competition", competitionCode, cancellationToken);
        public Task<FootballDataResponse> GetTeamsAsync(string competitionCode, CancellationToken cancellationToken = default) => Get("teams", competitionCode, cancellationToken);
        public Task<FootballDataResponse> GetMatchesAsync(string competitionCode, CancellationToken cancellationToken = default) => Get("matches", competitionCode, cancellationToken);
    }
}
