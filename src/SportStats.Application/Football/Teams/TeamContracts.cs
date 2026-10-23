using SportStats.Application.Competitions;

namespace SportStats.Application.Football.Teams;

/// <summary>Reads persisted football teams without changing application or provider state.</summary>
public interface ITeamReader
{
    Task<TeamPageResponse> ReadAsync(TeamPageRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TeamResponse(Guid Id, string Name, string? ShortName, string? CrestUrl);

public sealed record TeamPageResponse(IReadOnlyList<TeamResponse> Items,
    int Page, int PageSize, bool HasNextPage);

/// <summary>Validated, bounded offset pagination. Pages are numbered from one.</summary>
public sealed class TeamPageRequest
{
    public const int DefaultPage = CompetitionPageRequest.DefaultPage;
    public const int DefaultPageSize = CompetitionPageRequest.DefaultPageSize;
    public const int MaximumPageSize = CompetitionPageRequest.MaximumPageSize;

    public TeamPageRequest(int page = DefaultPage, int pageSize = DefaultPageSize)
    {
        var pagination = new CompetitionPageRequest(page, pageSize);
        Page = pagination.Page;
        PageSize = pagination.PageSize;
        Offset = pagination.Offset;
    }

    public int Page { get; }
    public int PageSize { get; }
    public int Offset { get; }
}
