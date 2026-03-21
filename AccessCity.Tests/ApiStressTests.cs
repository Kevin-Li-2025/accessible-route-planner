using System.Diagnostics;
using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace AccessCity.Tests;

/// <summary>
/// Load-style checks against the in-process test server. The API enables a global sliding-window
/// rate limiter (~100 requests/minute per connection identity in Development). Bursts therefore stay
/// small; sustained load is spread with short delays so the suite stays green while still exercising
/// concurrency and throughput.
/// </summary>
public class ApiStressTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public ApiStressTests(AccessCityApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task Health_ParallelBurst_AllSucceed()
    {
        // Stay under a single sliding segment so we do not trip 429 from the global limiter.
        const int parallel = 20;
        var client = _factory.CreateClient();
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, parallel).Select(_ => client.GetAsync("/health"));
        var responses = await Task.WhenAll(tasks);
        sw.Stop();

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        _output.WriteLine($"{parallel} parallel /health in {sw.ElapsedMilliseconds} ms (~{sw.ElapsedMilliseconds / (double)parallel:F2} ms wall-clock per request)");
    }

    [Fact]
    public async Task Health_StaggeredParallelBursts_AllSucceed()
    {
        const int waves = 4;
        const int parallelPerWave = 15;
        const int pauseMs = 2500;
        var client = _factory.CreateClient();
        var sw = Stopwatch.StartNew();

        for (var w = 0; w < waves; w++)
        {
            var tasks = Enumerable.Range(0, parallelPerWave).Select(_ => client.GetAsync("/health"));
            var responses = await Task.WhenAll(tasks);
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            if (w < waves - 1)
            {
                await Task.Delay(pauseMs);
            }
        }

        sw.Stop();
        _output.WriteLine(
            $"{waves} waves × {parallelPerWave} parallel /health ({pauseMs} ms between waves) in {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task SpatialPoi_ParallelBurst_All200()
    {
        const int parallel = 20;
        var client = _factory.CreateClient();
        var url = "/api/v1/spatial/poi?lat=52.48&lng=-1.89&radius=500";
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, parallel).Select(_ => client.GetAsync(url));
        var responses = await Task.WhenAll(tasks);
        sw.Stop();

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        _output.WriteLine($"{parallel} parallel {url} in {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task HazardsList_ParallelBurst_Accepts200Or503()
    {
        const int parallel = 16;
        var client = _factory.CreateClient();
        var url = "/api/v1/hazards?minLat=52.45&minLng=-1.95&maxLat=52.52&maxLng=-1.88";
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, parallel).Select(_ => client.GetAsync(url));
        var responses = await Task.WhenAll(tasks);
        sw.Stop();

        foreach (var r in responses)
        {
            Assert.True(
                r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.ServiceUnavailable,
                $"Unexpected {r.StatusCode}");
        }

        _output.WriteLine($"{parallel} parallel hazards bbox in {sw.ElapsedMilliseconds} ms (OK or 503 from upstream)");
    }

    [Fact]
    public async Task Authenticated_InfrastructureFeed_Parallel_All200Or503()
    {
        const int parallel = 16;
        var client = await _factory.CreateAuthenticatedClientAsync();
        var url = "/api/v1/dashboard/infrastructure-feed?limit=10";
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, parallel).Select(_ => client.GetAsync(url));
        var responses = await Task.WhenAll(tasks);
        sw.Stop();

        foreach (var r in responses)
        {
            Assert.True(
                r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.ServiceUnavailable,
                $"Unexpected {r.StatusCode}");
        }

        _output.WriteLine($"{parallel} parallel authenticated feed in {sw.ElapsedMilliseconds} ms");
    }
}
