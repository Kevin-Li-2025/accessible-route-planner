using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AccessCity.API.Configuration;
using AccessCity.API.Models;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Services;

public interface IAccessibilityAiInferenceService
{
    Task<AccessibilityAiInferenceResult> InferAsync(
        long assetId,
        InfrastructureAccessibilityProfile profile,
        AccessibilityAiInferenceRequest request,
        CancellationToken cancellationToken);
}

public sealed class AccessibilityAiInferenceService : IAccessibilityAiInferenceService
{
    private readonly LocalAccessibilityAiInferenceProvider _localProvider;
    private readonly LocalVisionAccessibilityInferenceProvider _localVisionProvider;
    private readonly OpenAiAccessibilityInferenceProvider _openAiProvider;
    private readonly NebiusAccessibilityInferenceProvider _nebiusProvider;
    private readonly AiEnrichmentOptions _options;
    private readonly ILogger<AccessibilityAiInferenceService> _logger;

    public AccessibilityAiInferenceService(
        LocalAccessibilityAiInferenceProvider localProvider,
        LocalVisionAccessibilityInferenceProvider localVisionProvider,
        OpenAiAccessibilityInferenceProvider openAiProvider,
        NebiusAccessibilityInferenceProvider nebiusProvider,
        IOptions<AiEnrichmentOptions> options,
        ILogger<AccessibilityAiInferenceService> logger)
    {
        _localProvider = localProvider;
        _localVisionProvider = localVisionProvider;
        _openAiProvider = openAiProvider;
        _nebiusProvider = nebiusProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AccessibilityAiInferenceResult> InferAsync(
        long assetId,
        InfrastructureAccessibilityProfile profile,
        AccessibilityAiInferenceRequest request,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRequest(request, _options);
        var useLocalVision = string.Equals(_options.Provider, "local-vision", StringComparison.OrdinalIgnoreCase);
        var useOpenAi = string.Equals(_options.Provider, "openai", StringComparison.OrdinalIgnoreCase);
        var useNebius = string.Equals(_options.Provider, "nebius", StringComparison.OrdinalIgnoreCase);
        if (useLocalVision && _localVisionProvider.IsConfigured)
        {
            try
            {
                return await _localVisionProvider.InferAsync(assetId, profile, normalized, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Local vision accessibility inference failed; falling back to local rules.");
                var fallback = await _localProvider.InferAsync(assetId, profile, normalized, cancellationToken);
                fallback.Provider = "local-vision:fallback-local-rules";
                fallback.Limitations.Add("Local vision inference failed or timed out; local deterministic rules generated this fallback result.");
                return fallback;
            }
        }

        if (useNebius && _nebiusProvider.IsConfigured)
        {
            try
            {
                return await _nebiusProvider.InferAsync(assetId, profile, normalized, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Nebius accessibility inference failed; falling back to local rules.");
                var fallback = await _localProvider.InferAsync(assetId, profile, normalized, cancellationToken);
                fallback.Provider = "nebius:fallback-local-rules";
                fallback.Limitations.Add("Nebius inference failed or timed out; local deterministic rules generated this fallback result.");
                return fallback;
            }
        }

        if (useOpenAi && _openAiProvider.IsConfigured)
        {
            try
            {
                return await _openAiProvider.InferAsync(assetId, profile, normalized, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "OpenAI accessibility inference failed; falling back to local rules.");
                var fallback = await _localProvider.InferAsync(assetId, profile, normalized, cancellationToken);
                fallback.Provider = "openai:fallback-local-rules";
                fallback.Limitations.Add("OpenAI inference failed or timed out; local deterministic rules generated this fallback result.");
                return fallback;
            }
        }

        var result = await _localProvider.InferAsync(assetId, profile, normalized, cancellationToken);
        if (useLocalVision && !_localVisionProvider.IsConfigured)
        {
            result.Provider = "local-vision:unconfigured-local-rules";
            result.Limitations.Add("Local vision provider is selected but no endpoint is configured; local deterministic rules generated this result.");
        }
        else if (useOpenAi && !_openAiProvider.IsConfigured)
        {
            result.Provider = "openai:unconfigured-local-rules";
            result.Limitations.Add("OpenAI provider is selected but no API key is configured; local deterministic rules generated this result.");
        }
        else if (useNebius && !_nebiusProvider.IsConfigured)
        {
            result.Provider = "nebius:unconfigured-local-rules";
            result.Limitations.Add("Nebius provider is selected but no API key is configured; local deterministic rules generated this result.");
        }

        return result;
    }

    private static AccessibilityAiInferenceRequest NormalizeRequest(
        AccessibilityAiInferenceRequest request,
        AiEnrichmentOptions options)
    {
        var observationText = string.IsNullOrWhiteSpace(request.ObservationText)
            ? string.Empty
            : string.Join(' ', request.ObservationText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (observationText.Length > Math.Max(100, options.MaxAccessibilityObservationChars))
        {
            observationText = observationText[..Math.Max(100, options.MaxAccessibilityObservationChars)];
        }

        var photos = request.Photos
            .Where(photo => Uri.TryCreate(photo.Url, UriKind.Absolute, out var uri)
                            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Select(photo => new AccessibilityPhotoInput
            {
                Source = string.IsNullOrWhiteSpace(photo.Source) ? "field_photo" : NormalizeToken(photo.Source),
                Url = photo.Url.Trim(),
                Caption = string.IsNullOrWhiteSpace(photo.Caption) ? null : photo.Caption.Trim(),
                TakenAtUtc = photo.TakenAtUtc?.ToUniversalTime()
            })
            .DistinctBy(photo => photo.Url, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(options.MaxAccessibilityPhotos, 0, 12))
            .ToList();

        return new AccessibilityAiInferenceRequest
        {
            ObservationText = observationText,
            Photos = photos,
            IncludeDraftVerification = request.IncludeDraftVerification
        };
    }

    private static string NormalizeToken(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is ':' or '_' or '-' ? character : '_')
            .ToArray();
        var normalized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "field_photo" : normalized;
    }
}

public sealed class LocalAccessibilityAiInferenceProvider
{
    private readonly AiEnrichmentOptions _options;

    public LocalAccessibilityAiInferenceProvider(IOptions<AiEnrichmentOptions> options)
    {
        _options = options.Value;
    }

    public Task<AccessibilityAiInferenceResult> InferAsync(
        long assetId,
        InfrastructureAccessibilityProfile profile,
        AccessibilityAiInferenceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var text = BuildSearchText(request);
        var candidates = new List<MissingOsmAttributeCandidate>();
        AddProfileGapCandidates(candidates, profile);
        AddObservationCandidates(candidates, text, request.Photos.Count);

        var deduped = candidates
            .GroupBy(candidate => candidate.Attribute, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First())
            .Where(candidate => candidate.Confidence >= _options.MinimumCandidateConfidence)
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Attribute, StringComparer.Ordinal)
            .Take(12)
            .ToList();

        var draft = request.IncludeDraftVerification
            ? AccessibilityCandidateDraftBuilder.BuildDraftVerification(deduped, request)
            : null;

        return Task.FromResult(new AccessibilityAiInferenceResult
        {
            InfrastructureAssetId = assetId,
            ForRouteDecision = false,
            Provider = "local-rules",
            Model = "deterministic-rules-v1",
            GeneratedAtUtc = DateTime.UtcNow,
            AdminSummary = BuildAdminSummary(profile, deduped.Count, request.Photos.Count),
            AttributeCandidates = deduped,
            DraftVerification = draft,
            Guardrails = StandardGuardrails(),
            Limitations =
            [
                "Local rules infer candidates from text, photo metadata, and known profile gaps; they do not inspect image pixels.",
                "Candidates must be reviewed before they can update an accessibility profile.",
                "Inference output is never used by the routing algorithm directly."
            ]
        });
    }

    private static string BuildSearchText(AccessibilityAiInferenceRequest request)
    {
        var photoText = string.Join(' ', request.Photos.Select(photo => $"{photo.Source} {photo.Caption} {photo.Url}"));
        return $"{request.ObservationText} {photoText}".ToLowerInvariant();
    }

    private static void AddProfileGapCandidates(
        List<MissingOsmAttributeCandidate> candidates,
        InfrastructureAccessibilityProfile profile)
    {
        var missing = profile.MissingFields.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (missing.Contains("width_metres") || missing.Contains("door_width_metres") || missing.Contains("toilets_door_width_metres"))
        {
            AddCandidate(candidates, "width_metres", "measure_clear_width", 0.74, "Existing profile is missing clear width data.", "profile_gap");
        }

        if (missing.Contains("surface"))
        {
            AddCandidate(candidates, "surface", "verify_material", 0.70, "Existing profile is missing surface material.", "profile_gap");
        }

        if (missing.Contains("smoothness"))
        {
            AddCandidate(candidates, "smoothness", "verify_surface_quality", 0.68, "Existing profile is missing surface smoothness.", "profile_gap");
        }

        if (missing.Contains("kerb") || missing.Contains("curb_ramp"))
        {
            AddCandidate(candidates, "curb_ramp", "verify_flush_or_lowered", 0.76, "Existing profile is missing curb ramp or kerb data.", "profile_gap");
        }

        if (missing.Contains("incline_percent"))
        {
            AddCandidate(candidates, "incline", "measure_percent_grade", 0.64, "Existing profile is missing slope/incline data.", "profile_gap");
        }

        if (missing.Contains("tactile_paving"))
        {
            AddCandidate(candidates, "tactile_paving", "verify_present_absent", 0.62, "Existing profile is missing tactile paving data.", "profile_gap");
        }

        if (missing.Contains("toilets_wheelchair_access") || missing.Contains("toilets_grab_bars"))
        {
            AddCandidate(candidates, "toilets:wheelchair", "verify_accessible_toilet_features", 0.72, "Existing profile is missing accessible restroom details.", "profile_gap");
        }

        if (missing.Contains("last_verified_at"))
        {
            AddCandidate(candidates, "last_verified_at", "field_verify", 0.66, "Existing profile has no recent verification timestamp.", "profile_gap");
        }

        if (profile.Photos.Count == 0)
        {
            AddCandidate(candidates, "photos", "add_field_photo", 0.58, "Existing profile has no supporting field photo.", "profile_gap");
        }
    }

    private static void AddObservationCandidates(List<MissingOsmAttributeCandidate> candidates, string text, int photoCount)
    {
        if (ContainsAny(text, "curb ramp", "kerb ramp", "curb cut", "dropped kerb", "lowered kerb", "flush kerb"))
        {
            AddCandidate(candidates, "curb_ramp", "true", 0.82, "Observation mentions a curb/kerb ramp or flush crossing.", "ai_observation_text");
            AddCandidate(candidates, "kerb_height_metres", "0", 0.68, "Observation suggests the kerb is flush or lowered.", "ai_observation_text");
        }

        if (ContainsAny(text, "no ramp", "missing ramp", "raised kerb", "high kerb", "wheelchair cannot cross"))
        {
            AddCandidate(candidates, "curb_ramp", "false", 0.84, "Observation indicates no usable ramp or a raised kerb.", "ai_observation_text");
            AddCandidate(candidates, "kerb_height_metres", ">0.06", 0.70, "Observation suggests a high kerb that should be measured.", "ai_observation_text");
        }

        if (ContainsAny(text, "narrow", "too tight", "cannot pass", "wheelchair cannot pass", "stroller cannot pass"))
        {
            AddCandidate(candidates, "width_metres", "measure_clear_width", 0.78, "Observation indicates constrained clear width.", "ai_observation_text");
        }

        if (ContainsAny(text, "concrete", "asphalt", "paving stones", "paving_stones", "gravel", "cobblestone", "mud", "grass", "dirt"))
        {
            AddCandidate(candidates, "surface", InferSurface(text), 0.76, "Observation names a visible surface material.", "ai_observation_text");
        }

        if (ContainsAny(text, "uneven", "cracked", "broken", "bumpy", "pothole", "loose surface"))
        {
            AddCandidate(candidates, "smoothness", "bad", 0.72, "Observation describes an uneven or broken surface.", "ai_observation_text");
        }

        if (ContainsAny(text, "smooth", "flat", "even pavement", "good condition"))
        {
            AddCandidate(candidates, "smoothness", "good", 0.64, "Observation describes a smooth/even surface.", "ai_observation_text");
        }

        if (ContainsAny(text, "steep", "slope", "incline", "gradient", "hill"))
        {
            AddCandidate(candidates, "incline", "measure_percent_grade", 0.70, "Observation mentions slope or gradient.", "ai_observation_text");
        }

        if (ContainsAny(text, "stairs", "steps", "stairway"))
        {
            AddCandidate(candidates, "wheelchair", "no", 0.82, "Observation mentions stairs or steps.", "ai_observation_text");
            AddCandidate(candidates, "step_free_access", "false", 0.82, "Steps imply this path or entrance is not step-free without an alternate ramp.", "ai_observation_text");
        }

        if (ContainsAny(text, "tactile", "truncated domes", "blister paving"))
        {
            AddCandidate(candidates, "tactile_paving", "true", 0.72, "Observation mentions tactile paving.", "ai_observation_text");
        }

        if (ContainsAny(text, "grab bar", "grab rail", "toilet rail", "accessible toilet", "wheelchair toilet"))
        {
            AddCandidate(candidates, "toilets:wheelchair", "true", 0.78, "Observation mentions accessible restroom fixtures.", "ai_observation_text");
            AddCandidate(candidates, "toilets:grab_bar", "true", 0.70, "Observation mentions grab bars or rails.", "ai_observation_text");
        }

        if (photoCount > 0)
        {
            AddCandidate(candidates, "photos", "field_photo_attached", 0.66, "Field photo URL was provided for human review.", "ai_observation_photo");
        }
    }

    private static string BuildAdminSummary(
        InfrastructureAccessibilityProfile profile,
        int candidateCount,
        int photoCount)
    {
        return $"Generated {candidateCount} accessibility candidate(s) from observation plus profile gaps; current profile {profile.VerificationStatus} at confidence {profile.Confidence:0.00}; {photoCount} photo(s) supplied.";
    }

    private static void AddCandidate(
        List<MissingOsmAttributeCandidate> candidates,
        string attribute,
        string value,
        double confidence,
        string evidence,
        string source)
    {
        candidates.Add(new MissingOsmAttributeCandidate
        {
            Attribute = attribute,
            Value = value,
            Confidence = Math.Round(confidence, 3),
            Evidence = evidence,
            Source = source,
            CanAutoApply = false
        });
    }

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string InferSurface(string text)
    {
        if (text.Contains("concrete", StringComparison.OrdinalIgnoreCase)) return "concrete";
        if (text.Contains("asphalt", StringComparison.OrdinalIgnoreCase)) return "asphalt";
        if (text.Contains("paving stones", StringComparison.OrdinalIgnoreCase) || text.Contains("paving_stones", StringComparison.OrdinalIgnoreCase)) return "paving_stones";
        if (text.Contains("gravel", StringComparison.OrdinalIgnoreCase)) return "gravel";
        if (text.Contains("cobblestone", StringComparison.OrdinalIgnoreCase)) return "cobblestone";
        if (text.Contains("mud", StringComparison.OrdinalIgnoreCase)) return "mud";
        if (text.Contains("grass", StringComparison.OrdinalIgnoreCase)) return "grass";
        if (text.Contains("dirt", StringComparison.OrdinalIgnoreCase)) return "dirt";
        return "verify_material";
    }

    internal static List<string> StandardGuardrails() =>
    [
        "AI output is advisory and cannot generate route geometry.",
        "AI output cannot change routing graph edge costs or safe-path decisions.",
        "Candidates require human/admin review through accessibility verification before profile updates.",
        "Every candidate must preserve evidence, confidence, provider, and model metadata."
    ];
}

public sealed class LocalVisionAccessibilityInferenceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AiEnrichmentOptions _options;

    public LocalVisionAccessibilityInferenceProvider(HttpClient httpClient, IOptions<AiEnrichmentOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.VisionModelTimeoutSeconds, 1, 30));
    }

    public bool IsConfigured => Uri.TryCreate(_options.VisionModelEndpoint, UriKind.Absolute, out _);

    public async Task<AccessibilityAiInferenceResult> InferAsync(
        long assetId,
        InfrastructureAccessibilityProfile profile,
        AccessibilityAiInferenceRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Local vision model endpoint is not configured.");
        }

        if (request.Photos.Count == 0)
        {
            throw new InvalidOperationException("Local vision inference requires at least one photo.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ResolveAnalyzeEndpoint());
        httpRequest.Content = JsonContent.Create(BuildRequestPayload(request), options: JsonOptions);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parsed = await JsonSerializer.DeserializeAsync<LocalVisionAnalyzeResponse>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Local vision model response could not be parsed.");

        var candidates = parsed.Candidates
            .Select(candidate => AccessibilityCandidateNormalizer.ToCandidate(
                candidate.Attribute,
                candidate.Value,
                candidate.Confidence,
                candidate.Evidence,
                string.IsNullOrWhiteSpace(candidate.Source) ? "accesscity_local_vision" : candidate.Source,
                "Local accessibility vision model candidate."))
            .Where(candidate => candidate.Confidence >= _options.MinimumCandidateConfidence && candidate.Attribute != "unknown")
            .GroupBy(candidate => $"{candidate.Attribute}:{candidate.Value}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First())
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Attribute, StringComparer.Ordinal)
            .Take(12)
            .ToList();

        return new AccessibilityAiInferenceResult
        {
            InfrastructureAssetId = assetId,
            ForRouteDecision = false,
            Provider = "local-vision",
            Model = string.IsNullOrWhiteSpace(parsed.Model) ? "accesscity-accessibility-vision" : parsed.Model,
            GeneratedAtUtc = DateTime.UtcNow,
            AdminSummary = $"Local vision model generated {candidates.Count} review-only accessibility candidate(s) from {request.Photos.Count} photo(s).",
            AttributeCandidates = candidates,
            DraftVerification = request.IncludeDraftVerification
                ? AccessibilityCandidateDraftBuilder.BuildDraftVerification(candidates, request)
                : null,
            Guardrails = LocalAccessibilityAiInferenceProvider.StandardGuardrails(),
            Limitations =
            [
                "Local vision output is a candidate extraction result, not an authoritative accessibility measurement.",
                "Candidates require human/admin review before profile updates.",
                "This endpoint is outside routing hot paths and never influences route decisions directly."
            ]
        };
    }

    private object BuildRequestPayload(AccessibilityAiInferenceRequest request) => new
    {
        thresholdFloor = Math.Clamp(_options.VisionModelMinimumConfidence, 0.01, 0.99),
        photos = request.Photos.Select(photo => new
        {
            url = photo.Url,
            caption = photo.Caption,
            source = photo.Source
        })
    };

    private Uri ResolveAnalyzeEndpoint()
    {
        var endpoint = _options.VisionModelEndpoint.TrimEnd('/');
        if (endpoint.EndsWith("/analyze", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(endpoint, UriKind.Absolute);
        }

        return new Uri($"{endpoint}/v1/accessibility-vision/analyze", UriKind.Absolute);
    }

    private sealed class LocalVisionAnalyzeResponse
    {
        public string Model { get; set; } = string.Empty;
        public List<LocalVisionCandidate> Candidates { get; set; } = [];
    }

    private sealed class LocalVisionCandidate
    {
        public string Attribute { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Evidence { get; set; } = string.Empty;
        public string Source { get; set; } = "accesscity_local_vision";
    }
}

public sealed class OpenAiAccessibilityInferenceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AiEnrichmentOptions _options;

    public OpenAiAccessibilityInferenceProvider(HttpClient httpClient, IOptions<AiEnrichmentOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.OpenAiTimeoutSeconds, 1, 30));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    public async Task<AccessibilityAiInferenceResult> InferAsync(
        long assetId,
        InfrastructureAccessibilityProfile profile,
        AccessibilityAiInferenceRequest request,
        CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("OpenAI API key is not configured.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint());
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = JsonContent.Create(BuildRequestPayload(profile, request), options: JsonOptions);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var outputText = ExtractFirstOutputText(document.RootElement);
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException("OpenAI response did not contain structured output text.");
        }

        var parsed = JsonSerializer.Deserialize<OpenAiAccessibilityInferenceOutput>(outputText, JsonOptions)
            ?? throw new InvalidOperationException("OpenAI structured output could not be parsed.");

        var candidates = parsed.Candidates
            .Select(candidate => AccessibilityCandidateNormalizer.ToCandidate(
                candidate.Attribute,
                candidate.Value,
                candidate.Confidence,
                candidate.Evidence,
                "openai_multimodal",
                "OpenAI accessibility inference candidate."))
            .Where(candidate => candidate.Confidence >= _options.MinimumCandidateConfidence && candidate.Attribute != "unknown")
            .GroupBy(candidate => candidate.Attribute, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First())
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Attribute, StringComparer.Ordinal)
            .Take(12)
            .ToList();

        return new AccessibilityAiInferenceResult
        {
            InfrastructureAssetId = assetId,
            ForRouteDecision = false,
            Provider = "openai",
            Model = _options.OpenAiModel,
            GeneratedAtUtc = DateTime.UtcNow,
            AdminSummary = string.IsNullOrWhiteSpace(parsed.Summary)
                ? $"OpenAI generated {candidates.Count} accessibility candidate(s)."
                : parsed.Summary.Trim(),
            AttributeCandidates = candidates,
            DraftVerification = request.IncludeDraftVerification
                ? AccessibilityCandidateDraftBuilder.BuildDraftVerification(candidates, request)
                : null,
            Guardrails = LocalAccessibilityAiInferenceProvider.StandardGuardrails(),
            Limitations =
            [
                "OpenAI output is a candidate extraction result, not an authoritative accessibility measurement.",
                "Image-derived conclusions require human/admin review before profile updates.",
                "This endpoint is outside routing hot paths and never influences route decisions directly."
            ]
        };
    }

    private object BuildRequestPayload(
        InfrastructureAccessibilityProfile profile,
        AccessibilityAiInferenceRequest request)
    {
        var content = new List<object>
        {
            new
            {
                type = "input_text",
                text = BuildPrompt(profile, request)
            }
        };

        foreach (var photo in request.Photos)
        {
            content.Add(new
            {
                type = "input_image",
                image_url = photo.Url,
                detail = "low"
            });
        }

        return new
        {
            model = string.IsNullOrWhiteSpace(_options.OpenAiModel) ? "gpt-5-mini" : _options.OpenAiModel,
            instructions = "You extract accessibility data-quality candidates for a pedestrian accessibility app. Return only JSON matching the schema. Do not recommend routes, alter route costs, or make final accessibility decisions.",
            input = new[]
            {
                new
                {
                    role = "user",
                    content
                }
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "accessibility_candidates",
                    strict = true,
                    schema = AccessibilityCandidatesSchema()
                }
            },
            max_output_tokens = Math.Clamp(_options.OpenAiMaxOutputTokens, 128, 4_000)
        };
    }

    private static string BuildPrompt(
        InfrastructureAccessibilityProfile profile,
        AccessibilityAiInferenceRequest request)
    {
        var profileJson = JsonSerializer.Serialize(new
        {
            profile.VerificationStatus,
            profile.Confidence,
            profile.MissingFields,
            profile.Path,
            entrance = profile.Entrances.FirstOrDefault(),
            restroom = profile.Restrooms.FirstOrDefault(),
            photoCount = profile.Photos.Count
        }, JsonOptions);

        var photoCaptions = request.Photos.Select(photo => new
        {
            photo.Source,
            photo.Caption,
            photo.TakenAtUtc
        });

        var observation = string.IsNullOrWhiteSpace(request.ObservationText)
            ? "No text observation supplied."
            : request.ObservationText;

        return $"""
        JSON task: infer candidate accessibility attributes from a field observation and optional images.

        Current profile:
        {profileJson}

        Field observation text:
        {observation}

        Photo metadata:
        {JsonSerializer.Serialize(photoCaptions, JsonOptions)}

        Return only candidates that should be reviewed by a human. Use attributes from this controlled set when possible:
        surface, smoothness, width_metres, kerb_height_metres, curb_ramp, incline, tactile_paving, wheelchair, step_free_access,
        door_width_metres, automatic_door, toilets:wheelchair, toilets:grab_bar, changing_table, photos, last_verified_at.

        Candidate values should be short normalized values such as concrete, asphalt, good, bad, true, false, measure_clear_width, verify_material, >0.06.
        Never mark candidates as route decisions. Never claim certainty from an image unless the evidence is clear; lower confidence is acceptable.
        """;
    }

    private static object AccessibilityCandidatesSchema() => new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "summary", "candidates" },
        properties = new
        {
            summary = new
            {
                type = "string"
            },
            candidates = new
            {
                type = "array",
                maxItems = 12,
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "attribute", "value", "confidence", "evidence" },
                    properties = new
                    {
                        attribute = new { type = "string" },
                        value = new { type = "string" },
                        confidence = new { type = "number" },
                        evidence = new { type = "string" }
                    }
                }
            }
        }
    };

    private Uri ResolveEndpoint()
    {
        var endpoint = string.IsNullOrWhiteSpace(_options.OpenAiEndpoint)
            ? "https://api.openai.com/v1/responses"
            : _options.OpenAiEndpoint;
        return new Uri(endpoint, UriKind.Absolute);
    }

    private string? ResolveApiKey() =>
        !string.IsNullOrWhiteSpace(_options.OpenAiApiKey)
            ? _options.OpenAiApiKey
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    private static string? ExtractFirstOutputText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("type", out var type) &&
            string.Equals(type.GetString(), "output_text", StringComparison.OrdinalIgnoreCase) &&
            element.TryGetProperty("text", out var text))
        {
            return text.GetString();
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var nested = ExtractFirstOutputText(property.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = ExtractFirstOutputText(item);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private sealed class OpenAiAccessibilityInferenceOutput
    {
        public string Summary { get; set; } = string.Empty;
        public List<OpenAiAccessibilityCandidate> Candidates { get; set; } = [];
    }

    private sealed class OpenAiAccessibilityCandidate
    {
        public string Attribute { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Evidence { get; set; } = string.Empty;
    }
}

public sealed class NebiusAccessibilityInferenceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AiEnrichmentOptions _options;

    public NebiusAccessibilityInferenceProvider(HttpClient httpClient, IOptions<AiEnrichmentOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.NebiusTimeoutSeconds, 1, 30));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    public async Task<AccessibilityAiInferenceResult> InferAsync(
        long assetId,
        InfrastructureAccessibilityProfile profile,
        AccessibilityAiInferenceRequest request,
        CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Nebius API key is not configured.");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ResolveChatCompletionsEndpoint());
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = JsonContent.Create(BuildRequestPayload(profile, request), options: JsonOptions);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var content = ExtractChatMessageContent(document.RootElement);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Nebius response did not contain chat message content.");
        }

        var parsed = ParseCandidateOutput(content)
            ?? throw new InvalidOperationException("Nebius candidate JSON could not be parsed.");
        var source = _options.NebiusEnableImageInputs ? "nebius_multimodal" : "nebius_text";
        var candidates = parsed.Candidates
            .Select(candidate => AccessibilityCandidateNormalizer.ToCandidate(
                candidate.Attribute,
                candidate.Value,
                candidate.Confidence,
                candidate.Evidence,
                source,
                "Nebius accessibility inference candidate."))
            .Where(candidate => candidate.Confidence >= _options.MinimumCandidateConfidence && candidate.Attribute != "unknown")
            .GroupBy(candidate => candidate.Attribute, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Confidence).First())
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Attribute, StringComparer.Ordinal)
            .Take(12)
            .ToList();

        return new AccessibilityAiInferenceResult
        {
            InfrastructureAssetId = assetId,
            ForRouteDecision = false,
            Provider = "nebius",
            Model = ResolveModel(),
            GeneratedAtUtc = DateTime.UtcNow,
            AdminSummary = string.IsNullOrWhiteSpace(parsed.Summary)
                ? $"Nebius generated {candidates.Count} accessibility candidate(s)."
                : parsed.Summary.Trim(),
            AttributeCandidates = candidates,
            DraftVerification = request.IncludeDraftVerification
                ? AccessibilityCandidateDraftBuilder.BuildDraftVerification(candidates, request)
                : null,
            Guardrails = LocalAccessibilityAiInferenceProvider.StandardGuardrails(),
            Limitations =
            [
                "Nebius output is a candidate extraction result, not an authoritative accessibility measurement.",
                "Model output is normalized into controlled AccessCity fields and still requires human/admin review.",
                _options.NebiusEnableImageInputs
                    ? "Image inputs require a vision-capable Nebius model and still require human/admin review."
                    : "Default Nebius mode uses observation text and photo metadata; enable image inputs only with a vision-capable Nebius model.",
                "This endpoint is outside routing hot paths and never influences route decisions directly."
            ]
        };
    }

    private object BuildRequestPayload(
        InfrastructureAccessibilityProfile profile,
        AccessibilityAiInferenceRequest request)
    {
        var content = new List<object>
        {
            new
            {
                type = "text",
                text = BuildPrompt(profile, request)
            }
        };

        if (_options.NebiusEnableImageInputs)
        {
            foreach (var photo in request.Photos)
            {
                content.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = photo.Url,
                        detail = "low"
                    }
                });
            }
        }

        return new
        {
            model = ResolveModel(),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You extract accessibility data-quality candidates for a pedestrian accessibility app. Return strict JSON only. Do not recommend routes, alter route costs, or make final accessibility decisions."
                },
                new
                {
                    role = "user",
                    content
                }
            },
            temperature = 0,
            max_tokens = Math.Clamp(_options.NebiusMaxTokens, 128, 4_000),
            response_format = new
            {
                type = "json_object"
            }
        };
    }

    private static string BuildPrompt(
        InfrastructureAccessibilityProfile profile,
        AccessibilityAiInferenceRequest request)
    {
        var profileJson = JsonSerializer.Serialize(new
        {
            profile.VerificationStatus,
            profile.Confidence,
            profile.MissingFields,
            profile.Path,
            entrance = profile.Entrances.FirstOrDefault(),
            restroom = profile.Restrooms.FirstOrDefault(),
            photoCount = profile.Photos.Count
        }, JsonOptions);

        var observation = string.IsNullOrWhiteSpace(request.ObservationText)
            ? "No text observation supplied."
            : request.ObservationText;
        var photoMetadata = request.Photos.Select(photo => new
        {
            photo.Source,
            photo.Caption,
            photo.TakenAtUtc,
            photo.Url
        });
        const string outputShape = "{\"summary\":\"string\",\"candidates\":[{\"attribute\":\"string\",\"value\":\"string\",\"confidence\":0.0,\"evidence\":\"string\"}]}";

        return $"""
        Return JSON only with this exact shape:
        {outputShape}

        Controlled attributes:
        surface, smoothness, width_metres, kerb_height_metres, curb_ramp, incline, tactile_paving, wheelchair, step_free_access,
        door_width_metres, automatic_door, toilets:wheelchair, toilets:grab_bar, changing_table, photos, last_verified_at.

        Normalize values to short strings: concrete, asphalt, paving_stones, gravel, cobblestone, good, bad, true, false,
        measure_clear_width, verify_material, >0.06.

        Current profile JSON:
        {profileJson}

        Field observation:
        {observation}

        Photo metadata and URLs:
        {JsonSerializer.Serialize(photoMetadata, JsonOptions)}

        Rules:
        - Produce review candidates only.
        - Do not output markdown.
        - If images are not provided to the model, use photo metadata only as weak evidence.
        - Do not include route geometry, route recommendations, or edge-cost changes.
        - If uncertain, lower confidence instead of inventing.
        """;
    }

    private Uri ResolveChatCompletionsEndpoint()
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.NebiusBaseUrl)
            ? "https://api.tokenfactory.nebius.com/v1"
            : _options.NebiusBaseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/chat/completions", UriKind.Absolute);
    }

    private string ResolveModel() =>
        string.IsNullOrWhiteSpace(_options.NebiusModel)
            ? "openai/gpt-oss-120b-fast"
            : _options.NebiusModel;

    private string? ResolveApiKey() =>
        !string.IsNullOrWhiteSpace(_options.NebiusApiKey)
            ? _options.NebiusApiKey
            : Environment.GetEnvironmentVariable("NEBIUS_API_KEY");

    private static string? ExtractChatMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return null;
        }

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join(
                string.Empty,
                content.EnumerateArray()
                    .Select(item => item.TryGetProperty("text", out var text) ? text.GetString() : null)
                    .Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => null
        };
    }

    private static ProviderCandidateOutput? ParseCandidateOutput(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith('{'))
        {
            var start = trimmed.IndexOf('{', StringComparison.Ordinal);
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                trimmed = trimmed[start..(end + 1)];
            }
        }

        return JsonSerializer.Deserialize<ProviderCandidateOutput>(trimmed, JsonOptions);
    }

    private sealed class ProviderCandidateOutput
    {
        public string Summary { get; set; } = string.Empty;
        public List<ProviderCandidate> Candidates { get; set; } = [];
    }

    private sealed class ProviderCandidate
    {
        public string Attribute { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Evidence { get; set; } = string.Empty;
    }
}

internal static class AccessibilityCandidateNormalizer
{
    private static readonly HashSet<string> BooleanAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "curb_ramp",
        "tactile_paving",
        "step_free_access",
        "automatic_door",
        "toilets:wheelchair",
        "toilets:grab_bar",
        "changing_table",
        "obstacle",
        "crosswalk"
    };

    public static MissingOsmAttributeCandidate ToCandidate(
        string attribute,
        string value,
        double confidence,
        string evidence,
        string source,
        string defaultEvidence)
    {
        var normalizedAttribute = NormalizeAttribute(attribute, value);
        var boundedConfidence = double.IsFinite(confidence)
            ? Math.Round(Math.Clamp(confidence, 0, 0.95), 3)
            : 0;

        return new MissingOsmAttributeCandidate
        {
            Attribute = normalizedAttribute,
            Value = NormalizeValue(normalizedAttribute, value),
            Confidence = boundedConfidence,
            Evidence = string.IsNullOrWhiteSpace(evidence) ? defaultEvidence : evidence.Trim(),
            Source = source,
            CanAutoApply = false
        };
    }

    private static string NormalizeAttribute(string attribute, string value)
    {
        var normalized = NormalizeToken(attribute);
        var joined = $"{normalized} {NormalizeToken(value)}";

        if (ContainsAny(joined, "toilet", "toilets", "restroom", "bathroom"))
        {
            if (ContainsAny(joined, "grab", "rail", "bar"))
            {
                return "toilets:grab_bar";
            }

            if (ContainsAny(joined, "wheelchair", "accessible"))
            {
                return "toilets:wheelchair";
            }
        }

        if (ContainsAny(joined, "curb_ramp", "kerb_ramp", "curb_cut", "dropped_kerb", "lowered_kerb") ||
            normalized == "ramp")
        {
            return "curb_ramp";
        }

        if (ContainsAny(joined, "kerb", "curb", "raised_kerb", "raised_curb", "high_kerb", "high_curb"))
        {
            return "kerb_height_metres";
        }

        if (ContainsAny(joined, "door_width"))
        {
            return "door_width_metres";
        }

        if (ContainsAny(joined, "width", "clear_width", "crossing_width", "narrow"))
        {
            return "width_metres";
        }

        if (ContainsAny(joined, "surface", "material", "concrete", "asphalt", "gravel", "cobblestone", "paving", "mud", "grass", "dirt"))
        {
            return "surface";
        }

        if (ContainsAny(joined, "smoothness", "condition", "uneven", "broken", "cracked", "bumpy", "pothole", "loose_surface"))
        {
            return "smoothness";
        }

        if (ContainsAny(joined, "incline", "slope", "grade", "gradient", "steep"))
        {
            return "incline";
        }

        if (ContainsAny(joined, "tactile", "truncated_domes", "blister_paving"))
        {
            return "tactile_paving";
        }

        if (ContainsAny(joined, "automatic_door", "powered_door"))
        {
            return "automatic_door";
        }

        if (ContainsAny(joined, "changing_table", "baby_change"))
        {
            return "changing_table";
        }

        if (ContainsAny(joined, "obstacle", "blocked", "blocking", "barrier"))
        {
            return "obstacle";
        }

        if (ContainsAny(joined, "crosswalk", "crossing", "zebra"))
        {
            return "crosswalk";
        }

        if (ContainsAny(joined, "step_free", "stairs", "steps", "stairway"))
        {
            return "step_free_access";
        }

        if (ContainsAny(joined, "wheelchair"))
        {
            return "wheelchair";
        }

        return normalized switch
        {
            "kerb_height" or "curb_height" => "kerb_height_metres",
            "door_width" => "door_width_metres",
            "toilets_grab_bar" => "toilets:grab_bar",
            "toilets_wheelchair" => "toilets:wheelchair",
            "" => "unknown",
            _ => normalized
        };
    }

    private static string NormalizeValue(string attribute, string value)
    {
        var normalized = NormalizeToken(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "verify";
        }

        if (BooleanAttributes.Contains(attribute))
        {
            if (ContainsAny(normalized, "absent", "no", "none", "false", "missing", "blocked", "not_present", "stairs", "steps"))
            {
                return "false";
            }

            if (ContainsAny(normalized, "present", "yes", "true", "available", "lowered", "flush", "dropped"))
            {
                return "true";
            }
        }

        return attribute switch
        {
            "wheelchair" when ContainsAny(normalized, "no", "false", "stairs", "steps", "blocked") => "no",
            "wheelchair" when ContainsAny(normalized, "yes", "true", "accessible") => "yes",
            "kerb_height_metres" when ContainsAny(normalized, "raised", "high", "present", "tall") => ">0.06",
            "kerb_height_metres" when ContainsAny(normalized, "flush", "lowered", "dropped", "zero") => "0",
            "width_metres" or "door_width_metres" when ContainsAny(normalized, "narrow", "tight", "measure", "unknown") => "measure_clear_width",
            "surface" => NormalizeSurfaceValue(normalized),
            "smoothness" when ContainsAny(normalized, "uneven", "broken", "cracked", "bumpy", "pothole", "loose", "bad", "poor") => "bad",
            "smoothness" when ContainsAny(normalized, "smooth", "flat", "even", "good") => "good",
            _ => normalized
        };
    }

    private static string NormalizeSurfaceValue(string normalized)
    {
        if (normalized.Contains("concrete", StringComparison.Ordinal)) return "concrete";
        if (normalized.Contains("asphalt", StringComparison.Ordinal)) return "asphalt";
        if (normalized.Contains("gravel", StringComparison.Ordinal)) return "gravel";
        if (normalized.Contains("cobblestone", StringComparison.Ordinal)) return "cobblestone";
        if (normalized.Contains("paving", StringComparison.Ordinal)) return "paving_stones";
        if (normalized.Contains("mud", StringComparison.Ordinal)) return "mud";
        if (normalized.Contains("grass", StringComparison.Ordinal)) return "grass";
        if (normalized.Contains("dirt", StringComparison.Ordinal)) return "dirt";
        return normalized;
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousSeparator = false;
        foreach (var character in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character == ':')
            {
                builder.Append(character);
                previousSeparator = false;
            }
            else if (!previousSeparator)
            {
                builder.Append('_');
                previousSeparator = true;
            }
        }

        return builder.ToString().Trim('_');
    }

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(term => text.Contains(term, StringComparison.Ordinal));
}

internal static class AccessibilityCandidateDraftBuilder
{
    public static AccessibilityVerificationRequest BuildDraftVerification(
        IReadOnlyCollection<MissingOsmAttributeCandidate> candidates,
        AccessibilityAiInferenceRequest sourceRequest)
    {
        var path = new AccessibilityPathAttributes();
        var entrance = new AccessibilityEntrance();
        var restroom = new AccessibilityRestroom();
        var hasPath = false;
        var hasEntrance = false;
        var hasRestroom = false;

        foreach (var candidate in candidates)
        {
            var value = candidate.Value;
            switch (candidate.Attribute.ToLowerInvariant())
            {
                case "surface":
                    path = CopyPath(path, surface: NormalizeCandidateValue(value));
                    hasPath = true;
                    break;
                case "smoothness":
                    path = CopyPath(path, smoothness: NormalizeCandidateValue(value));
                    hasPath = true;
                    break;
                case "width_metres":
                    path = CopyPath(path, widthMetres: ParseMetres(value));
                    hasPath = true;
                    break;
                case "kerb_height_metres":
                    path = CopyPath(path, kerbHeightMetres: ParseMetres(value));
                    hasPath = true;
                    break;
                case "curb_ramp":
                    path = CopyPath(path, hasCurbRamp: ParseBool(value));
                    hasPath = true;
                    break;
                case "incline":
                    path = CopyPath(path, inclinePercent: ParsePercent(value), inclineText: NormalizeCandidateValue(value));
                    hasPath = true;
                    break;
                case "tactile_paving":
                    path = CopyPath(path, hasTactilePaving: ParseBool(value));
                    hasPath = true;
                    break;
                case "wheelchair":
                    path = CopyPath(path, wheelchairAccess: NormalizeCandidateValue(value));
                    hasPath = true;
                    break;
                case "step_free_access":
                    path = CopyPath(path, hasStepFreeAccess: ParseBool(value));
                    entrance = CopyEntrance(entrance, stepFree: ParseBool(value));
                    hasPath = true;
                    hasEntrance = true;
                    break;
                case "door_width_metres":
                    entrance = CopyEntrance(entrance, doorWidthMetres: ParseMetres(value));
                    hasEntrance = true;
                    break;
                case "automatic_door":
                    entrance = CopyEntrance(entrance, automaticDoor: ParseBool(value));
                    hasEntrance = true;
                    break;
                case "toilets:wheelchair":
                    restroom = CopyRestroom(restroom, wheelchairAccessible: ParseBool(value));
                    hasRestroom = true;
                    break;
                case "toilets:grab_bar":
                    restroom = CopyRestroom(restroom, hasGrabBars: ParseBool(value));
                    hasRestroom = true;
                    break;
                case "changing_table":
                    restroom = CopyRestroom(restroom, hasChangingTable: ParseBool(value));
                    hasRestroom = true;
                    break;
            }
        }

        return new AccessibilityVerificationRequest
        {
            ObservedAtUtc = DateTime.UtcNow,
            Source = "ai_candidate_review",
            Notes = BuildDraftNotes(candidates),
            Path = hasPath ? path : null,
            Entrance = hasEntrance ? entrance : null,
            Restroom = hasRestroom ? restroom : null,
            Photos = sourceRequest.Photos
        };
    }

    private static string BuildDraftNotes(IReadOnlyCollection<MissingOsmAttributeCandidate> candidates)
    {
        var parts = candidates
            .Take(6)
            .Select(candidate => $"{candidate.Attribute}={candidate.Value} ({candidate.Confidence.ToString("0.00", CultureInfo.InvariantCulture)})");
        return "AI-generated review draft; human verification required. Candidates: " + string.Join("; ", parts);
    }

    private static AccessibilityPathAttributes CopyPath(
        AccessibilityPathAttributes path,
        string? surface = null,
        string? smoothness = null,
        double? widthMetres = null,
        double? kerbHeightMetres = null,
        double? inclinePercent = null,
        string? inclineText = null,
        bool? hasTactilePaving = null,
        bool? hasCurbRamp = null,
        bool? hasStepFreeAccess = null,
        string? wheelchairAccess = null)
    {
        return new AccessibilityPathAttributes
        {
            Surface = surface ?? path.Surface,
            Smoothness = smoothness ?? path.Smoothness,
            WidthMetres = widthMetres ?? path.WidthMetres,
            KerbHeightMetres = kerbHeightMetres ?? path.KerbHeightMetres,
            InclinePercent = inclinePercent ?? path.InclinePercent,
            InclineText = inclineText ?? path.InclineText,
            HasTactilePaving = hasTactilePaving ?? path.HasTactilePaving,
            HasCurbRamp = hasCurbRamp ?? path.HasCurbRamp,
            HasStepFreeAccess = hasStepFreeAccess ?? path.HasStepFreeAccess,
            HasStairs = path.HasStairs,
            HasBarrier = path.HasBarrier,
            WheelchairAccess = wheelchairAccess ?? path.WheelchairAccess,
            Lighting = path.Lighting,
            CrossingType = path.CrossingType,
            Access = path.Access
        };
    }

    private static AccessibilityEntrance CopyEntrance(
        AccessibilityEntrance entrance,
        bool? stepFree = null,
        double? doorWidthMetres = null,
        bool? automaticDoor = null)
    {
        return new AccessibilityEntrance
        {
            Name = entrance.Name,
            EntranceType = entrance.EntranceType,
            StepFree = stepFree ?? entrance.StepFree,
            HasRamp = entrance.HasRamp,
            DoorWidthMetres = doorWidthMetres ?? entrance.DoorWidthMetres,
            AutomaticDoor = automaticDoor ?? entrance.AutomaticDoor,
            StepHeightMetres = entrance.StepHeightMetres
        };
    }

    private static AccessibilityRestroom CopyRestroom(
        AccessibilityRestroom restroom,
        bool? wheelchairAccessible = null,
        bool? hasGrabBars = null,
        bool? hasChangingTable = null)
    {
        return new AccessibilityRestroom
        {
            WheelchairAccessible = wheelchairAccessible ?? restroom.WheelchairAccessible,
            HasGrabBars = hasGrabBars ?? restroom.HasGrabBars,
            DoorWidthMetres = restroom.DoorWidthMetres,
            TurningSpaceMetres = restroom.TurningSpaceMetres,
            HasChangingTable = hasChangingTable ?? restroom.HasChangingTable,
            RequiresKey = restroom.RequiresKey,
            GenderAccess = restroom.GenderAccess
        };
    }

    private static string? NormalizeCandidateValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains("verify", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("measure", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant().Replace(' ', '_');
    }

    private static bool? ParseBool(string value)
    {
        var normalized = NormalizeCandidateValue(value);
        return normalized switch
        {
            "true" or "yes" or "present" or "field_photo_attached" => true,
            "false" or "no" or "absent" => false,
            _ => null
        };
    }

    private static double? ParsePercent(string value)
    {
        var normalized = value.Trim().Replace("%", "", StringComparison.Ordinal);
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ParseMetres(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains("measure", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("verify", StringComparison.OrdinalIgnoreCase) ||
            value.Contains('>', StringComparison.Ordinal) ||
            value.Contains('<', StringComparison.Ordinal))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant()
            .Replace("metres", "", StringComparison.Ordinal)
            .Replace("meters", "", StringComparison.Ordinal)
            .Replace("metre", "", StringComparison.Ordinal)
            .Replace("meter", "", StringComparison.Ordinal)
            .Replace("m", "", StringComparison.Ordinal)
            .Trim();

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
