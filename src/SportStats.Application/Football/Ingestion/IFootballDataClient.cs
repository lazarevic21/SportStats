namespace SportStats.Application.Football.Ingestion;

/// <summary>Read-only provider transport. Successful JSON still requires domain normalization.</summary>
public interface IFootballDataClient
{
    Task<FootballDataResponse> GetCompetitionAsync(string competitionCode, CancellationToken cancellationToken = default);
    Task<FootballDataResponse> GetTeamsAsync(string competitionCode, CancellationToken cancellationToken = default);
    Task<FootballDataResponse> GetMatchesAsync(string competitionCode, CancellationToken cancellationToken = default);
}

public enum FootballDataFailureKind
{
    Unauthorized, Forbidden, NotFound, RateLimited, ProviderFailure,
    NetworkFailure, Timeout, InvalidResponse, UnexpectedStatus
}

/// <summary>Sanitized failure information; contains no credentials, payload or transport exception.</summary>
public sealed record FootballDataFailure(FootballDataFailureKind Kind, int? StatusCode = null,
    TimeSpan? RetryAfter = null, DateTimeOffset? RetryAfterUtc = null);

public sealed record FootballDataResponse(string? Json, FootballDataFailure? Failure)
{
    public bool IsSuccess => Json is not null && Failure is null;
    public FootballDataResponseMetadata? Metadata { get; init; }
}

/// <summary>Allowlisted transport facts; no arbitrary headers or freshness guarantee.</summary>
public sealed record FootballDataResponseMetadata(int StatusCode, DateTimeOffset? DateUtc, int ResponseBytes);
