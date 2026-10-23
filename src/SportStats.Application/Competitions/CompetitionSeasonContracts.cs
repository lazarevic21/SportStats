namespace SportStats.Application.Competitions;

/// <summary>Reads persisted seasons belonging to a competition without changing state.</summary>
public interface ICompetitionSeasonReader
{
    Task<SeasonPageResponse> ReadAsync(CompetitionSeasonPageRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record SeasonResponse(Guid Id, Guid CompetitionId, string Key,
    DateOnly? StartDate, DateOnly? EndDate);

public sealed record SeasonPageResponse(IReadOnlyList<SeasonResponse> Items,
    int Page, int PageSize, bool HasNextPage);

/// <summary>Validated competition identifier and bounded, one-based offset pagination.</summary>
public sealed class CompetitionSeasonPageRequest
{
    public CompetitionSeasonPageRequest(Guid competitionId,
        int page = CompetitionPageRequest.DefaultPage,
        int pageSize = CompetitionPageRequest.DefaultPageSize)
    {
        if (competitionId == Guid.Empty)
            throw new ArgumentException("A competition identifier is required.", nameof(competitionId));

        var pagination = new CompetitionPageRequest(page, pageSize);
        CompetitionId = competitionId;
        Page = pagination.Page;
        PageSize = pagination.PageSize;
        Offset = pagination.Offset;
    }

    public Guid CompetitionId { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int Offset { get; }
}
