using SportStats.Application.Competitions;

namespace SportStats.Application.Football.Standings;

/// <summary>Reads the latest persisted standings snapshot for a season without changing state.</summary>
public interface IStandingsReader
{
    Task<StandingsPageResponse> ReadAsync(StandingsPageRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StandingsTeamResponse(Guid Id, string Name);

public sealed record StandingRowResponse(int Position, StandingsTeamResponse Team);

public sealed record StandingsSnapshotResponse(Guid Id, string ProviderKey, DateTime RetrievedAtUtc,
    string TableType, string Scope);

public sealed record StandingsPageResponse(Guid SeasonId, StandingsSnapshotResponse? Snapshot,
    IReadOnlyList<StandingRowResponse> Items, int Page, int PageSize, bool HasNextPage);

/// <summary>Validated season identifier and bounded, one-based offset pagination.</summary>
public sealed class StandingsPageRequest
{
    public const int DefaultPage = CompetitionPageRequest.DefaultPage;
    public const int DefaultPageSize = CompetitionPageRequest.DefaultPageSize;
    public const int MaximumPageSize = CompetitionPageRequest.MaximumPageSize;

    public StandingsPageRequest(Guid seasonId, int page = DefaultPage, int pageSize = DefaultPageSize)
    {
        if (seasonId == Guid.Empty)
            throw new ArgumentException("A season identifier is required.", nameof(seasonId));

        var pagination = new CompetitionPageRequest(page, pageSize);
        SeasonId = seasonId;
        Page = pagination.Page;
        PageSize = pagination.PageSize;
        Offset = pagination.Offset;
    }

    public Guid SeasonId { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int Offset { get; }
}
