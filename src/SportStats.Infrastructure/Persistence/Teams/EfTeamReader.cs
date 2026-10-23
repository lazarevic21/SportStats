using Microsoft.EntityFrameworkCore;
using SportStats.Application.Football.Teams;

namespace SportStats.Infrastructure.Persistence.Teams;

public sealed class EfTeamReader(SportStatsDbContext context) : ITeamReader
{
    public async Task<TeamPageResponse> ReadAsync(TeamPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await BuildQuery(request).ToListAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CreatePage(rows, request);
    }

    internal static TeamPageResponse CreatePage(List<TeamResponse> rows, TeamPageRequest request)
    {
        var hasNextPage = rows.Count > request.PageSize;
        if (hasNextPage) rows.RemoveAt(rows.Count - 1);
        return new(rows.AsReadOnly(), request.Page, request.PageSize, hasNextPage);
    }

    // One bounded scalar projection; the lookahead avoids a separate count query.
    internal IQueryable<TeamResponse> BuildQuery(TeamPageRequest request) =>
        context.FootballTeams.AsNoTracking()
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Skip(request.Offset).Take(request.PageSize + 1)
            .Select(x => new TeamResponse(x.Id, x.Name, x.ShortName, x.CrestUrl));
}
