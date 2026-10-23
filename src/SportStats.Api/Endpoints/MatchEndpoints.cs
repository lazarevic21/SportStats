using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using SportStats.Application.Football.Matches;

namespace SportStats.Api.Endpoints;

public static class MatchEndpoints
{
    private static readonly HashSet<string> SupportedParameters =
        new(["page", "pageSize", "seasonId", "teamId", "from", "to"], StringComparer.OrdinalIgnoreCase);

    public static IEndpointRouteBuilder MapMatchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/matches", (Delegate)ReadAsync)
            .Produces<MatchPageResponse>()
            .Produces<ValidationProblemDetails>(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return endpoints;
    }

    private static async Task<IResult> ReadAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var errors = new Dictionary<string, string[]>();
        if (query.Keys.Any(key => !SupportedParameters.Contains(key)))
            errors["query"] = ["Only page, pageSize, seasonId, teamId, from and to query parameters are supported."];

        var page = ReadPagination(query, "page", MatchPageRequest.DefaultPage, errors);
        var pageSize = ReadPagination(query, "pageSize", MatchPageRequest.DefaultPageSize, errors);
        var seasonId = ReadIdentifier(query, "seasonId", errors);
        var teamId = ReadIdentifier(query, "teamId", errors);
        var from = ReadDate(query, "from", errors);
        var to = ReadDate(query, "to", errors);
        MatchPageRequest? request = null;
        if (errors.Count == 0)
        {
            try
            {
                request = new MatchPageRequest(page, pageSize, seasonId, teamId, from, to);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                var field = exception.ParamName == "pageSize" ? "pageSize" : "page";
                errors[field] = [field == "pageSize"
                    ? "Page size must be between 1 and 100."
                    : "Page must be positive and its offset must be supported."];
            }
            catch (ArgumentException)
            {
                errors["to"] = ["The to date must be later than the from date."];
            }
        }

        if (errors.Count > 0)
            return Results.Json(new ValidationProblemDetails(errors)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request."
            }, contentType: "application/problem+json", statusCode: StatusCodes.Status400BadRequest);

        // Invalid requests must not resolve persistence or initialize database configuration.
        var reader = context.RequestServices.GetRequiredService<IMatchReader>();
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

    private static Guid? ReadIdentifier(IQueryCollection query, string name, Dictionary<string, string[]> errors)
    {
        if (!query.TryGetValue(name, out var values)) return null;
        if (values.Count == 1 && Guid.TryParseExact(values[0], "D", out var value) && value != Guid.Empty)
            return value;
        errors[name] = ["Supply one nonempty GUID in canonical format."];
        return null;
    }

    private static DateOnly? ReadDate(IQueryCollection query, string name, Dictionary<string, string[]> errors)
    {
        if (!query.TryGetValue(name, out var values)) return null;
        if (values.Count == 1 && DateOnly.TryParseExact(values[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var value)) return value;
        errors[name] = ["Supply one valid date in yyyy-MM-dd format."];
        return null;
    }
}
