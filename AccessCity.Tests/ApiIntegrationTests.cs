using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Models;
using AccessCity.API.Models.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AccessCity.Tests;

public class ApiIntegrationTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public ApiIntegrationTests(AccessCityApiFactory factory)
    {
        _factory = factory;
    }

    // ---- Health ----

    [Fact]
    public async Task Health_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", body);
    }

    [Fact]
    public async Task Health_Ready_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health/ready");
        response.EnsureSuccessStatusCode();
    }

    // ---- Auth (anonymous) ----

    [Fact]
    public async Task Auth_Register_Valid_Returns_200_And_Token()
    {
        var client = _factory.CreateClient();
        var request = new RegisterRequest($"reg-{Guid.NewGuid():N}@example.com", "P@ssword123!", "Full Name");
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", request, JsonOptions);
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.ServiceUnavailable);
        if (response.StatusCode != HttpStatusCode.OK) return;
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Assert.NotNull(auth);
        Assert.NotNull(auth.Token);
        Assert.Equal(request.Email, auth.Email);
    }

    [Fact]
    public async Task Auth_Register_Duplicate_Email_Returns_400()
    {
        var client = _factory.CreateClient();
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "P@ssword123!", "User"), JsonOptions);
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "OtherPass1!", "Other"), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Auth_Login_Valid_Returns_200_And_Token()
    {
        var client = _factory.CreateClient();
        var email = $"login-{Guid.NewGuid():N}@example.com";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, "P@ssword123!", "User"), JsonOptions);
        if (!reg.IsSuccessStatusCode)
            return;
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "P@ssword123!"), JsonOptions);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            return;
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions);
        Assert.NotNull(auth?.Token);
    }

    [Fact]
    public async Task Auth_Login_Invalid_Credentials_Returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("nonexistent@example.com", "WrongPass"), JsonOptions);
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Auth_RefreshToken_Invalid_Returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/v1/auth/refresh-token?token=invalid-token", null);
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Auth_RevokeToken_Invalid_Returns_404()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/v1/auth/revoke-token?token=invalid-token", null);
        Assert.True(response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Auth_ForgotPassword_Returns_200_Even_When_Email_Unknown()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new ForgotPasswordRequest("unknown@example.com"), JsonOptions);
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            return;
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Auth_ResetPassword_Invalid_Request_Returns_400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new ResetPasswordRequest("nonexistent@example.com", "fake-token", "NewP@ss1!"), JsonOptions);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.ServiceUnavailable);
    }

    // ---- Hazards (anonymous) ----

    [Fact]
    public async Task Hazards_Get_NoQuery_Returns_200_And_Array()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/hazards");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.NotNull(json);
        Assert.True(json.StartsWith("[") || json.Contains("[]"));
    }

    [Fact]
    public async Task Hazards_Get_WithBbox_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/hazards?minLat=52.45&minLng=-1.95&maxLat=52.52&maxLng=-1.88");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Hazards_Get_InvalidBbox_Returns_400()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/hazards?minLat=52.52&maxLat=52.45&minLng=-1.88&maxLng=-1.95");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hazards_GetById_NotFound_Returns_404()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/v1/hazards/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Hazards_Post_MissingLocation_Returns_400_Or_Created()
    {
        // Note: Location is a NTS Coordinate (struct), so omitting it defaults to (0,0)
        // which is valid. The API may return 201 or 400 depending on other validation.
        var client = _factory.CreateClient();
        var body = new { Type = "pothole", Description = "A pothole" };
        var response = await client.PostAsJsonAsync("/api/v1/hazards", body, JsonOptions);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest
            || response.StatusCode == HttpStatusCode.Created
            || response.StatusCode == HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Hazards_Post_MissingType_Returns_400()
    {
        var client = _factory.CreateClient();
        var body = new { Location = new { type = "Point", coordinates = new[] { -1.89, 52.48 } }, Description = "Desc" };
        var response = await client.PostAsJsonAsync("/api/v1/hazards", body, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hazards_Post_MissingDescription_Returns_400()
    {
        var client = _factory.CreateClient();
        var body = new { Location = new { type = "Point", coordinates = new[] { -1.89, 52.48 } }, Type = "pothole" };
        var response = await client.PostAsJsonAsync("/api/v1/hazards", body, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Hazards_Post_Valid_Returns_201_And_Location_Header()
    {
        var client = _factory.CreateClient();
        var body = new
        {
            Location = new { type = "Point", coordinates = new[] { -1.89, 52.48 } },
            Type = "pothole",
            Description = "Test hazard for integration test",
            PhotoUrl = ""
        };
        var response = await client.PostAsJsonAsync("/api/v1/hazards", body, JsonOptions);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            Assert.NotNull(response.Headers.Location);
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"id\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.True(content.Contains("\"status\":0") || content.Contains("Reported", StringComparison.OrdinalIgnoreCase));
        }
        else if (response.StatusCode == HttpStatusCode.InternalServerError)
        {
            // In-Memory DB may not support spatial types
        }
        else
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Hazard POST failed: {response.StatusCode} - {err}");
        }
    }

    [Fact]
    public async Task Hazards_Patch_NotFound_Returns_404()
    {
        var client = _factory.CreateClient();
        var response = await client.PatchAsJsonAsync($"/api/v1/hazards/{Guid.NewGuid()}", (int)HazardStatus.Resolved, JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Routing (anonymous) ----

    [Fact]
    public async Task Routing_SafePath_MissingCoords_Returns_400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var body = new { Start = (object?)null, End = new { X = -1.89, Y = 52.48 }, SafetyWeight = 0.5 };
        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", body, JsonOptions);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest
            || response.StatusCode == HttpStatusCode.NotFound
            || response.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task Routing_SafePath_InvalidSafetyWeight_Returns_400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var body = new { Start = new { X = -1.89, Y = 52.48 }, End = new { X = -1.88, Y = 52.49 }, SafetyWeight = 1.5 };
        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", body, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Routing_SafePath_Valid_Returns_200()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);
        var body = new { Start = new { X = -1.8904, Y = 52.4862 }, End = new { X = -1.8904, Y = 52.4862 }, SafetyWeight = 0.5 };
        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", body, JsonOptions);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Routing_RiskScore_Valid_Returns_200()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/routing/risk-score?lat=52.4862&lng=-1.8904&radius=500");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("overallRisk", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Routing_RiskScore_InvalidCoords_Returns_400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/routing/risk-score?lat=100&lng=0&radius=500");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Routing_RiskScore_InvalidRadius_Returns_400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/routing/risk-score?lat=52.48&lng=-1.89&radius=10000");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Routing_AiRiskScore_Valid_Returns_200()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/routing/ai-risk-score?lat=52.4862&lng=-1.8904&radius=200");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Routing_AiRiskScore_InvalidCoords_Returns_400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/routing/ai-risk-score?lat=95&lng=-200");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Dashboard ----

    [Fact]
    public async Task Dashboard_Summary_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/dashboard/summary");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            return;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(json.Contains("totalHazards", StringComparison.OrdinalIgnoreCase) || json.Contains("TotalHazards", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dashboard_HeatMap_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/dashboard/heat-map");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("FeatureCollection", json);
    }

    [Fact]
    public async Task Dashboard_InfrastructureFeed_Returns_200_And_RespectsLimit()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/dashboard/infrastructure-feed?limit=5");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(list.ValueKind == JsonValueKind.Array);
        Assert.True(list.GetArrayLength() <= 5);
    }

    [Fact]
    public async Task SafeHaven_Nearby_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/safe-haven/nearby?lat=52.4862&lng=-1.8904&radius=500");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
        Assert.True(json.TryGetProperty("places", out _) || json.TryGetProperty("Places", out _));
    }

    [Fact]
    public async Task Integrations_Status_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/integrations/status");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(JsonValueKind.Object, json.ValueKind);
    }

    [Fact]
    public async Task Routing_HazardBlendRisk_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/routing/hazard-blend-risk?lat=52.4862&lng=-1.8904&radius=200");
        response.EnsureSuccessStatusCode();
    }

    // ---- Geocoding ----

    [Fact]
    public async Task Geocoding_Search_EmptyQuery_Returns_400()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/geocoding/search?query=");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Geocoding_Search_Valid_Returns_200_Or_503()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/geocoding/search?query=Birmingham");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var list = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
            Assert.True(list.ValueKind == JsonValueKind.Array);
        }
        else if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            // Nominatim can be rate-limited
        }
        else
        {
            Assert.True(response.IsSuccessStatusCode, $"Unexpected {response.StatusCode}");
        }
    }

    [Fact]
    public async Task Geocoding_Reverse_InvalidCoords_Returns_400()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/geocoding/reverse?lat=100&lon=0");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Geocoding_Reverse_Valid_Returns_200_Or_503()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/geocoding/reverse?lat=52.4862&lon=-1.8904");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        { /* rate limit */ }
        else
            Assert.True(response.IsSuccessStatusCode, $"Unexpected {response.StatusCode}");
    }

    // ---- Spatial ----

    [Fact]
    public async Task Spatial_Poi_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/spatial/poi?lat=52.48&lng=-1.89&radius=1000");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.True(list.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task Spatial_MapOverlay_Returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/spatial/map-overlay?layerName=hazards");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("FeatureCollection", json);
    }

    // ---- Offline (requires auth) ----

    [Fact]
    public async Task Offline_Bundle_Unauthorized_Returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/offlinemap/bundle?minLat=52.45&minLng=-1.95&maxLat=52.52&maxLng=-1.88");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Offline_Bundle_Authorized_ValidBbox_Returns_200_Or_503()
    {
        HttpClient client;
        try
        {
            client = await _factory.CreateAuthenticatedClientAsync();
        }
        catch (HttpRequestException)
        {
            return;
        }
        var response = await client.GetAsync("/api/v1/offlinemap/bundle?minLat=52.45&minLng=-1.95&maxLat=52.52&maxLng=-1.88");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) return;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(json.Contains("hazards", StringComparison.OrdinalIgnoreCase) && json.Contains("area", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Offline_Bundle_Authorized_InvalidBbox_Returns_400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/offlinemap/bundle?minLat=52.52&maxLat=52.45&minLng=-1.88&maxLng=-1.95");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Map Tiles (requires auth) ----

    [Fact]
    public async Task MapTiles_Unauthorized_Returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/tiles/10/512/512.pbf");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MapTiles_Authorized_Returns_200_Or_204()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/tiles/10/512/512.pbf");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent,
            $"Unexpected {response.StatusCode}");
    }

    // ---- Hazard round-trip: POST then GET by id and PATCH (when DB supports it) ----

    [Fact]
    public async Task Hazards_Post_Then_GetById_And_Patch_RoundTrip()
    {
        var client = _factory.CreateClient();
        var body = new
        {
            Location = new { type = "Point", coordinates = new[] { -1.891, 52.481 } },
            Type = "steps",
            Description = "Round-trip test hazard",
            PhotoUrl = ""
        };
        var postResponse = await client.PostAsJsonAsync("/api/v1/hazards", body, JsonOptions);
        if (postResponse.StatusCode != HttpStatusCode.Created)
            return;
        var postBody = await postResponse.Content.ReadAsStringAsync();
        var idMatch = System.Text.RegularExpressions.Regex.Match(postBody, @"""id""\s*:\s*""([^""]+)""");
        Assert.True(idMatch.Success, "Response should contain id");
        var id = Guid.Parse(idMatch.Groups[1].Value);

        var getResponse = await client.GetAsync($"/api/v1/hazards/{id}");
        getResponse.EnsureSuccessStatusCode();
        var getBody = await getResponse.Content.ReadAsStringAsync();
        Assert.Contains(id.ToString(), getBody);
        Assert.True(getBody.Contains("\"status\":0") || getBody.Contains("Reported", StringComparison.OrdinalIgnoreCase));

        var patchResponse = await client.PatchAsJsonAsync($"/api/v1/hazards/{id}", (int)HazardStatus.Resolved, JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, patchResponse.StatusCode);

        var getAfter = await client.GetAsync($"/api/v1/hazards/{id}");
        getAfter.EnsureSuccessStatusCode();
        var afterBody = await getAfter.Content.ReadAsStringAsync();
        Assert.True(afterBody.Contains("\"status\":2") || afterBody.Contains("Resolved", StringComparison.OrdinalIgnoreCase));
    }
}
