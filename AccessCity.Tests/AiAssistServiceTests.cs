using AccessCity.API.Configuration;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using System.Net;
using System.Text;

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
    public async Task OpenAiAccessibilityInference_ParsesStructuredOutput_AsReviewOnlyCandidates()
    {
        var handler = new FakeOpenAiHandler("""
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "{\"summary\":\"Review raised kerb and concrete surface.\",\"candidates\":[{\"attribute\":\"surface\",\"value\":\"concrete\",\"confidence\":0.82,\"evidence\":\"Image/text shows concrete paving.\"},{\"attribute\":\"curb_ramp\",\"value\":\"false\",\"confidence\":0.88,\"evidence\":\"Raised kerb and no ramp visible.\"}]}"
                    }
                  ]
                }
              ]
            }
            """);
        var provider = new OpenAiAccessibilityInferenceProvider(
            new HttpClient(handler),
            Options.Create(new AiEnrichmentOptions
            {
                Provider = "openai",
                OpenAiApiKey = "test-key",
                OpenAiModel = "gpt-5-mini",
                MinimumCandidateConfidence = 0.35
            }));

        var result = await provider.InferAsync(
            99,
            new InfrastructureAccessibilityProfile { Confidence = 0.4, VerificationStatus = "partial" },
            new AccessibilityAiInferenceRequest
            {
                ObservationText = "Raised kerb, no ramp.",
                Photos = [new AccessibilityPhotoInput { Url = "https://example.com/kerb.jpg" }]
            },
            CancellationToken.None);

        Assert.Equal("openai", result.Provider);
        Assert.Equal("gpt-5-mini", result.Model);
        Assert.False(result.ForRouteDecision);
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "surface" && candidate.Value == "concrete");
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "curb_ramp" && candidate.Value == "false");
        Assert.All(result.AttributeCandidates, candidate => Assert.False(candidate.CanAutoApply));
        Assert.NotNull(result.DraftVerification);
        Assert.Equal("concrete", result.DraftVerification!.Path!.Surface);
        Assert.False(result.DraftVerification.Path.HasCurbRamp);
        Assert.Contains("Bearer test-key", handler.AuthorizationHeader);
        Assert.Contains("input_image", handler.RequestJson);
    }

    [Fact]
    public async Task NebiusAccessibilityInference_NormalizesNaturalLanguageCandidates_AsReviewOnlyDraft()
    {
        var handler = new FakeOpenAiHandler("""
            {
              "choices": [
                {
                  "message": {
                    "content": "Analysis ignored by parser. {\"summary\":\"Review crossing kerb and rough surface.\",\"candidates\":[{\"attribute\":\"raised kerb\",\"value\":\"present\",\"confidence\":0.99,\"evidence\":\"Raised kerb at crossing.\"},{\"attribute\":\"ramp\",\"value\":\"absent\",\"confidence\":0.91,\"evidence\":\"No dropped ramp visible.\"},{\"attribute\":\"crossing width\",\"value\":\"narrow\",\"confidence\":0.87,\"evidence\":\"Crossing looks too narrow.\"},{\"attribute\":\"edge surface\",\"value\":\"gravel\",\"confidence\":0.86,\"evidence\":\"Loose gravel edge.\"},{\"attribute\":\"pavement condition\",\"value\":\"uneven, broken\",\"confidence\":0.84,\"evidence\":\"Uneven broken pavement.\"}]}"
                  }
                }
              ]
            }
            """);
        var provider = new NebiusAccessibilityInferenceProvider(
            new HttpClient(handler),
            Options.Create(new AiEnrichmentOptions
            {
                Provider = "nebius",
                NebiusApiKey = "test-key",
                NebiusBaseUrl = "https://api.tokenfactory.nebius.com/v1",
                NebiusModel = "openai/gpt-oss-120b-fast",
                NebiusMaxTokens = 700,
                MinimumCandidateConfidence = 0.35
            }));

        var result = await provider.InferAsync(
            100,
            new InfrastructureAccessibilityProfile
            {
                Confidence = 0.38,
                VerificationStatus = "partial",
                MissingFields = ["surface", "smoothness", "kerb", "width_metres"]
            },
            new AccessibilityAiInferenceRequest
            {
                ObservationText = "Raised kerb, no ramp, narrow crossing, gravel edge, uneven broken pavement.",
                Photos = [new AccessibilityPhotoInput { Url = "https://example.com/crossing.jpg", Caption = "crossing" }]
            },
            CancellationToken.None);

        Assert.Equal("nebius", result.Provider);
        Assert.Equal("openai/gpt-oss-120b-fast", result.Model);
        Assert.False(result.ForRouteDecision);
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "kerb_height_metres" && candidate.Value == ">0.06");
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "curb_ramp" && candidate.Value == "false");
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "width_metres" && candidate.Value == "measure_clear_width");
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "surface" && candidate.Value == "gravel");
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "smoothness" && candidate.Value == "bad");
        Assert.All(result.AttributeCandidates, candidate => Assert.False(candidate.CanAutoApply));
        Assert.NotNull(result.DraftVerification);
        Assert.False(result.DraftVerification!.Path!.HasCurbRamp);
        Assert.Equal("gravel", result.DraftVerification.Path.Surface);
        Assert.Equal("bad", result.DraftVerification.Path.Smoothness);
        Assert.Contains("Bearer test-key", handler.AuthorizationHeader);
        Assert.Equal("/v1/chat/completions", handler.RequestPath);
        Assert.Contains("response_format", handler.RequestJson);
        Assert.DoesNotContain("image_url", handler.RequestJson);
        Assert.Contains("Photo metadata", handler.RequestJson);
    }

    [Fact]
    public async Task LocalVisionAccessibilityInference_ParsesVisionCandidates_AsReviewOnlyDraft()
    {
        var handler = new FakeOpenAiHandler("""
            {
              "model": "accesscity-convnext-tiny-v1",
              "candidates": [
                {"attribute":"curb_ramp","value":"false","confidence":0.88,"evidence":"Vision detected missing ramp.","source":"accesscity_local_vision"},
                {"attribute":"kerb_height_metres","value":">0.06","confidence":0.75,"evidence":"Raised kerb should be measured.","source":"accesscity_local_vision"},
                {"attribute":"smoothness","value":"bad","confidence":0.81,"evidence":"Surface problem detected.","source":"accesscity_local_vision"},
                {"attribute":"obstacle","value":"present","confidence":0.77,"evidence":"Obstacle candidate detected.","source":"accesscity_local_vision"}
              ],
              "forRouteDecision": false,
              "latencyMs": 8.5
            }
            """);
        var provider = new LocalVisionAccessibilityInferenceProvider(
            new HttpClient(handler),
            Options.Create(new AiEnrichmentOptions
            {
                Provider = "local-vision",
                VisionModelEndpoint = "http://vision.local",
                VisionModelMinimumConfidence = 0.55,
                MinimumCandidateConfidence = 0.35
            }));

        var result = await provider.InferAsync(
            101,
            new InfrastructureAccessibilityProfile { Confidence = 0.5, VerificationStatus = "partial" },
            new AccessibilityAiInferenceRequest
            {
                Photos = [new AccessibilityPhotoInput { Url = "https://example.com/kerb.jpg", Caption = "raised kerb" }]
            },
            CancellationToken.None);

        Assert.Equal("local-vision", result.Provider);
        Assert.Equal("accesscity-convnext-tiny-v1", result.Model);
        Assert.False(result.ForRouteDecision);
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "curb_ramp" && candidate.Value == "false");
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "smoothness" && candidate.Value == "bad");
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "obstacle" && candidate.Value == "true");
        Assert.All(result.AttributeCandidates, candidate => Assert.False(candidate.CanAutoApply));
        Assert.NotNull(result.DraftVerification);
        Assert.False(result.DraftVerification!.Path!.HasCurbRamp);
        Assert.Equal("bad", result.DraftVerification.Path.Smoothness);
        Assert.Equal("/v1/accessibility-vision/analyze", handler.RequestPath);
        Assert.Contains("thresholdFloor", handler.RequestJson);
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

    private sealed class FakeOpenAiHandler : HttpMessageHandler
    {
        private readonly string _responseJson;

        public FakeOpenAiHandler(string responseJson)
        {
            _responseJson = responseJson;
        }

        public string AuthorizationHeader { get; private set; } = string.Empty;
        public string RequestJson { get; private set; } = string.Empty;
        public string RequestPath { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AuthorizationHeader = request.Headers.Authorization?.ToString() ?? string.Empty;
            RequestPath = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestJson = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
