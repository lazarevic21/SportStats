using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using SportStats.Application.Football.Ingestion;
using SportStats.Infrastructure;
using SportStats.Infrastructure.FootballData;
using Xunit;

namespace SportStats.UnitTests;

public sealed class FootballDataHttpClientTests
{
    private const string Token = "test-secret-never-log";

    [Theory]
    [InlineData("competition", "PL", "https://api.football-data.org/v4/competitions/PL")]
    [InlineData("teams", "BL1", "https://api.football-data.org/v4/competitions/BL1/teams")]
    [InlineData("matches", "SA", "https://api.football-data.org/v4/competitions/SA/matches")]
    public async Task RequestsUseExpectedEndpointAndPerRequestAuthentication(string endpoint, string code, string expected)
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(expected, request.RequestUri!.AbsoluteUri);
            Assert.Equal(Token, Assert.Single(request.Headers.GetValues("X-Auth-Token")));
            Assert.Null(request.Content);
            return Task.FromResult(Response("{\"id\":2021}"));
        });
        using var http = new HttpClient(handler);
        var client = Client(http);
        var result = endpoint switch
        {
            "teams" => await client.GetTeamsAsync(code),
            "matches" => await client.GetMatchesAsync(code),
            _ => await client.GetCompetitionAsync(code)
        };
        Assert.True(result.IsSuccess);
        Assert.Equal("{\"id\":2021}", result.Json);
        Assert.Null(result.Failure);
        Assert.False(http.DefaultRequestHeaders.Contains("X-Auth-Token"));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401, FootballDataFailureKind.Unauthorized)]
    [InlineData(403, FootballDataFailureKind.Forbidden)]
    [InlineData(404, FootballDataFailureKind.NotFound)]
    [InlineData(429, FootballDataFailureKind.RateLimited)]
    [InlineData(500, FootballDataFailureKind.ProviderFailure)]
    [InlineData(503, FootballDataFailureKind.ProviderFailure)]
    [InlineData(302, FootballDataFailureKind.UnexpectedStatus)]
    [InlineData(400, FootballDataFailureKind.UnexpectedStatus)]
    public async Task FailuresAreTypedSanitizedAndNeverRetried(int status, FootballDataFailureKind kind)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(Token, (HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        var result = await Client(http).GetMatchesAsync("PL");
        Assert.False(result.IsSuccess);
        Assert.Null(result.Json);
        Assert.Equal(kind, result.Failure!.Kind);
        Assert.Equal(status, result.Failure.StatusCode);
        Assert.DoesNotContain(Token, result.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not-json")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    public async Task InvalidBodiesAreRejected(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(body))));
        var result = await Client(http).GetTeamsAsync("PL");
        Assert.Equal(FootballDataFailureKind.InvalidResponse, result.Failure!.Kind);
        Assert.Null(result.Json);
    }

    [Fact]
    public async Task RetryAfterDeltaIsPreserved()
    {
        var response = Response("", HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(response)));
        var result = await Client(http).GetMatchesAsync("PL");
        Assert.Equal(TimeSpan.FromSeconds(45), result.Failure!.RetryAfter);
    }

    [Fact]
    public async Task MalformedRetryAfterStillReturnsRateLimit()
    {
        var response = Response("", HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "invalid");
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(response)));
        var result = await Client(http).GetMatchesAsync("PL");
        Assert.Equal(FootballDataFailureKind.RateLimited, result.Failure!.Kind);
        Assert.Null(result.Failure.RetryAfter);
    }

    [Fact]
    public async Task RetryAfterDateIsPreservedWithoutLocalClockComparison()
    {
        var date = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var response = Response("", HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(date);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(response)));
        var result = await Client(http).GetMatchesAsync("PL");
        Assert.Equal(date, result.Failure!.RetryAfterUtc);
        Assert.Null(result.Failure.RetryAfter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("secret with space")]
    [InlineData("secret\r\nInjected: yes")]
    [InlineData("secret\u007f")]
    public void InvalidTokenFailsWithoutEchoingCredential(string token)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response("{}"))));
        var exception = Assert.Throws<InvalidOperationException>(() => new FootballDataClient(http,
            Options.Create(new FootballDataOptions { ApiToken = token })));
        if (token.Length > 0) Assert.DoesNotContain(token, exception.ToString());
    }

    [Theory]
    [InlineData(0, 1024)]
    [InlineData(301, 1024)]
    [InlineData(30, 0)]
    [InlineData(30, 16777217)]
    public void InvalidLimitsFailBeforeSending(int seconds, int bytes)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response("{}"))));
        Assert.Throws<InvalidOperationException>(() => new FootballDataClient(http,
            Options.Create(new FootballDataOptions { ApiToken = Token, TimeoutSeconds = seconds, MaxResponseBytes = bytes })));
    }

    [Fact]
    public async Task BodyReadCancellationPropagatesAfterHeaders()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new BlockingStream(() => cancellation.Cancel());
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        })));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).GetMatchesAsync("PL", cancellation.Token));
        Assert.True(stream.ReadAttempted);
    }

    [Fact]
    public async Task BodyIoFailureIsSanitized()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new BlockingStream(() => throw new IOException(Token)))
        })));
        var result = await Client(http).GetMatchesAsync("PL");
        Assert.Equal(FootballDataFailureKind.NetworkFailure, result.Failure!.Kind);
        Assert.DoesNotContain(Token, result.ToString());
    }

    [Fact]
    public async Task ConfiguredTimeoutCoversBodyRead()
    {
        var stream = new BlockingStream(() => { });
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        })));
        var client = new FootballDataClient(http, Options.Create(new FootballDataOptions { ApiToken = Token, TimeoutSeconds = 1 }));
        var result = await client.GetMatchesAsync("PL").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(FootballDataFailureKind.Timeout, result.Failure!.Kind);
        Assert.True(stream.ReadAttempted);
    }

    [Fact]
    public async Task UnknownLengthBodyStillHonorsResponseLimit()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"large\":\"" + new string('x', 200) + "\"}"));
        var content = new StreamContent(stream);
        content.Headers.ContentLength = null;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        var client = new FootballDataClient(http, Options.Create(new FootballDataOptions { ApiToken = Token, MaxResponseBytes = 64 }));
        var result = await client.GetMatchesAsync("PL");
        Assert.Equal(FootballDataFailureKind.InvalidResponse, result.Failure!.Kind);
    }

    [Fact]
    public void FactoryDisablesRedirectsAndCookies()
    {
        var services = new ServiceCollection();
        services.AddFootballDataClient(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(typeof(IFootballDataClient).Name);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        var primary = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseCookies);
    }

    [Fact]
    public async Task FactoryClientDoesNotLogCredentialsOrPayload()
    {
        const string payloadMarker = "private-provider-response";
        var logs = new LogCollector();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddFootballDataClient(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FootballData:ApiToken"] = Token
        }).Build());
        services.AddHttpClient<IFootballDataClient, FootballDataClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new Handler((_, _) => Task.FromResult(Response(payloadMarker, HttpStatusCode.Forbidden))));
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IFootballDataClient>().GetMatchesAsync("PL");
        Assert.Equal(FootballDataFailureKind.Forbidden, result.Failure!.Kind);
        Assert.DoesNotContain(Token, string.Join("\n", logs.Messages));
        Assert.DoesNotContain(payloadMarker, string.Join("\n", logs.Messages));
    }

    [Fact]
    public async Task NetworkExceptionsDoNotExposeTheirMessage()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException(Token));
        using var http = new HttpClient(handler);
        var result = await Client(http).GetCompetitionAsync("PL");
        Assert.Equal(FootballDataFailureKind.NetworkFailure, result.Failure!.Kind);
        Assert.DoesNotContain(Token, result.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("../PL")]
    [InlineData("PL?token=secret")]
    [InlineData("https://evil.example")]
    [InlineData("PL/teams")]
    [InlineData("")]
    [InlineData("PL\r\nX:evil")]
    public async Task InvalidCompetitionCannotChangeRequestTarget(string code)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response("{}")));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Client(http).GetMatchesAsync(code));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotSend()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response("{}")));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).GetMatchesAsync("PL", cancellation.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationDuringSendPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler((_, token) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(Token, new Exception(Token), token);
        });
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).GetMatchesAsync("PL", cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Token, exception.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TransportTimeoutIsTypedRatherThanCallerCancellation()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("timeout", new TimeoutException())));
        var result = await Client(http).GetMatchesAsync("PL");
        Assert.Equal(FootballDataFailureKind.Timeout, result.Failure!.Kind);
    }

    [Fact]
    public async Task OversizedBodyIsRejected()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response("{\"large\":\"" + new string('x', 200) + "\"}"))));
        var client = new FootballDataClient(http, Options.Create(new FootballDataOptions { ApiToken = Token, MaxResponseBytes = 64 }));
        var result = await client.GetMatchesAsync("PL");
        Assert.Equal(FootballDataFailureKind.InvalidResponse, result.Failure!.Kind);
    }

    [Fact]
    public void InfrastructureRegistrationDoesNotRequireProviderOrDatabaseSecrets()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IFootballMatchObservationNormalizer>());
    }

    [Fact]
    public void MissingTokenOnlyFailsWhenClientIsResolved()
    {
        var services = new ServiceCollection();
        services.AddFootballDataClient(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IFootballDataClient>());
        Assert.Contains("token configuration", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("TimeoutSeconds")]
    [InlineData("MaxResponseBytes")]
    public void MalformedNumericConfigurationIsLazyAndSanitized(string key)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FootballData:ApiToken"] = Token,
            [$"FootballData:{key}"] = Token
        }).Build();
        var services = new ServiceCollection();
        services.AddFootballDataClient(configuration);
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IFootballDataClient>());
        Assert.DoesNotContain(Token, exception.ToString());
        Assert.Null(exception.InnerException);
    }

    private static FootballDataClient Client(HttpClient http) => new(http, Options.Create(new FootballDataOptions { ApiToken = Token }));
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }

    private sealed class BlockingStream(Action onRead) : Stream
    {
        public bool ReadAttempted { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            onRead();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LogCollector : ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
        public void Dispose() { }
    }
}
