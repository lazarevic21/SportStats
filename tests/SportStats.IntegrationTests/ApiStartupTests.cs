using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using SportStats.Api.Configuration;
using Xunit;

namespace SportStats.IntegrationTests;

public sealed class ApiStartupTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Password=synthetic-secret;invalid-setting=synthetic-secret")]
    [InlineData("Host=127.0.0.1;Port=1;Database=sportstats_test;Username=sportstats_app;Password=synthetic-secret;Timeout=1")]
    public async Task HealthRemainsHealthyWithMissingMalformedOrUnreachableDatabaseConfiguration(string? connectionString)
    {
        using var factory = CreateFactory("SportStats tests").WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SportStats"] = connectionString
                })));
        using var client = factory.CreateClient(ClientOptions());

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", payload.RootElement.GetProperty("status").GetString());
        Assert.Single(payload.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task HealthReturnsMinimalHealthyResponseWithoutExternalConfiguration()
    {
        using var factory = CreateFactory("SportStats tests");
        using var client = factory.CreateClient(ClientOptions());

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", payload.RootElement.GetProperty("status").GetString());
        Assert.Single(payload.RootElement.EnumerateObject());
        Assert.False(response.Headers.Contains("Server"));
        Assert.Null(factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("SportStats"));
        Assert.Equal("SportStats tests", factory.Services.GetRequiredService<IOptions<ApiOptions>>().Value.ServiceName);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void DefaultFactoryClearsInheritedDatabaseConfiguration(string environment)
    {
        using var factory = CreateFactory("SportStats tests").WithWebHostBuilder(builder =>
            builder.UseEnvironment(environment));
        using var client = factory.CreateClient(ClientOptions());
        Assert.Null(factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("SportStats"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void FactoryMasksDeliberatelyInheritedSyntheticDatabaseSetting(string environment)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:SportStats"] = "synthetic-inherited-value-not-a-connection-string"
                }));
        }).WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var syntheticValueIsInherited = string.Equals(configuration.Build().GetConnectionString("SportStats"),
                    "synthetic-inherited-value-not-a-connection-string", StringComparison.Ordinal);
                Assert.True(syntheticValueIsInherited, "The synthetic inherited setting must exist before isolation.");
            });
            ConfigureIsolatedHost(builder);
        });
        using var client = factory.CreateClient(ClientOptions());
        Assert.Null(factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("SportStats"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidServiceNamePreventsStartup(string name)
    {
        using var factory = CreateFactory(name);

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient(ClientOptions()));
    }

    [Fact]
    public void OverlongConfigurationFailsWithoutEchoingItsValue()
    {
        var syntheticSensitiveValue = "synthetic-sensitive-" + new string('x', 101);
        using var factory = CreateFactory(syntheticSensitiveValue);

        var exception = Assert.Throws<OptionsValidationException>(() => factory.CreateClient(ClientOptions()));

        Assert.DoesNotContain(syntheticSensitiveValue, exception.Message);
    }

    [Fact]
    public async Task UnmappedRouteReturnsNotFound()
    {
        using var factory = CreateFactory("SportStats tests");
        using var client = factory.CreateClient(ClientOptions());

        using var response = await client.GetAsync("/api/not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task UnexpectedErrorsReturnSanitizedProblemDetails(string environment)
    {
        using var factory = CreateFactory("SportStats tests").WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, ThrowingStartupFilter>());
        });
        using var client = factory.CreateClient(ClientOptions());
        using var request = new HttpRequestMessage(HttpMethod.Get, "/test-error");
        request.Headers.Accept.ParseAdd("application/problem+json");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(ThrowingStartupFilter.SyntheticSecret, body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal("The server could not complete the request.", payload.RootElement.GetProperty("detail").GetString());
        Assert.False(payload.RootElement.TryGetProperty("traceId", out _));
    }

    private static WebApplicationFactory<Program> CreateFactory(string serviceName) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            ConfigureIsolatedHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Api:ServiceName"] = serviceName
                }));
        });

    private static void ConfigureIsolatedHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SportStats"] = null
            }));
    }

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    };

    private sealed class ThrowingStartupFilter : IStartupFilter
    {
        public const string SyntheticSecret = "synthetic-test-secret-do-not-expose";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(context => throw new InvalidOperationException(SyntheticSecret));
        };
    }
}
