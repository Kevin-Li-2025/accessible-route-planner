using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using NetTopologySuite.IO.Converters;
using Xunit;
using Xunit.Abstractions;

namespace AccessCity.Tests;

/// <summary>
/// Full-surface load test against the in-process API. Health checks and integrations are not
/// subject to the global sliding-window limiter, so those phases use large parallelism. All other
/// routes share ~100 req/min per partition (anonymous: connection IP, authenticated: user name),
/// so rate-limited work is run in staggered waves with pauses.
/// </summary>
[CollectionDefinition("SuperStress", DisableParallelization = true)]
public sealed class SuperStressCollection
{
}

[Collection("SuperStress")]
public class ApiSuperStressTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private readonly ITestOutputHelper _output;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new GeoJsonConverterFactory() },
    };

    private static readonly object BirminghamRoute = new
    {
        Start = new { X = -1.8904, Y = 52.4862 },
        End = new { X = -1.8894, Y = 52.4862 },
        Profile = "standard",
        Preferences = Array.Empty<string>(),
        SafetyWeight = 0.5,
    };

    public ApiSuperStressTests(AccessCityApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task SuperStress_FullApp_MixedConcurrentPhases()
    {
        var swTotal = Stopwatch.StartNew();

        var authClient = await _factory.CreateAuthenticatedClientAsync(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            },
            client => client.Timeout = TimeSpan.FromMinutes(3));

        await _factory.ImportOsmAsync(authClient);

        var anon = _factory.CreateClient();
        anon.Timeout = TimeSpan.FromMinutes(3);

        // ── Phase 1: endpoints excluded from global rate limiting ─────────────────────────────
        const int healthParallel = 80;
        const int readyParallel = 60;
        const int integrationsParallel = 60;

        var p1 = Stopwatch.StartNew();
        await RunParallelGets(anon, healthParallel, "/health", HttpStatusCode.OK, _output, "health");
        await RunParallelGets(anon, readyParallel, "/health/ready", HttpStatusCode.OK, _output, "ready");
        await RunParallelGets(
            anon,
            integrationsParallel,
            "/api/v1/integrations/status",
            HttpStatusCode.OK,
            _output,
            "integrations/status");
        p1.Stop();
        _output.WriteLine(
            $"Phase1 (no global limiter): {healthParallel}+{readyParallel}+{integrationsParallel} parallel GETs in {p1.ElapsedMilliseconds} ms");

        // ── Phase 2: anonymous partition — staggered waves (stay under global limiter) ───────
        const int waves = 10;
        const int pauseMs = 6500;
        var p2 = Stopwatch.StartNew();
        for (var w = 0; w < waves; w++)
        {
            await RunAnonymousStressWave(anon, w);
            if (w < waves - 1)
            {
                await Task.Delay(pauseMs);
            }
        }

        p2.Stop();
        _output.WriteLine($"Phase2 (anonymous staggered): {waves} waves, ~14 calls/wave, pause {pauseMs} ms → {p2.ElapsedMilliseconds} ms");

        // ── Phase 3: authenticated partition (separate rate-limit bucket) ─────────────────────
        const int authWaves = 6;
        const int authPauseMs = 5000;
        var p3 = Stopwatch.StartNew();
        for (var aw = 0; aw < authWaves; aw++)
        {
            await RunAuthenticatedStressWave(authClient);
            if (aw < authWaves - 1)
            {
                await Task.Delay(authPauseMs);
            }
        }

        p3.Stop();
        _output.WriteLine($"Phase3 (JWT staggered): {authWaves} waves, pause {authPauseMs} ms → {p3.ElapsedMilliseconds} ms");

        swTotal.Stop();
        _output.WriteLine($"SuperStress total wall-clock: {swTotal.ElapsedMilliseconds} ms ({swTotal.Elapsed:c})");
    }

    private static async Task RunParallelGets(
        HttpClient client,
        int count,
        string url,
        HttpStatusCode expected,
        ITestOutputHelper output,
        string label)
    {
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, count).Select(_ => client.GetAsync(url));
        var responses = await Task.WhenAll(tasks);
        sw.Stop();

        try
        {
            Assert.All(responses, r =>
            {
                using (r)
                {
                    Assert.True(
                        r.StatusCode != HttpStatusCode.TooManyRequests,
                        $"429 on {label}: {url}");
                    Assert.True(
                        r.StatusCode != HttpStatusCode.InternalServerError,
                        $"500 on {label}: {url}");
                    Assert.Equal(expected, r.StatusCode);
                }
            });
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }

        output.WriteLine($"{count} parallel GET {label} in {sw.ElapsedMilliseconds} ms");
    }

    private static async Task RunAnonymousStressWave(HttpClient client, int waveIndex)
    {
        // ~14 concurrent calls per wave — spaced across waves to respect ~100 req/min per IP.
        var tasks = new List<Task<HttpResponseMessage>>(16);

        void AddGet(string path) => tasks.Add(client.GetAsync(path));

        AddGet("/api/v1/dashboard/summary");
        AddGet("/api/v1/dashboard/heat-map");
        AddGet("/api/v1/dashboard/infrastructure-feed?limit=10");
        AddGet("/api/v1/hazards?minLat=52.45&minLng=-1.95&maxLat=52.52&maxLng=-1.88");
        AddGet("/api/v1/spatial/poi?lat=52.4862&lng=-1.8904&radius=500");
        AddGet("/api/v1/spatial/map-overlay?layerName=hazards");
        AddGet("/api/v1/spatial/map-overlay?layerName=infrastructure");
        AddGet("/api/v1/safe-haven/nearby?lat=52.4862&lng=-1.8904&radius=500");
        AddGet("/api/v1/geocoding/search?query=Birmingham%20New%20Street");
        AddGet("/api/v1/geocoding/reverse?lat=52.4778&lon=-1.8983");
        AddGet("/api/v1/routing/risk-score?lat=52.4862&lng=-1.8904&radius=200");
        AddGet("/api/v1/routing/ai-risk-score?lat=52.4862&lng=-1.8904&radius=200");
        AddGet("/api/v1/routing/hazard-blend-risk?lat=52.4862&lng=-1.8904&radius=200");

        tasks.Add(client.PostAsJsonAsync("/api/v1/routing/safe-path/options", BirminghamRoute, JsonOptions));
        tasks.Add(client.PostAsJsonAsync("/api/v1/routing/safe-path", BirminghamRoute, JsonOptions));

        // Light mutation traffic (invalid bodies) — cheap 400s, exercises pipeline.
        if (waveIndex % 3 == 0)
        {
            tasks.Add(client.PostAsync("/api/v1/hazards", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")));
        }

        var responses = await Task.WhenAll(tasks);
        try
        {
            foreach (var r in responses)
            {
                using (r)
                {
                    var code = r.StatusCode;
                    Assert.True(code != HttpStatusCode.TooManyRequests, $"429 unexpected: {r.RequestMessage?.RequestUri}");
                    Assert.True(code != HttpStatusCode.InternalServerError, $"500 unexpected: {r.RequestMessage?.RequestUri}");

                    if (r.RequestMessage?.Method == HttpMethod.Post &&
                        r.RequestMessage.RequestUri?.AbsolutePath.Contains("/hazards", StringComparison.Ordinal) == true)
                    {
                        Assert.Equal(HttpStatusCode.BadRequest, code);
                        continue;
                    }

                    if (r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/geocoding/", StringComparison.Ordinal) == true)
                    {
                        Assert.True(code is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable, $"Geocoding: {code}");
                        continue;
                    }

                    if (r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/hazards", StringComparison.Ordinal) == true &&
                        r.RequestMessage.Method == HttpMethod.Get)
                    {
                        Assert.True(code is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable, $"Hazards: {code}");
                        continue;
                    }

                    if (r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/routing/safe-path", StringComparison.Ordinal) == true &&
                        r.RequestMessage.Method == HttpMethod.Post &&
                        !r.RequestMessage.RequestUri.AbsolutePath.Contains("options", StringComparison.Ordinal))
                    {
                        Assert.True(
                            code is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable,
                            $"safe-path: {code}");
                        continue;
                    }

                    if (r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/routing/safe-path/options", StringComparison.Ordinal) == true &&
                        r.RequestMessage.Method == HttpMethod.Post)
                    {
                        Assert.True(
                            code is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.ServiceUnavailable,
                            $"safe-path/options: {code}");
                        continue;
                    }

                    // safe-path/options, dashboard, spatial, routing GETs, safe-haven
                    Assert.Equal(HttpStatusCode.OK, code);
                }
            }
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }
    }

    private static async Task RunAuthenticatedStressWave(HttpClient client)
    {
        var tasks = new List<Task<HttpResponseMessage>>
        {
            client.GetAsync("/api/v1/offlinemap/bundle?minLat=52.45&minLng=-1.95&maxLat=52.52&maxLng=-1.88"),
            client.GetAsync("/api/v1/tiles/10/512/340.pbf"),
            client.GetAsync("/api/v1/dashboard/infrastructure-feed?limit=15"),
            client.GetAsync("/api/v1/dashboard/summary"),
            client.GetAsync("/api/v1/dashboard/heat-map"),
            client.GetAsync("/api/v1/spatial/poi?lat=52.4862&lng=-1.8904&radius=400"),
            client.PostAsJsonAsync("/api/v1/routing/safe-path/options", BirminghamRoute, JsonOptions),
            client.PostAsJsonAsync("/api/v1/routing/safe-path", BirminghamRoute, JsonOptions),
        };

        var responses = await Task.WhenAll(tasks);
        try
        {
            foreach (var r in responses)
            {
                using (r)
                {
                    var code = r.StatusCode;
                    Assert.True(code != HttpStatusCode.TooManyRequests, $"429 (auth): {r.RequestMessage?.RequestUri}");
                    Assert.True(code != HttpStatusCode.InternalServerError, $"500 (auth): {r.RequestMessage?.RequestUri}");

                    if (r.RequestMessage?.RequestUri?.AbsolutePath.EndsWith(".pbf", StringComparison.Ordinal) == true)
                    {
                        Assert.True(code is HttpStatusCode.OK or HttpStatusCode.NoContent, $"tile: {code}");
                        continue;
                    }

                    if (r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/routing/safe-path", StringComparison.Ordinal) == true &&
                        r.RequestMessage.Method == HttpMethod.Post &&
                        !r.RequestMessage.RequestUri.AbsolutePath.Contains("options", StringComparison.Ordinal))
                    {
                        Assert.True(
                            code is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable,
                            $"safe-path (auth): {code}");
                        continue;
                    }

                    if (r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/routing/safe-path/options", StringComparison.Ordinal) == true &&
                        r.RequestMessage.Method == HttpMethod.Post)
                    {
                        Assert.True(
                            code is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.ServiceUnavailable,
                            $"safe-path/options (auth): {code}");
                        continue;
                    }

                    if (r.RequestMessage?.RequestUri?.AbsolutePath.Contains("/dashboard/infrastructure-feed", StringComparison.Ordinal) == true)
                    {
                        Assert.True(code is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable, $"feed: {code}");
                        continue;
                    }

                    Assert.Equal(HttpStatusCode.OK, code);
                }
            }
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }
    }
}
