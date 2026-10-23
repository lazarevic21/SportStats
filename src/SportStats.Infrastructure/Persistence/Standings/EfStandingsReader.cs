using Microsoft.EntityFrameworkCore;
using SportStats.Application.Football.Standings;

namespace SportStats.Infrastructure.Persistence.Standings;

public sealed class EfStandingsReader(SportStatsDbContext context) : IStandingsReader
{
    public async Task<StandingsPageResponse> ReadAsync(StandingsPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await BuildQuery(request).SingleOrDefaultAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return CreatePage(snapshot, request);
    }

    internal static StandingsPageResponse CreatePage(SnapshotPage? snapshot, StandingsPageRequest request)
    {
        if (snapshot is null)
            return new(request.SeasonId, null, Array.Empty<StandingRowResponse>(), request.Page, request.PageSize, false);

        var hasNextPage = snapshot.Items.Count > request.PageSize;
        if (hasNextPage) snapshot.Items.RemoveAt(snapshot.Items.Count - 1);
        return new(request.SeasonId, snapshot.Snapshot, snapshot.Items.AsReadOnly(),
            request.Page, request.PageSize, hasNextPage);
    }

    // Select the latest snapshot across provider/table/scope before paging its rows.
    // A correlated collection retains snapshot metadata even when the requested page is empty.
    internal IQueryable<SnapshotPage> BuildQuery(StandingsPageRequest request) =>
        context.FootballStandingSnapshots.AsNoTracking().AsSingleQuery()
            .Where(x => x.SeasonId == request.SeasonId)
            .OrderByDescending(x => x.RetrievedAtUtc).ThenByDescending(x => x.Id)
            .Take(1)
            .Select(x => new SnapshotPage(
                new StandingsSnapshotResponse(x.Id, x.ProviderKey, x.RetrievedAtUtc, x.TableType, x.Scope),
                context.FootballStandingRows.AsNoTracking()
                    .Where(row => row.SnapshotId == x.Id)
                    .OrderBy(row => row.Position).ThenBy(row => row.TeamId)
                    .Skip(request.Offset).Take(request.PageSize + 1)
                    .Select(row => new StandingRowResponse(row.Position,
                        new StandingsTeamResponse(row.TeamId, row.Team.Name))).ToList()));

    internal sealed record SnapshotPage(StandingsSnapshotResponse Snapshot, List<StandingRowResponse> Items);
}
