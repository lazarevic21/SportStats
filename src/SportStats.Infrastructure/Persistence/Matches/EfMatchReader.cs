using Microsoft.EntityFrameworkCore;
using SportStats.Application.Football.Matches;

namespace SportStats.Infrastructure.Persistence.Matches;

public sealed class EfMatchReader(SportStatsDbContext context) : IMatchReader
{
    public async Task<MatchPageResponse> ReadAsync(MatchPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await BuildQuery(request).ToListAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CreatePage(rows, request);
    }

    internal static MatchPageResponse CreatePage(List<MatchResponse> rows, MatchPageRequest request)
    {
        var hasNextPage = rows.Count > request.PageSize;
        if (hasNextPage) rows.RemoveAt(rows.Count - 1);
        return new(rows.AsReadOnly(), request.Page, request.PageSize, hasNextPage);
    }

    internal IQueryable<MatchResponse> BuildQuery(MatchPageRequest request)
    {
        var query = context.FootballMatches.AsNoTracking();
        if (request.SeasonId.HasValue)
            query = query.Where(x => x.SeasonId == request.SeasonId.Value);
        if (request.TeamId.HasValue)
            query = query.Where(x => x.HomeTeamId == request.TeamId.Value || x.AwayTeamId == request.TeamId.Value);
        if (request.FromUtc is { } fromUtc)
            query = query.Where(x => x.KickoffUtc >= fromUtc);
        if (request.ToUtc is { } toUtc)
            query = query.Where(x => x.KickoffUtc < toUtc);

        // Only the two team names require joins. No entity materialization or separate count query.
        return query.OrderBy(x => x.KickoffUtc == null).ThenBy(x => x.KickoffUtc).ThenBy(x => x.Id)
            .Skip(request.Offset).Take(request.PageSize + 1)
            .Select(x => new MatchResponse(x.Id, x.SeasonId,
                new MatchTeamResponse(x.HomeTeamId, x.HomeTeam.Name),
                new MatchTeamResponse(x.AwayTeamId, x.AwayTeam.Name),
                x.KickoffUtc, x.Status.ToString(), x.Matchday, x.HomeGoals, x.AwayGoals));
    }
}
