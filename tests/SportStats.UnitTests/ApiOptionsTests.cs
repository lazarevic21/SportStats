using System.ComponentModel.DataAnnotations;
using SportStats.Api.Configuration;
using Xunit;

namespace SportStats.UnitTests;

public sealed class ApiOptionsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void ServiceNameAcceptsSupportedLengths(int length)
    {
        var options = new ApiOptions { ServiceName = new string('a', length) };

        Assert.True(Validator.TryValidateObject(options, new ValidationContext(options), [], true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ServiceNameRejectsMissingOrBlankValues(string? name)
    {
        var options = new ApiOptions { ServiceName = name! };
        var results = new List<ValidationResult>();

        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, true));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ApiOptions.ServiceName)));
    }

    [Fact]
    public void ServiceNameRejectsOverlongValues()
    {
        var options = new ApiOptions { ServiceName = new string('a', 101) };

        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), [], true));
    }
}
