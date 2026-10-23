using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using SportStats.Api.Configuration;
using SportStats.Api.Endpoints;
using SportStats.Application;
using SportStats.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOptions<ApiOptions>()
    .BindConfiguration(ApiOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(options => !string.IsNullOrWhiteSpace(options.ServiceName),
        "Api:ServiceName must not be empty or whitespace.")
    .ValidateOnStart();
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Title = "An unexpected error occurred.";
        context.ProblemDetails.Detail = "The server could not complete the request.";
        context.ProblemDetails.Extensions.Clear();
    };
});

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = (context, report) =>
        context.Response.WriteAsJsonAsync(new { status = report.Status.ToString() },
            cancellationToken: context.RequestAborted)
});

app.MapCompetitionEndpoints();
app.MapCompetitionSeasonEndpoints();
app.MapTeamEndpoints();
app.MapMatchEndpoints();
app.MapStandingsEndpoints();

app.Run();

public partial class Program;
