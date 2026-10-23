using Microsoft.EntityFrameworkCore;
using SportStats.Application.Competitions;

namespace SportStats.Infrastructure.Persistence.Competitions;

public sealed class EfCompetitionSeasonReader(SportStatsDbContext context) : ICompetitionSeasonReader
{
    public async Task<SeasonPageResponse> ReadAsync(CompetitionSeasonPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await BuildQuery(request).ToListAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CreatePage(rows, request);
    }

    internal static SeasonPageResponse CreatePage(List<SeasonResponse> rows,
        CompetitionSeasonPageRequest request)
    {
        var hasNextPage = rows.Count > request.PageSize;
        if (hasNextPage) rows.RemoveAt(rows.Count - 1);
        return new(rows.AsReadOnly(), request.Page, request.PageSize, hasNextPage);
    }

    // Keys are opaque identifiers, not chronological dates. The ID breaks ordering ties.
    // One lookahead row avoids a count or parent-existence query.
    internal IQueryable<SeasonResponse> BuildQuery(CompetitionSeasonPageRequest request) =>
        context.Seasons.AsNoTracking()
            .Where(x => x.CompetitionId == request.CompetitionId)
            .OrderBy(x => x.Key).ThenBy(x => x.Id)
            .Skip(request.Offset).Take(request.PageSize + 1)
            .Select(x => new SeasonResponse(x.Id, x.CompetitionId, x.Key, x.StartDate, x.EndDate));
}
