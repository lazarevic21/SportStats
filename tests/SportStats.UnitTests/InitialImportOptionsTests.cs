using SportStats.InitialImport;
using Xunit;

namespace SportStats.UnitTests;

public sealed class InitialImportOptionsTests
{
    [Fact]
    public void ExplicitLiveSelectionDefaultsToPreview()
    {
        Assert.True(ImportOptions.TryParse(["--live", "--competition", "PL", "--year", "2026"], out var options));
        Assert.False(options.Apply);
    }
    [Fact]
    public void ApplyRequiresExplicitFlag()
    {
        Assert.True(ImportOptions.TryParse(["--live", "--competition", "PL", "--year", "2026", "--apply"], out var options));
        Assert.True(options.Apply);
    }
    [Theory]
    [InlineData("")]
    [InlineData("--competition PL --year 2026")]
    [InlineData("--live --year 2026")]
    [InlineData("--live --competition PL")]
    [InlineData("--live --competition PD --year 2026")]
    [InlineData("--live --competition PL --year 0")]
    [InlineData("--live --competition PL --year 2026 --apply --apply")]
    [InlineData("--live --competition PL --year 2026 --token secret")]
    [InlineData("--live --competition PL --year 2026 --live")]
    public void IncompleteUnsupportedOrDuplicateOptionsFailClosed(string arguments) =>
        Assert.False(ImportOptions.TryParse(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), out _));
}
