using SportStats.Application.Competitions;
using Xunit;

namespace SportStats.UnitTests;

public sealed class CompetitionPageRequestTests
{
    [Fact]
    public void DefaultsDescribeFirstBoundedPage()
    {
        var request = new CompetitionPageRequest();
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
        var request = new CompetitionPageRequest(page, pageSize);
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
        Assert.Throws<ArgumentOutOfRangeException>(() => new CompetitionPageRequest(page, pageSize));
}
