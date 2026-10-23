using SportStats.Application.Football.Matches;
using Xunit;

namespace SportStats.UnitTests;

public sealed class MatchPageRequestTests
{
    [Fact]
    public void FiltersPreserveIdentityAndConvertDatesToUtcMidnight()
    {
        var seasonId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var request = new MatchPageRequest(seasonId: seasonId, teamId: teamId,
            from: new(2026, 1, 1), to: new(2026, 2, 1));
        Assert.Equal(seasonId, request.SeasonId);
        Assert.Equal(teamId, request.TeamId);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), request.FromUtc);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), request.ToUtc);
        Assert.Equal(DateTimeKind.Utc, request.FromUtc!.Value.Kind);
        Assert.Null(new MatchPageRequest().FromUtc);
        Assert.Null(new MatchPageRequest().ToUtc);
    }

    [Fact]
    public void EmptyIdentifiersAndNonIncreasingRangesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new MatchPageRequest(seasonId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => new MatchPageRequest(teamId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => new MatchPageRequest(from: new(2026, 1, 1), to: new(2026, 1, 1)));
        Assert.Throws<ArgumentException>(() => new MatchPageRequest(from: new(2026, 2, 1), to: new(2026, 1, 1)));
    }

    [Fact]
    public void DefaultsDescribeFirstBoundedPage()
    {
        var request = new MatchPageRequest();
        Assert.Equal(1, request.Page);
        Assert.Equal(20, request.PageSize);
        Assert.Equal(0, request.Offset);
    }

    [Theory]
    [InlineData(2, 20, 20)]
    [InlineData(3, 100, 200)]
    [InlineData(int.MaxValue, 1, int.MaxValue - 1)]
    [InlineData(1073741824, 2, 2147483646)]
    public void ValidPagesHaveSafeOffsets(int page, int pageSize, int offset)
    {
        var request = new MatchPageRequest(page, pageSize);
        Assert.Equal(page, request.Page);
        Assert.Equal(pageSize, request.PageSize);
        Assert.Equal(offset, request.Offset);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(1073741825, 2)]
    public void InvalidOrOverflowingPagesAreRejected(int page, int pageSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchPageRequest(page, pageSize));
}
