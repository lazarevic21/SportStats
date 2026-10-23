using Microsoft.EntityFrameworkCore;
using SportStats.Application.Competitions;

namespace SportStats.Infrastructure.Persistence.Competitions;

public sealed class EfCompetitionReader(SportStatsDbContext context) : ICompetitionReader
{
    public async Task<CompetitionPageResponse> ReadAsync(CompetitionPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await BuildQuery(request).ToListAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CreatePage(rows, request);
    }

    internal static CompetitionPageResponse CreatePage(List<CompetitionResponse> rows,
        CompetitionPageRequest request)
    {
        var hasNextPage = rows.Count > request.PageSize;
        if (hasNextPage) rows.RemoveAt(rows.Count - 1);
        return new(rows.AsReadOnly(), request.Page, request.PageSize, hasNextPage);
    }

    // One bounded projection; the extra row avoids a separate COUNT round trip.
    internal IQueryable<CompetitionResponse> BuildQuery(CompetitionPageRequest request) =>
        context.Competitions.AsNoTracking()
            .OrderBy(x => x.Sport.Code).ThenBy(x => x.Code).ThenBy(x => x.Id)
            .Skip(request.Offset).Take(request.PageSize + 1)
            .Select(x => new CompetitionResponse(x.Id, x.Code, x.Name,
                x.SportId, x.Sport.Code, x.Sport.Name));
}
