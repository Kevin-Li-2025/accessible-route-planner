using System.Net;
using System.Net.Http.Json;
using AccessCity.API.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AccessCity.Tests;

[Collection("SuperStress")]
public sealed class ProductionTrafficGuardTests
{
    [Fact]
    public async Task ReadinessBypassesGlobalLimiterUnderProbeBursts()
    {
        using var baseFactory = new AccessCityApiFactory();
        using var factory = BuildFactory(
            baseFactory,
            new Dictionary<string, string?>
            {
                ["RateLimiting:Global:PermitLimit"] = "1",
                ["RateLimiting:Global:WindowSeconds"] = "60",
                ["RateLimiting:Global:QueueLimit"] = "0"
            });

        using var client = factory.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetAsync("/health/ready")));

        try
        {
            Assert.All(responses, response =>
            {
                using (response)
                {
                    Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
                    Assert.True(
                        response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
                        $"Unexpected readiness status {response.StatusCode}");
                }
            });
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task RoutingHeavyPolicyShedsBurstBeforeAsyncJobQueueGrows()
    {
        using var baseFactory = new AccessCityApiFactory();
        using var factory = BuildFactory(
            baseFactory,
            new Dictionary<string, string?>
            {
                ["RateLimiting:Global:PermitLimit"] = "1000",
                ["RateLimiting:RoutingHeavy:PermitLimit"] = "1",
                ["RateLimiting:RoutingHeavy:WindowSeconds"] = "60",
                ["RateLimiting:RoutingHeavy:QueueLimit"] = "0"
            });

        var rateOptions = factory.Services.GetRequiredService<IOptions<AccessCityRateLimitingOptions>>().Value;
        Assert.Equal(1, rateOptions.RoutingHeavy.PermitLimit);

        using var client = factory.CreateClient();
        var routeRequest = new
        {
            Start = new { X = -1.8904, Y = 52.4862 },
            End = new { X = -1.8894, Y = 52.4862 },
            Profile = "standard",
            Preferences = Array.Empty<string>(),
            SafetyWeight = 0.5
        };

        using var first = await client.PostAsJsonAsync("/api/v1/routing/safe-path/async", routeRequest);
        using var second = await client.PostAsJsonAsync("/api/v1/routing/safe-path/async", routeRequest);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    private static WebApplicationFactory<Program> BuildFactory(
        AccessCityApiFactory baseFactory,
        IReadOnlyDictionary<string, string?> settings) =>
        baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(settings);
            });
        });
}
