using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SportStats.Application.Football.Matches;
using Xunit;

namespace SportStats.IntegrationTests;

public sealed class MatchApiTests
{
    [Theory]
    [InlineData("/api/matches", 1, 20)]
    [InlineData("/api/matches?page=3&pageSize=100", 3, 100)]
    [InlineData("/api/matches?PAGE=2&PAGESIZE=1", 2, 1)]
    public async Task ValidRequestsReturnProjectedPage(string url, int page, int pageSize)
    {
        var match = SampleMatch();
        var reader = new SyntheticReader((request, _) => Task.FromResult(
            new MatchPageResponse(new[] { match }, request.Page, request.PageSize, true)));
        using var factory = CreateFactory(reader);
        using var client = factory.CreateClient(ClientOptions());

        using var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(page, reader.LastRequest!.Page);
        Assert.Equal(pageSize, reader.LastRequest.PageSize);
        Assert.True(reader.LastToken.CanBeCanceled);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.Equal(4, root.EnumerateObject().Count());
        Assert.Equal(page, root.GetProperty("page").GetInt32());
        Assert.Equal(pageSize, root.GetProperty("pageSize").GetInt32());
        Assert.True(root.GetProperty("hasNextPage").GetBoolean());
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(match.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(match.SeasonId, item.GetProperty("seasonId").GetGuid());
        Assert.Equal(match.HomeTeam.Id, item.GetProperty("homeTeam").GetProperty("id").GetGuid());
        Assert.Equal("Home", item.GetProperty("homeTeam").GetProperty("name").GetString());
        Assert.Equal("Away", item.GetProperty("awayTeam").GetProperty("name").GetString());
        Assert.Equal("Finished", item.GetProperty("status").GetString());
        Assert.Equal(2, item.GetProperty("homeGoals").GetInt32());
        Assert.Equal(1, item.GetProperty("awayGoals").GetInt32());
        Assert.Equal(5, item.GetProperty("matchday").GetInt32());
        Assert.Equal(match.KickoffUtc, item.GetProperty("kickoffUtc").GetDateTime());
    }

    [Fact]
    public async Task UnknownDateAndScoreRemainNull()
    {
        var match = SampleMatch() with { KickoffUtc = null, HomeGoals = null, AwayGoals = null, Matchday = null, Status = "Unknown" };
        using var factory = CreateFactory(new SyntheticReader((request, _) => Task.FromResult(
            new MatchPageResponse(new[] { match }, request.Page, request.PageSize, false))));
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/matches");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
        foreach (var field in new[] { "kickoffUtc", "homeGoals", "awayGoals", "matchday" })
            Assert.Equal(JsonValueKind.Null, item.GetProperty(field).ValueKind);
        Assert.Equal("Unknown", item.GetProperty("status").GetString());
    }
    [Theory]
    [InlineData("competitionId=synthetic-private-value")]
    [InlineData("status=synthetic-private-value")]
    [InlineData("synthetic-private-key=synthetic-private-value")]
    public async Task UnsupportedQueryParametersAreRejectedBeforeReaderResolution(string query)
    {
        using var factory = CreateFactory(null);
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/matches?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-private", body);
        Assert.DoesNotContain("Reader must not be resolved", body);
        Assert.DoesNotContain("competitionId", body);

        using var payload = JsonDocument.Parse(body);
        Assert.Equal(400, payload.RootElement.GetProperty("status").GetInt32());
        var errors = payload.RootElement.GetProperty("errors");
        Assert.Single(errors.EnumerateObject());
        Assert.True(errors.TryGetProperty("query", out var messages));
        Assert.Equal("Only page, pageSize, seasonId, teamId, from and to query parameters are supported.",
            Assert.Single(messages.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task EmptyPageReturnsSuccessfulEmptyCollection()
    {
        using var factory = CreateFactory(new SyntheticReader((request, _) => Task.FromResult(
            new MatchPageResponse(Array.Empty<MatchResponse>(), request.Page, request.PageSize, false))));
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/matches?page=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(payload.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(payload.RootElement.GetProperty("hasNextPage").GetBoolean());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("page=2147483648")]
    [InlineData("page=synthetic-private-token")]
    [InlineData("pageSize=1.5")]
    [InlineData("page=")]
    [InlineData("pageSize=")]
    [InlineData("page=1&page=2")]
    [InlineData("pageSize=20&pageSize=20")]
    [InlineData("page=1&PAGE=2")]
    [InlineData("page=+1")]
    [InlineData("page=%201")]
    public async Task InvalidPaginationIsSafeAndDoesNotResolveReader(string query)
    {
        using var factory = CreateFactory(null);
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/matches?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-private-token", body);
        Assert.DoesNotContain("Reader must not be resolved", body);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(400, payload.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Invalid request.", payload.RootElement.GetProperty("title").GetString());
        var errors = payload.RootElement.GetProperty("errors").EnumerateObject().ToArray();
        Assert.NotEmpty(errors);
        foreach (var error in errors)
        {
            Assert.Contains(error.Name, new[] { "page", "pageSize" });
            foreach (var message in error.Value.EnumerateArray())
                Assert.Contains(message.GetString(), new[]
                {
                    "Supply one supported integer value.",
                    "Page size must be between 1 and 100.",
                    "Page must be positive and its offset must be supported."
                });
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task UnexpectedFailuresAreSanitizedInEveryEnvironment(string environment)
    {
        const string secret = "synthetic-private-reader-failure";
        using var factory = CreateFactory(new SyntheticReader((_, _) => throw new InvalidOperationException(secret)), environment);
        using var client = factory.CreateClient(ClientOptions());
        client.DefaultRequestHeaders.Accept.ParseAdd("application/problem+json");
        using var response = await client.GetAsync("/api/matches");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("The server could not complete the request.", payload.RootElement.GetProperty("detail").GetString());
        Assert.False(payload.RootElement.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task RequestCancellationReachesReader()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = CreateFactory(new SyntheticReader(async (_, token) =>
        {
            entered.TrySetResult(token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                canceled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("Unreachable synthetic reader branch.");
        }));
        using var client = factory.CreateClient(ClientOptions());
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync("/api/matches", cancellation.Token);
        var readerToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(readerToken.IsCancellationRequested);
    }

    [Fact]
    public async Task HealthDoesNotResolveReaderOrDatabaseConfiguration()
    {
        using var factory = CreateFactory(null);
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("SportStats"));
    }

    [Theory]
    [InlineData("seasonId=")]
    [InlineData("seasonId=synthetic-private-token")]
    [InlineData("teamId=00000000-0000-0000-0000-000000000000")]
    [InlineData("teamId=11111111-1111-1111-1111-111111111111&TEAMID=11111111-1111-1111-1111-111111111111")]
    [InlineData("from=2026-02-30")]
    [InlineData("from=2026-1-01")]
    [InlineData("from=2026-01-01T00:00:00Z")]
    [InlineData("from=")]
    [InlineData("to=synthetic-private-token")]
    [InlineData("from=2026-01-01&from=2026-01-01")]
    [InlineData("from=2026-01-02&to=2026-01-01")]
    [InlineData("from=2026-01-01&to=2026-01-01")]
    public async Task InvalidFiltersAreSanitizedBeforeReaderResolution(string query)
    {
        using var factory = CreateFactory(null);
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/matches?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-private", body);
        Assert.DoesNotContain("Reader must not be resolved", body);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("Invalid request.", payload.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("seasonId=11111111-1111-1111-1111-111111111111&teamId=22222222-2222-2222-2222-222222222222&from=2026-01-01&to=2026-02-01")]
    [InlineData("SEASONID=11111111-1111-1111-1111-111111111111&TEAMID=22222222-2222-2222-2222-222222222222&FROM=2026-01-01&TO=2026-02-01")]
    public async Task CombinedFiltersReachReader(string query)
    {
        var reader = new SyntheticReader((request, _) => Task.FromResult(
            new MatchPageResponse(Array.Empty<MatchResponse>(), request.Page, request.PageSize, false)));
        using var factory = CreateFactory(reader);
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/matches?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), reader.LastRequest!.SeasonId);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), reader.LastRequest.TeamId);
        Assert.Equal(new DateOnly(2026, 1, 1), reader.LastRequest.From);
        Assert.Equal(new DateOnly(2026, 2, 1), reader.LastRequest.To);
    }

    private static WebApplicationFactory<Program> CreateFactory(IMatchReader? reader, string environment = "Production") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:SportStats"] = null }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMatchReader>();
                services.AddSingleton<IMatchReader>(_ => reader ?? throw new InvalidOperationException("Reader must not be resolved."));
            });
        });

    private static MatchResponse SampleMatch() => new(Guid.NewGuid(), Guid.NewGuid(),
        new MatchTeamResponse(Guid.NewGuid(), "Home"), new MatchTeamResponse(Guid.NewGuid(), "Away"),
        new DateTime(2026, 10, 23, 18, 0, 0, DateTimeKind.Utc), "Finished", 5, 2, 1);

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    };

    private sealed class SyntheticReader(Func<MatchPageRequest, CancellationToken, Task<MatchPageResponse>> read) : IMatchReader
    {
        public int Calls { get; private set; }
        public MatchPageRequest? LastRequest { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<MatchPageResponse> ReadAsync(MatchPageRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastToken = cancellationToken;
            return read(request, cancellationToken);
        }
    }
}
