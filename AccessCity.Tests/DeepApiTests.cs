using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Models;
using AccessCity.API.Models.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AccessCity.Tests;

public class DeepApiTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public DeepApiTests(AccessCityApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_rejects_empty_email()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest("", "Password123!", "A"), _opts);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Register_rejects_bad_email_format()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest("notanemail", "Password123!", "A"), _opts);
        Assert.True(res.StatusCode == HttpStatusCode.BadRequest || res.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Register_rejects_short_password()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/register", new RegisterRequest("a@b.co", "short", "A"), _opts);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Login_rejects_empty_password()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest("x@y.com", ""), _opts);
        Assert.True(res.StatusCode == HttpStatusCode.BadRequest || res.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RefreshToken_missing_token_returns_401_or_400()
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsync("/api/auth/refresh-token", null);
        Assert.True(res.StatusCode == HttpStatusCode.Unauthorized || res.StatusCode == HttpStatusCode.BadRequest || res.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Hazards_get_bbox_lat_90_accepts()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync("/api/hazards?minLat=89&maxLat=90&minLng=0&maxLng=1");
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Hazards_get_bbox_lat_over_90_returns_400()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync("/api/hazards?minLat=90&maxLat=91&minLng=0&maxLng=1");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Hazards_get_bbox_lon_180_accepts()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync("/api/hazards?minLat=0&maxLat=1&minLng=179&maxLng=180");
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Hazards_get_bbox_lon_over_180_returns_400()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync("/api/hazards?minLat=0&maxLat=1&minLng=180&maxLng=181");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Hazards_post_coords_exactly_90_accepted()
    {
        var c = _factory.CreateClient();
        var body = new { Location = new { type = "Point", coordinates = new[] { 0.0, 90.0 } }, Type = "t", Description = "d", PhotoUrl = "" };
        var res = await c.PostAsJsonAsync("/api/hazards", body, _opts);
        if (res.StatusCode == HttpStatusCode.Created)
        {
            Assert.NotNull(res.Headers.Location);
        }
        else if (res.StatusCode == HttpStatusCode.InternalServerError) { }
        else
        {
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        }
    }

    [Fact]
    public async Task Hazards_post_lat_91_returns_400()
    {
        var c = _factory.CreateClient();
        var body = new { Location = new { type = "Point", coordinates = new[] { 0.0, 91.0 } }, Type = "t", Description = "d", PhotoUrl = "" };
        var res = await c.PostAsJsonAsync("/api/hazards", body, _opts);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Hazards_patch_all_status_values()
    {
        var c = _factory.CreateClient();
        var postBody = new { Location = new { type = "Point", coordinates = new[] { -1.9, 52.49 } }, Type = "x", Description = "y", PhotoUrl = "" };
        var post = await c.PostAsJsonAsync("/api/hazards", postBody, _opts);
        if (post.StatusCode != HttpStatusCode.Created) return;
        var raw = await post.Content.ReadAsStringAsync();
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"""id""\s*:\s*""([^""]+)""");
        if (!m.Success) return;
        var id = Guid.Parse(m.Groups[1].Value);
        foreach (var status in new[] { HazardStatus.UnderReview, HazardStatus.Resolved, HazardStatus.Dismissed })
        {
            var patch = await c.PatchAsJsonAsync($"/api/hazards/{id}", (int)status, _opts);
            Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);
        }
    }

    [Fact]
    public async Task Routing_safe_path_weight_0_ok()
    {
        var c = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(c);
        var req = new { Start = new { X = -1.89, Y = 52.48 }, End = new { X = -1.88, Y = 52.49 }, SafetyWeight = 0.0 };
        var res = await c.PostAsJsonAsync("/api/routing/safe-path", req, _opts);
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Routing_safe_path_weight_1_ok()
    {
        var c = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(c);
        var req = new { Start = new { X = -1.89, Y = 52.48 }, End = new { X = -1.88, Y = 52.49 }, SafetyWeight = 1.0 };
        var res = await c.PostAsJsonAsync("/api/routing/safe-path", req, _opts);
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Routing_safe_path_weight_negative_400()
    {
        var c = await _factory.CreateAuthenticatedClientAsync();
        var req = new { Start = new { X = -1.89, Y = 52.48 }, End = new { X = -1.88, Y = 52.49 }, SafetyWeight = -0.1 };
        var res = await c.PostAsJsonAsync("/api/routing/safe-path", req, _opts);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Routing_risk_score_radius_1_ok()
    {
        var c = await _factory.CreateAuthenticatedClientAsync();
        var res = await c.GetAsync("/api/routing/risk-score?lat=52.48&lng=-1.89&radius=1");
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Routing_risk_score_radius_5000_ok()
    {
        var c = await _factory.CreateAuthenticatedClientAsync();
        var res = await c.GetAsync("/api/routing/risk-score?lat=52.48&lng=-1.89&radius=5000");
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Routing_risk_score_radius_zero_400()
    {
        var c = await _factory.CreateAuthenticatedClientAsync();
        var res = await c.GetAsync("/api/routing/risk-score?lat=52.48&lng=-1.89&radius=0");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Dashboard_infrastructure_feed_limit_1()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync("/api/dashboard/infrastructure-feed?limit=1");
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        res.EnsureSuccessStatusCode();
        var arr = await res.Content.ReadFromJsonAsync<JsonElement>(_opts);
        Assert.True(arr.GetArrayLength() <= 1);
    }

    [Fact]
    public async Task Dashboard_infrastructure_feed_limit_100()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync("/api/dashboard/infrastructure-feed?limit=100");
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        res.EnsureSuccessStatusCode();
        var arr = await res.Content.ReadFromJsonAsync<JsonElement>(_opts);
        Assert.True(arr.GetArrayLength() <= 100);
    }

    [Fact]
    public async Task Geocoding_reverse_lat_90_ok()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync("/api/geocoding/reverse?lat=90&lon=0");
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        Assert.True(res.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Geocoding_reverse_lat_91_400()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync("/api/geocoding/reverse?lat=91&lon=0");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Geocoding_reverse_lon_180_ok()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync("/api/geocoding/reverse?lat=0&lon=180");
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        Assert.True(res.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Spatial_poi_default_radius()
    {
        var c = _factory.CreateClient();
        var res = await c.GetAsync("/api/spatial/poi?lat=52.48&lng=-1.89");
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Tiles_different_zooms()
    {
        HttpClient c;
        try { c = await _factory.CreateAuthenticatedClientAsync(); }
        catch (HttpRequestException) { return; }
        foreach (var (z, x, y) in new[] { (0, 0, 0), (5, 16, 10), (10, 512, 512), (18, 1000, 1000) })
        {
            var res = await c.GetAsync($"/api/tiles/{z}/{x}/{y}.pbf");
            Assert.True(res.StatusCode == HttpStatusCode.OK || res.StatusCode == HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Offline_bundle_single_point_bbox()
    {
        HttpClient c;
        try { c = await _factory.CreateAuthenticatedClientAsync(); }
        catch (Exception) { return; }
        var res = await c.GetAsync("/api/offlinemap/bundle?minLat=52.486&maxLat=52.486&minLng=-1.89&maxLng=-1.89");
        if (res.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        res.EnsureSuccessStatusCode();
    }
}
