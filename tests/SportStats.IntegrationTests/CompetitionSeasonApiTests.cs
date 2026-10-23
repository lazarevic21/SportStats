using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SportStats.Application.Competitions;
using Xunit;

namespace SportStats.IntegrationTests;

public sealed class CompetitionSeasonApiTests
{
    [Theory]
    [InlineData("/api/competitions/11111111-1111-1111-1111-111111111111/seasons", 1, 20)]
    [InlineData("/api/competitions/11111111-1111-1111-1111-111111111111/seasons?page=3&pageSize=100", 3, 100)]
    public async Task ValidRequestsReturnProjectedPage(string url, int page, int pageSize)
    {
        var competition = new SeasonResponse(Guid.NewGuid(), Guid.Parse("11111111-1111-1111-1111-111111111111"), "2026", new DateOnly(2026, 8, 1), null);
        var reader = new SyntheticReader((request, _) => Task.FromResult(
            new SeasonPageResponse(new[] { competition }, request.Page, request.PageSize, true)));
        using var factory = CreateFactory(reader);
        using var client = factory.CreateClient(ClientOptions());

        using var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), reader.LastRequest!.CompetitionId);
        Assert.Equal(page, reader.LastRequest.Page);
        Assert.Equal(pageSize, reader.LastRequest.PageSize);
        Assert.True(reader.LastToken.CanBeCanceled);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        Assert.Equal(4, root.EnumerateObject().Count());
        Assert.Equal(page, root.GetProperty("page").GetInt32());
        Assert.Equal(pageSize, root.GetProperty("pageSize").GetInt32());
        Assert.True(root.GetProperty("hasNextPage").GetBoolean());
        var item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(competition.Id, item.GetProperty("id").GetGuid());
        Assert.Equal(competition.CompetitionId, item.GetProperty("competitionId").GetGuid());
        Assert.Equal("2026", item.GetProperty("key").GetString());
        Assert.Equal("2026-08-01", item.GetProperty("startDate").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("endDate").ValueKind);
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", 1)]
    [InlineData("22222222-2222-2222-2222-222222222222", 1)]
    [InlineData("11111111-1111-1111-1111-111111111111", 50)]
    public async Task MissingCompetitionOrSeasonPageReturnsSuccessfulEmptyCollection(string id, int page)
    {
        using var factory = CreateFactory(new SyntheticReader((request, _) => Task.FromResult(
            new SeasonPageResponse(Array.Empty<SeasonResponse>(), request.Page, request.PageSize, false))));
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync($"/api/competitions/{id}/seasons?page={page}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(payload.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(payload.RootElement.GetProperty("hasNextPage").GetBoolean());
    }

    [Theory]
    [InlineData("synthetic-private-token")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidCompetitionIdIsSafeAndDoesNotResolveReader(string id)
    {
        using var factory = CreateFactory(null);
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync($"/api/competitions/{id}/seasons");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(id, body);
        Assert.DoesNotContain("Reader must not be resolved", body);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(400, payload.RootElement.GetProperty("status").GetInt32());
        Assert.True(payload.RootElement.GetProperty("errors").TryGetProperty("competitionId", out _));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=-1")]
    [InlineData("pageSize=101")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("page=2147483648")]
    [InlineData("page=synthetic-private-token")]
    [InlineData("pageSize=1.5")]
    [InlineData("page=+1")]
    [InlineData("pageSize=%2020")]
    [InlineData("page=")]
    [InlineData("pageSize=")]
    [InlineData("page=1&page=2")]
    [InlineData("pageSize=20&pageSize=20")]
    public async Task InvalidPaginationIsSafeAndDoesNotResolveReader(string query)
    {
        using var factory = CreateFactory(null);
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/competitions/11111111-1111-1111-1111-111111111111/seasons?" + query);
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
        using var response = await client.GetAsync("/api/competitions/11111111-1111-1111-1111-111111111111/seasons");
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
        var request = client.GetAsync("/api/competitions/11111111-1111-1111-1111-111111111111/seasons", cancellation.Token);
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

    private static WebApplicationFactory<Program> CreateFactory(ICompetitionSeasonReader? reader, string environment = "Production") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:SportStats"] = null }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICompetitionSeasonReader>();
                services.AddSingleton<ICompetitionSeasonReader>(_ => reader ?? throw new InvalidOperationException("Reader must not be resolved."));
            });
        });

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    };

    private sealed class SyntheticReader(Func<CompetitionSeasonPageRequest, CancellationToken, Task<SeasonPageResponse>> read) : ICompetitionSeasonReader
    {
        public int Calls { get; private set; }
        public CompetitionSeasonPageRequest? LastRequest { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<SeasonPageResponse> ReadAsync(CompetitionSeasonPageRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastToken = cancellationToken;
            return read(request, cancellationToken);
        }
    }
}
