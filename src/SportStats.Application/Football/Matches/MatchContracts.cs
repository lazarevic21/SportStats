using SportStats.Application.Competitions;

namespace SportStats.Application.Football.Matches;

/// <summary>Reads persisted football matches without changing application or provider state.</summary>
public interface IMatchReader
{
    Task<MatchPageResponse> ReadAsync(MatchPageRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record MatchTeamResponse(Guid Id, string Name);

public sealed record MatchResponse(Guid Id, Guid SeasonId, MatchTeamResponse HomeTeam,
    MatchTeamResponse AwayTeam, DateTime? KickoffUtc, string Status, int? Matchday,
    int? HomeGoals, int? AwayGoals);

public sealed record MatchPageResponse(IReadOnlyList<MatchResponse> Items,
    int Page, int PageSize, bool HasNextPage);

/// <summary>Bounded pagination and optional filters. Dates bound kickoff at UTC midnight: from inclusive, to exclusive.</summary>
public sealed class MatchPageRequest
{
    public const int DefaultPage = CompetitionPageRequest.DefaultPage;
    public const int DefaultPageSize = CompetitionPageRequest.DefaultPageSize;
    public const int MaximumPageSize = CompetitionPageRequest.MaximumPageSize;

    public MatchPageRequest(int page = DefaultPage, int pageSize = DefaultPageSize,
        Guid? seasonId = null, Guid? teamId = null, DateOnly? from = null, DateOnly? to = null)
    {
        var pagination = new CompetitionPageRequest(page, pageSize);
        if (seasonId == Guid.Empty) throw new ArgumentException("Season identifier must be nonempty.", nameof(seasonId));
        if (teamId == Guid.Empty) throw new ArgumentException("Team identifier must be nonempty.", nameof(teamId));
        if (from.HasValue && to.HasValue && from.Value >= to.Value)
            throw new ArgumentException("The date range must be increasing.", nameof(to));

        Page = pagination.Page;
        PageSize = pagination.PageSize;
        Offset = pagination.Offset;
        SeasonId = seasonId;
        TeamId = teamId;
        From = from;
        To = to;
    }

    public int Page { get; }
    public int PageSize { get; }
    public int Offset { get; }
    public Guid? SeasonId { get; }
    public Guid? TeamId { get; }
    public DateOnly? From { get; }
    public DateOnly? To { get; }
    public DateTime? FromUtc => From?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    public DateTime? ToUtc => To?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
}
