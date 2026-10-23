using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SportStats.Application.Football.Teams;
using Xunit;

namespace SportStats.IntegrationTests;

public sealed class TeamApiTests
{
    [Theory]
    [InlineData("/api/teams", 1, 20)]
    [InlineData("/api/teams?page=3&pageSize=100", 3, 100)]
    [InlineData("/api/teams?PAGE=2&PAGESIZE=1", 2, 1)]
    public async Task ValidRequestsReturnProjectedPage(string url, int page, int pageSize)
    {
        var team = new TeamResponse(Guid.NewGuid(), "Team", "TM", "https://example.invalid/crest.png");
        var reader = new SyntheticReader((request, _) => Task.FromResult(
            new TeamPageResponse(new[] { team }, request.Page, request.PageSize, true)));
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
        Assert.Equal(team.Id, item.GetProperty("id").GetGuid());
        Assert.Equal("Team", item.GetProperty("name").GetString());
        Assert.Equal("TM", item.GetProperty("shortName").GetString());
        Assert.Equal("https://example.invalid/crest.png", item.GetProperty("crestUrl").GetString());
        Assert.Equal(4, item.EnumerateObject().Count());
    }

    [Fact]
    public async Task NullableAndEmptyOptionalIdentityFieldsArePreserved()
    {
        var teams = new[]
        {
            new TeamResponse(Guid.NewGuid(), "First", null, null),
            new TeamResponse(Guid.NewGuid(), "Second", "", "")
        };
        using var factory = CreateFactory(new SyntheticReader((request, _) => Task.FromResult(
            new TeamPageResponse(teams, request.Page, request.PageSize, false))));
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/teams");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("shortName").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("crestUrl").ValueKind);
        Assert.Equal("", items[1].GetProperty("shortName").GetString());
        Assert.Equal("", items[1].GetProperty("crestUrl").GetString());
    }

    [Theory]
    [InlineData("competitionId=synthetic-private-value")]
    [InlineData("seasonId=synthetic-private-value")]
    [InlineData("synthetic-private-key=synthetic-private-value")]
    public async Task UnsupportedQueryParametersAreRejectedBeforeReaderResolution(string query)
    {
        using var factory = CreateFactory(null);
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/teams?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-private", body);
        Assert.DoesNotContain("Reader must not be resolved", body);
        Assert.DoesNotContain("competitionId", body);
        Assert.DoesNotContain("seasonId", body);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(400, payload.RootElement.GetProperty("status").GetInt32());
        var errors = payload.RootElement.GetProperty("errors");
        Assert.Single(errors.EnumerateObject());
        Assert.True(errors.TryGetProperty("query", out var messages));
        Assert.Equal("Only page and pageSize query parameters are supported.",
            Assert.Single(messages.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task EmptyPageReturnsSuccessfulEmptyCollection()
    {
        using var factory = CreateFactory(new SyntheticReader((request, _) => Task.FromResult(
            new TeamPageResponse(Array.Empty<TeamResponse>(), request.Page, request.PageSize, false))));
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/teams?page=50");
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
        using var response = await client.GetAsync("/api/teams?" + query);
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
        using var response = await client.GetAsync("/api/teams");
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
        var request = client.GetAsync("/api/teams", cancellation.Token);
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

    private static WebApplicationFactory<Program> CreateFactory(ITeamReader? reader, string environment = "Production") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:SportStats"] = null }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITeamReader>();
                services.AddSingleton<ITeamReader>(_ => reader ?? throw new InvalidOperationException("Reader must not be resolved."));
            });
        });

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    };

    private sealed class SyntheticReader(Func<TeamPageRequest, CancellationToken, Task<TeamPageResponse>> read) : ITeamReader
    {
        public int Calls { get; private set; }
        public TeamPageRequest? LastRequest { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<TeamPageResponse> ReadAsync(TeamPageRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastToken = cancellationToken;
            return read(request, cancellationToken);
        }
    }
}
