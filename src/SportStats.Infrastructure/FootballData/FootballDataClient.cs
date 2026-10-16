using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SportStats.Application.Football.Ingestion;

namespace SportStats.Infrastructure.FootballData;

/// <summary>Single-attempt transport. No normalization, retries, persistence or freshness decisions.</summary>
public sealed class FootballDataClient : IFootballDataClient
{
    private readonly HttpClient client;
    private readonly string token;
    private readonly TimeSpan timeout;
    private readonly int maxResponseBytes;

    public FootballDataClient(HttpClient client, IOptions<FootballDataOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiToken) || settings.ApiToken.Any(c => c < 33 || c > 126))
            throw new InvalidOperationException("FootballData API token configuration is missing or invalid.");
        if (settings.TimeoutSeconds is < 1 or > 300)
            throw new InvalidOperationException("FootballData timeout must be between 1 and 300 seconds.");
        if (settings.MaxResponseBytes is < 1 or > 16 * 1024 * 1024)
            throw new InvalidOperationException("FootballData response limit must be between 1 byte and 16 MiB.");
        this.client = client;
        token = settings.ApiToken;
        timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        maxResponseBytes = settings.MaxResponseBytes;
    }

    public Task<FootballDataResponse> GetCompetitionAsync(string competitionCode, CancellationToken cancellationToken = default)
        => GetAsync(competitionCode, "", cancellationToken);
    public Task<FootballDataResponse> GetTeamsAsync(string competitionCode, CancellationToken cancellationToken = default)
        => GetAsync(competitionCode, "/teams", cancellationToken);
    public Task<FootballDataResponse> GetMatchesAsync(string competitionCode, CancellationToken cancellationToken = default)
        => GetAsync(competitionCode, "/matches", cancellationToken);

    private async Task<FootballDataResponse> GetAsync(string competitionCode, string suffix, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(competitionCode) || competitionCode.Length > 64 ||
            competitionCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("A competition code or ID must be a simple provider identifier.", nameof(competitionCode));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.football-data.org/v4/competitions/{competitionCode}{suffix}");
        request.Headers.Add("X-Auth-Token", token);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
                return Failure(MapStatus(response.StatusCode), (int)response.StatusCode,
                    response.StatusCode == HttpStatusCode.TooManyRequests ? response.Headers.RetryAfter?.Delta : null,
                    response.StatusCode == HttpStatusCode.TooManyRequests ? response.Headers.RetryAfter?.Date : null);
            if (response.Content.Headers.ContentLength > maxResponseBytes)
                return Failure(FootballDataFailureKind.InvalidResponse);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), deadline.Token);
                if (read == 0) break;
                if (body.Length + read > maxResponseBytes)
                    return Failure(FootballDataFailureKind.InvalidResponse);
                body.Write(buffer, 0, read);
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (body.Length == 0) return Failure(FootballDataFailureKind.InvalidResponse);
            var bytes = body.ToArray();
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.EnumerateObject().Any())
                return Failure(FootballDataFailureKind.InvalidResponse);
            deadline.Token.ThrowIfCancellationRequested();
            return new FootballDataResponse(Encoding.UTF8.GetString(bytes), null)
            {
                Metadata = new((int)response.StatusCode, response.Headers.Date?.ToUniversalTime(), bytes.Length)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(FootballDataFailureKind.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("FootballData request was cancelled.", cancellationToken);
        }
        catch (HttpRequestException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(FootballDataFailureKind.NetworkFailure);
        }
        catch (IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(FootballDataFailureKind.NetworkFailure);
        }
        catch (JsonException) { return Failure(FootballDataFailureKind.InvalidResponse); }
    }

    private static FootballDataFailureKind MapStatus(HttpStatusCode status) => (int)status switch
    {
        401 => FootballDataFailureKind.Unauthorized,
        403 => FootballDataFailureKind.Forbidden,
        404 => FootballDataFailureKind.NotFound,
        429 => FootballDataFailureKind.RateLimited,
        >= 500 and <= 599 => FootballDataFailureKind.ProviderFailure,
        _ => FootballDataFailureKind.UnexpectedStatus
    };

    private static FootballDataResponse Failure(FootballDataFailureKind kind, int? status = null,
        TimeSpan? retryAfter = null, DateTimeOffset? retryAfterUtc = null)
        => new(null, new FootballDataFailure(kind, status, retryAfter, retryAfterUtc));
}
