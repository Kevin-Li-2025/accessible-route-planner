using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AccessCity.Tests;

public class RiskScoreCacheServiceTests
{
    [Fact]
    public void BuildKey_BucketsNearbyCoordinatesAndRadius()
    {
        using var provider = CreateProvider();
        var cache = provider.GetRequiredService<IRiskScoreCacheService>();

        var keyA = cache.BuildKey(52.48621, -1.89041, 501);
        var keyB = cache.BuildKey(52.48624, -1.89044, 549);

        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public async Task GetOrComputeAsync_ReusesCachedRiskScore()
    {
        using var provider = CreateProvider();
        var cache = provider.GetRequiredService<IRiskScoreCacheService>();
        var calls = 0;

        var first = await cache.GetOrComputeAsync(
            "risk-score:test",
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new RiskScoreResponse { OverallRisk = 0.42 });
            });

        var second = await cache.GetOrComputeAsync(
            "risk-score:test",
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new RiskScoreResponse { OverallRisk = 0.99 });
            });

        Assert.Equal(0.42, first.OverallRisk);
        Assert.Equal(0.42, second.OverallRisk);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrComputeAsync_DoesNotCancelSharedFillWhenCallerTimesOut()
    {
        using var provider = CreateProvider();
        var cache = provider.GetRequiredService<IRiskScoreCacheService>();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        var result = await cache.GetOrComputeAsync(
            "risk-score:caller-canceled",
            async token =>
            {
                await Task.Delay(1, token);
                return new RiskScoreResponse { OverallRisk = 0.37 };
            },
            canceled.Token);

        Assert.Equal(0.37, result.OverallRisk);
    }

    private static ServiceProvider CreateProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RiskScoring:CacheTtlSeconds"] = "30",
                ["RiskScoring:CacheFillTimeoutSeconds"] = "5",
                ["RiskScoring:CacheCoordinatePrecision"] = "4",
                ["RiskScoring:CacheRadiusBucketMetres"] = "50"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDistributedMemoryCache();
#pragma warning disable EXTEXP0018
        services.AddHybridCache();
#pragma warning restore EXTEXP0018
        services.AddScoped<IRiskScoreCacheService, RiskScoreCacheService>();
        return services.BuildServiceProvider();
    }
}
