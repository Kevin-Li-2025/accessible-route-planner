using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Models;

namespace AccessCity.Tests;

public sealed class AiAssistEndpointTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;

    public AiAssistEndpointTests(AccessCityApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RouteExplanation_AcceptsFrontendGeoJsonRoutePayload()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/ai-assist/route-explanation",
            new
            {
                routeRequest = new
                {
                    profile = "manual-wheelchair",
                    safetyWeight = 0.8,
                    preferences = new[] { "avoid-stairs", "prefer-crossings" }
                },
                route = new
                {
                    path = new
                    {
                        type = "LineString",
                        coordinates = new[] { new[] { -1.89, 52.48 }, new[] { -1.88, 52.485 } }
                    },
                    distance = 840,
                    estimatedTime = 720,
                    safetyScore = 0.82,
                    warnings = new[] { "Raised kerb near final crossing" }
                }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<RouteExplanationResponse>();
        Assert.NotNull(parsed);
        Assert.False(parsed!.ForRouteDecision);
        Assert.Contains("deterministic router", parsed.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual-wheelchair", parsed.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Raised kerb", parsed.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HazardReportDraft_ReturnsReviewOnlyDuplicateAndTextSuggestions()
    {
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/hazards",
            new
            {
                type = "blocked_pavement",
                description = "Pavement is blocked by a parked van.",
                photoUrl = "",
                location = new { x = -1.8904, y = 52.4862 }
            });
        createResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            "/api/v1/ai-assist/hazards/report-draft",
            new
            {
                latitude = 52.48621,
                longitude = -1.89039,
                type = "blocked_pavement",
                description = "  Pavement blocked and wheelchair cannot pass safely. ",
                photoAttached = true
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<HazardReportDraftAiResult>();
        Assert.NotNull(parsed);
        Assert.False(parsed!.ForRouteDecision);
        Assert.Equal("Pavement blocked and wheelchair cannot pass safely.", parsed.Text.NormalizedDescription);
        Assert.Equal("obstruction", parsed.Text.SuggestedType);
        Assert.Equal("high", parsed.Text.SuggestedSeverity);
        Assert.True(parsed.ShouldReviewExistingReport);
        Assert.NotEmpty(parsed.DuplicateSuggestions);
        Assert.Contains("photo-attached", parsed.Text.Tags);
        Assert.All(parsed.MissingOsmAttributeCandidates, candidate => Assert.False(candidate.CanAutoApply));
    }

    [Fact]
    public async Task HazardPhotoAnalysis_ReturnsReviewOnlyAccessibilityCandidates()
    {
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/hazards",
            new
            {
                type = "missing_curb_ramp",
                description = "Raised kerb, no ramp, narrow pavement, uneven concrete surface.",
                photoUrl = "/api/v1/hazards/photos/test-kerb.jpg",
                location = new { x = -1.8904, y = 52.4862 }
            });
        createResponse.EnsureSuccessStatusCode();
        await using var createdStream = await createResponse.Content.ReadAsStreamAsync();
        using var createdJson = await JsonDocument.ParseAsync(createdStream);
        var hazardId = createdJson.RootElement.GetProperty("id").GetGuid();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai-assist/hazards/{hazardId}/photo-analysis",
            new
            {
                photoUrl = "/api/v1/hazards/photos/test-kerb.jpg",
                observationText = "Wheelchair cannot cross because there is no dropped kerb.",
                includeDraftVerification = true,
                submitForReview = false
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<HazardPhotoAiAnalysisResult>();
        Assert.NotNull(parsed);
        Assert.Equal(hazardId, parsed!.HazardId);
        Assert.False(parsed.ForRouteDecision);
        Assert.StartsWith("http://", parsed.PhotoUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("review_required", parsed.ReviewStatus);
        Assert.Contains(parsed.AttributeCandidates, candidate => candidate.Attribute == "curb_ramp" && candidate.Value == "false");
        Assert.Contains(parsed.AttributeCandidates, candidate => candidate.Attribute == "width_metres");
        Assert.Contains(parsed.AttributeCandidates, candidate => candidate.Attribute == "photos");
        Assert.All(parsed.AttributeCandidates, candidate => Assert.False(candidate.CanAutoApply));
        Assert.NotNull(parsed.DraftVerification);
        Assert.Contains(parsed.Guardrails, guardrail => guardrail.Contains("cannot change routing graph", StringComparison.OrdinalIgnoreCase));
    }
}
