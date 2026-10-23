namespace SportStats.Application.Competitions;

/// <summary>Reads persisted competitions without changing application or provider state.</summary>
public interface ICompetitionReader
{
    Task<CompetitionPageResponse> ReadAsync(CompetitionPageRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CompetitionResponse(Guid Id, string Code, string Name,
    Guid SportId, string SportCode, string SportName);

public sealed record CompetitionPageResponse(IReadOnlyList<CompetitionResponse> Items,
    int Page, int PageSize, bool HasNextPage);

/// <summary>Validated, bounded offset pagination. Pages are numbered from one.</summary>
public sealed class CompetitionPageRequest
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaximumPageSize = 100;

    public CompetitionPageRequest(int page = DefaultPage, int pageSize = DefaultPageSize)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        var offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(page));

        Page = page;
        PageSize = pageSize;
        Offset = (int)offset;
    }

    public int Page { get; }
    public int PageSize { get; }
    public int Offset { get; }
}
