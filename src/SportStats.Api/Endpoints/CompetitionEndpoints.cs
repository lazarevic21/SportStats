using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SportStats.Application.Competitions;

namespace SportStats.Api.Endpoints;

public static class CompetitionEndpoints
{
    public static IEndpointRouteBuilder MapCompetitionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/competitions", (Delegate)ReadAsync)
            .Produces<CompetitionPageResponse>()
            .Produces<ValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context)
    {
        var errors = new Dictionary<string, string[]>();
        var page = ReadPagination(context.Request.Query, "page", CompetitionPageRequest.DefaultPage, errors);
        var pageSize = ReadPagination(context.Request.Query, "pageSize", CompetitionPageRequest.DefaultPageSize, errors);
        CompetitionPageRequest? request = null;

        if (errors.Count == 0)
        {
            try
            {
                request = new CompetitionPageRequest(page, pageSize);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                var field = exception.ParamName == "pageSize" ? "pageSize" : "page";
                errors[field] = [field == "pageSize"
                    ? "Page size must be between 1 and 100."
                    : "Page must be positive and its offset must be supported."];
            }
        }

        if (errors.Count > 0)
        {
            return Results.Json(new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid pagination."
            }, contentType: "application/problem+json", statusCode: StatusCodes.Status400BadRequest);
        }

        // Resolve persistence only after validation; malformed input must not initialize database configuration.
        var reader = context.RequestServices.GetRequiredService<ICompetitionReader>();
        var response = await reader.ReadAsync(request!, context.RequestAborted);
        return Results.Ok(response);
    }

    private static int ReadPagination(IQueryCollection query, string name, int defaultValue,
        Dictionary<string, string[]> errors)
    {
        if (!query.TryGetValue(name, out var values))
            return defaultValue;

        if (values.Count == 1 && int.TryParse(values[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out var value))
            return value;

        errors[name] = ["Supply one supported integer value."];
        return defaultValue;
    }
}
