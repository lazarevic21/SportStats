using System.ComponentModel.DataAnnotations;

namespace SportStats.Api.Configuration;

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    [Required]
    [StringLength(100)]
    public string ServiceName { get; set; } = "SportStats";
}
