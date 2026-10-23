using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SportStats.Application.Football.Standings;

namespace SportStats.Api.Endpoints;

public static class StandingsEndpoints
{
    public static IEndpointRouteBuilder MapStandingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/seasons/{seasonId}/standings", (Delegate)ReadAsync)
            .Produces<StandingsPageResponse>()
            .Produces<ValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Guid.TryParse(context.Request.RouteValues["seasonId"]?.ToString(), out var seasonId)
            || seasonId == Guid.Empty)
            errors["seasonId"] = ["Supply a nonempty season identifier."];

        if (context.Request.Query.Keys.Any(key =>
                !string.Equals(key, "page", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "pageSize", StringComparison.OrdinalIgnoreCase)))
            errors["query"] = ["Only page and pageSize query parameters are supported."];

        var page = ReadPagination(context.Request.Query, "page", StandingsPageRequest.DefaultPage, errors);
        var pageSize = ReadPagination(context.Request.Query, "pageSize", StandingsPageRequest.DefaultPageSize, errors);
        StandingsPageRequest? request = null;
        if (errors.Count == 0)
        {
            try
            {
                request = new StandingsPageRequest(seasonId, page, pageSize);
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
            return Results.Json(new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request."
            }, contentType: "application/problem+json", statusCode: StatusCodes.Status400BadRequest);

        // Invalid requests must not initialize persistence or database configuration.
        var reader = context.RequestServices.GetRequiredService<IStandingsReader>();
        var response = await reader.ReadAsync(request!, context.RequestAborted);
        return Results.Ok(response);
    }

    private static int ReadPagination(IQueryCollection query, string name, int defaultValue,
        Dictionary<string, string[]> errors)
    {
        if (!query.TryGetValue(name, out var values)) return defaultValue;
        if (values.Count == 1 && int.TryParse(values[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out var value)) return value;
        errors[name] = ["Supply one supported integer value."];
        return defaultValue;
    }
}
