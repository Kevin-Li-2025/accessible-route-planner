using AccessCity.API.Configuration;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace AccessCity.Tests;

public sealed class AiAssistServiceTests
{
    private readonly AiAssistService _service = new(Options.Create(new AiEnrichmentOptions
    {
        Provider = "local-rules",
        DuplicateRadiusMetres = 30,
        MinimumCandidateConfidence = 0.35,
        AllowRouteDecisionInfluence = false,
        MaxExplanationWarnings = 3
    }));

    [Fact]
    public async Task EnrichHazard_NormalizesText_And_ProducesReviewOnlyOsmCandidates()
    {
        var hazard = new HazardReport
        {
            Id = Guid.NewGuid(),
            Type = "access issue",
            Description = "  Blocked curb ramp   with narrow sidewalk and uneven gravel surface.  ",
            PhotoUrl = "https://example.test/photo.jpg",
            Location = new Point(-0.1257, 51.5085) { SRID = 4326 },
            ReportedAt = DateTime.UtcNow,
            Status = HazardStatus.Reported,
            Source = "user"
        };
        var nearby = new List<HazardReport>
        {
            hazard,
            new()
            {
                Id = Guid.NewGuid(),
                Type = "missing curb ramp",
                Description = "Raised kerb blocks wheelchair access",
                Location = new Point(-0.12568, 51.50857) { SRID = 4326 },
                ReportedAt = DateTime.UtcNow,
                Status = HazardStatus.Reported,
                Source = "user"
            }
        };

        var result = await _service.EnrichHazardAsync(hazard, nearby, CancellationToken.None);

        Assert.False(result.ForRouteDecision);
        Assert.Equal("Blocked curb ramp with narrow sidewalk and uneven gravel surface.", result.Text.NormalizedDescription);
        Assert.Equal("missing_curb_ramp", result.Text.SuggestedType);
        Assert.Equal("high", result.Text.SuggestedSeverity);
        Assert.Contains("accessibility", result.Text.Tags);
        Assert.Contains("photo-attached", result.Text.Tags);
        Assert.Contains(result.MissingOsmAttributeCandidates, candidate => candidate.Attribute == "curb_ramp");
        Assert.Contains(result.MissingOsmAttributeCandidates, candidate => candidate.Attribute == "width_metres");
        Assert.Contains(result.MissingOsmAttributeCandidates, candidate => candidate.Attribute == "surface");
        Assert.Contains(result.MissingOsmAttributeCandidates, candidate => candidate.Attribute == "smoothness");
        Assert.All(result.MissingOsmAttributeCandidates, candidate => Assert.False(candidate.CanAutoApply));
        Assert.Single(result.DuplicateSuggestions);
        Assert.Contains(result.Guardrails, guardrail => guardrail.Contains("must not generate route geometry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExplainRoute_FormatsExistingRoute_WithoutDecisionAuthority()
    {
        var request = new RouteExplanationRequest
        {
            RouteRequest = new RouteRequest
            {
                Profile = "manual-wheelchair",
                SafetyWeight = 0.8,
                Preferences = ["avoid-stairs", "prefer-crossings"]
            },
            Route = new RouteResponse
            {
                Distance = 840,
                EstimatedTime = 720,
                SafetyScore = 0.82,
                Warnings = ["Raised kerb near final crossing"]
            }
        };

        var result = await _service.ExplainRouteAsync(request, CancellationToken.None);

        Assert.False(result.ForRouteDecision);
        Assert.Contains("deterministic router", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual-wheelchair", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avoid-stairs", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Limitations, limitation => limitation.Contains("does not create routes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Limitations, limitation => limitation.Contains("edge costs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReviewAccessibilityProfile_ReturnsHumanReviewCandidates_WithoutDecisionAuthority()
    {
        var profile = new InfrastructureAccessibilityProfile
        {
            VerificationStatus = "unverified",
            Confidence = 0.32,
            MissingFields =
            [
                "width_metres",
                "kerb",
                "incline_percent",
                "last_verified_at"
            ]
        };

        var result = await _service.ReviewAccessibilityProfileAsync(42, profile, CancellationToken.None);

        Assert.False(result.ForRouteDecision);
        Assert.Equal(42, result.InfrastructureAssetId);
        Assert.Contains(result.MissingAttributeCandidates, candidate => candidate.Attribute == "width_metres");
        Assert.Contains(result.MissingAttributeCandidates, candidate => candidate.Attribute == "curb_ramp");
        Assert.Contains(result.MissingAttributeCandidates, candidate => candidate.Attribute == "last_verified_at");
        Assert.All(result.MissingAttributeCandidates, candidate => Assert.False(candidate.CanAutoApply));
        Assert.Contains(result.Guardrails, guardrail => guardrail.Contains("cannot generate routes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.VerificationChecklist, item => item.Contains("low confidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constructor_RejectsRouteDecisionInfluence()
    {
        var options = Options.Create(new AiEnrichmentOptions
        {
            AllowRouteDecisionInfluence = true
        });

        var error = Assert.Throws<InvalidOperationException>(() => new AiAssistService(options));
        Assert.Contains("AllowRouteDecisionInfluence", error.Message, StringComparison.Ordinal);
    }
}
