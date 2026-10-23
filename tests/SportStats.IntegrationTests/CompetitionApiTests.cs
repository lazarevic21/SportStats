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

public sealed class CompetitionApiTests
{
    [Theory]
    [InlineData("/api/competitions", 1, 20)]
    [InlineData("/api/competitions?page=3&pageSize=100", 3, 100)]
    public async Task ValidRequestsReturnProjectedPage(string url, int page, int pageSize)
    {
        var competition = new CompetitionResponse(Guid.NewGuid(), "league", "League", Guid.NewGuid(), "football", "Football");
        var reader = new SyntheticReader((request, _) => Task.FromResult(
            new CompetitionPageResponse(new[] { competition }, request.Page, request.PageSize, true)));
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
        Assert.Equal(competition.Id, item.GetProperty("id").GetGuid());
        Assert.Equal("league", item.GetProperty("code").GetString());
        Assert.Equal("League", item.GetProperty("name").GetString());
        Assert.Equal(competition.SportId, item.GetProperty("sportId").GetGuid());
        Assert.Equal("football", item.GetProperty("sportCode").GetString());
        Assert.Equal("Football", item.GetProperty("sportName").GetString());
    }

    [Fact]
    public async Task EmptyPageReturnsSuccessfulEmptyCollection()
    {
        using var factory = CreateFactory(new SyntheticReader((request, _) => Task.FromResult(
            new CompetitionPageResponse(Array.Empty<CompetitionResponse>(), request.Page, request.PageSize, false))));
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/competitions?page=50");
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
    public async Task InvalidPaginationIsSafeAndDoesNotResolveReader(string query)
    {
        using var factory = CreateFactory(null);
        using var client = factory.CreateClient(ClientOptions());
        using var response = await client.GetAsync("/api/competitions?" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-private-token", body);
        Assert.DoesNotContain("Reader must not be resolved", body);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(400, payload.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Invalid pagination.", payload.RootElement.GetProperty("title").GetString());
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
        using var response = await client.GetAsync("/api/competitions");
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
        var request = client.GetAsync("/api/competitions", cancellation.Token);
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

    private static WebApplicationFactory<Program> CreateFactory(ICompetitionReader? reader, string environment = "Production") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:SportStats"] = null }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICompetitionReader>();
                services.AddSingleton<ICompetitionReader>(_ => reader ?? throw new InvalidOperationException("Reader must not be resolved."));
            });
        });

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    };

    private sealed class SyntheticReader(Func<CompetitionPageRequest, CancellationToken, Task<CompetitionPageResponse>> read) : ICompetitionReader
    {
        public int Calls { get; private set; }
        public CompetitionPageRequest? LastRequest { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<CompetitionPageResponse> ReadAsync(CompetitionPageRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastToken = cancellationToken;
            return read(request, cancellationToken);
        }
    }
}
