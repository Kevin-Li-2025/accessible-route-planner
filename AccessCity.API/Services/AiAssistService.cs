using System.Text.RegularExpressions;
using AccessCity.API.Configuration;
using AccessCity.API.Models;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Services;

public interface IAiAssistService
{
    Task<HazardAiEnrichmentResult> EnrichHazardAsync(
        HazardReport hazard,
        IReadOnlyCollection<HazardReport> nearbyHazards,
        CancellationToken cancellationToken);

    Task<RouteExplanationResponse> ExplainRouteAsync(
        RouteExplanationRequest request,
        CancellationToken cancellationToken);

    Task<AccessibilityAiReviewResult> ReviewAccessibilityProfileAsync(
        long infrastructureAssetId,
        InfrastructureAccessibilityProfile profile,
        CancellationToken cancellationToken);
}

public sealed partial class AiAssistService : IAiAssistService
{
    private const double EarthRadiusMetres = 6_371_000;
    private readonly AiEnrichmentOptions _options;

    private static readonly HazardRule[] HazardRules =
    [
        new("missing_curb_ramp", "accessibility", 0.86,
        [
            "curb ramp", "kerb ramp", "curb cut", "dropped kerb", "missing ramp", "no ramp", "raised kerb", "high kerb"
        ]),
        new("stairs", "accessibility", 0.86, ["stairs", "steps", "stairway"]),
        new("missing_sidewalk", "network_gap", 0.84, ["no sidewalk", "missing sidewalk", "no pavement", "missing pavement"]),
        new("obstruction", "temporary_obstacle", 0.78,
        [
            "blocked", "obstruction", "barrier", "scaffold", "skip", "bin", "trash", "parked car", "parked van", "construction"
        ]),
        new("surface_quality", "surface", 0.74,
        [
            "uneven", "gravel", "cobblestone", "cracked", "broken pavement", "pothole", "loose surface", "mud"
        ]),
        new("steep_gradient", "grade", 0.72, ["steep", "slope", "incline", "gradient", "hill"]),
        new("low_lighting", "visibility", 0.70, ["dark", "poor lighting", "no lights", "streetlight out", "unlit"]),
        new("flooding", "temporary_obstacle", 0.72, ["flood", "flooding", "standing water", "puddle"])
    ];

    public AiAssistService(IOptions<AiEnrichmentOptions> options)
    {
        _options = options.Value;
        if (_options.AllowRouteDecisionInfluence)
        {
            throw new InvalidOperationException("AiEnrichment:AllowRouteDecisionInfluence must remain false.");
        }
    }

    public Task<HazardAiEnrichmentResult> EnrichHazardAsync(
        HazardReport hazard,
        IReadOnlyCollection<HazardReport> nearbyHazards,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = NormalizeDescription(hazard.Description);
        var searchText = $"{hazard.Type} {normalized}".ToLowerInvariant();
        var matchedRules = HazardRules
            .Where(rule => rule.Keywords.Any(keyword => searchText.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var topRule = matchedRules.FirstOrDefault();
        var suggestedType = topRule?.SuggestedType ?? NormalizeToken(hazard.Type);
        var confidence = topRule is null
            ? 0.45
            : Math.Min(0.95, topRule.Confidence + (matchedRules.Count - 1) * 0.03);
        var severity = SuggestSeverity(suggestedType, searchText);
        var duplicates = FindDuplicates(hazard, nearbyHazards, suggestedType);
        var candidates = BuildOsmCandidates(searchText, hazard.PhotoUrl);
        var tags = matchedRules
            .Select(rule => rule.Tag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(hazard.PhotoUrl))
        {
            tags.Add("photo-attached");
        }

        var result = new HazardAiEnrichmentResult
        {
            HazardId = hazard.Id,
            ForRouteDecision = false,
            Provider = _options.Provider,
            GeneratedAtUtc = DateTime.UtcNow,
            Text = new HazardTextEnrichment
            {
                NormalizedDescription = normalized,
                SuggestedType = suggestedType,
                SuggestedSeverity = severity,
                Confidence = Math.Round(confidence, 3),
                Tags = tags,
                AdminSummary = BuildAdminSummary(hazard, suggestedType, severity, duplicates.Count, candidates.Count)
            },
            DuplicateSuggestions = duplicates,
            MissingOsmAttributeCandidates = candidates,
            Guardrails =
            [
                "Text enrichment only; routing remains deterministic and auditable.",
                "OSM attribute candidates require review and are never auto-applied.",
                "This service must not generate route geometry or change graph edge costs."
            ]
        };

        return Task.FromResult(result);
    }

    public Task<RouteExplanationResponse> ExplainRouteAsync(
        RouteExplanationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var routeRequest = request.RouteRequest;
        var route = request.Route;
        var preferences = routeRequest.Preferences
            .Where(preference => !string.IsNullOrWhiteSpace(preference))
            .Select(preference => preference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var reasons = new List<string>
        {
            $"The deterministic router selected a {route.Distance:0} metre route estimated at {route.EstimatedTime:0} seconds."
        };

        if (routeRequest.SafetyWeight >= 0.7)
        {
            reasons.Add("High safety weight means hazard exposure and accessibility penalties were prioritized over raw distance.");
        }
        else if (routeRequest.SafetyWeight <= 0.3)
        {
            reasons.Add("Low safety weight means distance and travel time were prioritized unless a segment violated hard accessibility filters.");
        }
        else
        {
            reasons.Add("Balanced safety weight trades off travel time, hazard exposure, and accessibility penalties.");
        }

        if (!string.Equals(routeRequest.Profile, "standard", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"The {routeRequest.Profile} profile applies reproducible accessibility constraints before scoring candidate edges.");
        }

        if (preferences.Count > 0)
        {
            reasons.Add($"Requested preferences considered by the router: {string.Join(", ", preferences)}.");
        }

        if (route.Warnings.Count > 0)
        {
            var warnings = route.Warnings.Take(Math.Max(1, _options.MaxExplanationWarnings));
            reasons.Add($"Remaining route warnings: {string.Join("; ", warnings)}.");
        }

        reasons.Add($"The resulting safety score is {route.SafetyScore:0.00} on the service scale.");

        var limitations = new List<string>
        {
            "This endpoint formats an explanation from route fields; it does not create routes.",
            "It does not adjust OSM graph attributes, risk weights, or edge costs.",
            "Missing or stale OSM accessibility tags can still limit route accuracy."
        };

        return Task.FromResult(new RouteExplanationResponse
        {
            ForRouteDecision = false,
            Provider = _options.Provider,
            GeneratedAtUtc = DateTime.UtcNow,
            Reasons = reasons,
            Limitations = limitations,
            Explanation = string.Join(" ", reasons)
        });
    }

    public Task<AccessibilityAiReviewResult> ReviewAccessibilityProfileAsync(
        long infrastructureAssetId,
        InfrastructureAccessibilityProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = BuildAccessibilityCandidates(profile);
        var checklist = BuildAccessibilityChecklist(profile, candidates);

        return Task.FromResult(new AccessibilityAiReviewResult
        {
            InfrastructureAssetId = infrastructureAssetId,
            ForRouteDecision = false,
            Provider = _options.Provider,
            GeneratedAtUtc = DateTime.UtcNow,
            AdminSummary = BuildAccessibilityAdminSummary(profile, candidates.Count, checklist.Count),
            MissingAttributeCandidates = candidates,
            VerificationChecklist = checklist,
            Guardrails =
            [
                "Accessibility review is advisory and cannot generate routes.",
                "Suggested attributes require human verification before profile updates.",
                "Applying a verification updates facility metadata only; routing edge costs remain deterministic."
            ]
        });
    }

    private List<DuplicateHazardSuggestion> FindDuplicates(
        HazardReport hazard,
        IReadOnlyCollection<HazardReport> nearbyHazards,
        string suggestedType)
    {
        var radius = Math.Max(1, _options.DuplicateRadiusMetres);
        return nearbyHazards
            .Where(candidate => candidate.Id != hazard.Id)
            .Select(candidate => new
            {
                Hazard = candidate,
                Distance = DistanceMetres(hazard.Location.Y, hazard.Location.X, candidate.Location.Y, candidate.Location.X)
            })
            .Where(candidate => candidate.Distance <= radius)
            .Select(candidate =>
            {
                var sameType = string.Equals(
                    NormalizeToken(candidate.Hazard.Type),
                    suggestedType,
                    StringComparison.OrdinalIgnoreCase);
                var confidence = sameType ? 0.9 : 0.65;
                confidence *= 1 - candidate.Distance / (radius * 1.5);
                return new DuplicateHazardSuggestion
                {
                    HazardId = candidate.Hazard.Id,
                    DistanceMetres = Math.Round(candidate.Distance, 1),
                    Confidence = Math.Round(Math.Clamp(confidence, 0.35, 0.95), 3),
                    Reason = sameType
                        ? "Nearby report has the same normalized hazard type."
                        : "Nearby report is inside the duplicate review radius."
                };
            })
            .Where(candidate => candidate.Confidence >= _options.MinimumCandidateConfidence)
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.DistanceMetres)
            .Take(5)
            .ToList();
    }

    private List<MissingOsmAttributeCandidate> BuildOsmCandidates(string searchText, string? photoUrl)
    {
        var candidates = new List<MissingOsmAttributeCandidate>();
        var source = string.IsNullOrWhiteSpace(photoUrl) ? "user_report_text" : "user_report_text_photo";

        if (ContainsAny(searchText, "curb ramp", "kerb ramp", "curb cut", "dropped kerb", "missing ramp", "no ramp", "raised kerb", "high kerb"))
        {
            AddCandidate(candidates, "curb_ramp", "missing_or_blocked", 0.78, "Report mentions curb or kerb ramp accessibility.", source);
            AddCandidate(candidates, "kerb_height_metres", ">0.06", 0.62, "Report suggests a high or non-flush kerb.", source);
        }

        if (ContainsAny(searchText, "narrow", "too tight", "not enough width", "wheelchair cannot pass"))
        {
            AddCandidate(candidates, "width_metres", "<0.9", 0.70, "Report suggests sidewalk clear width is below wheelchair comfort threshold.", source);
        }

        if (ContainsAny(searchText, "gravel", "cobblestone", "mud", "loose surface"))
        {
            AddCandidate(candidates, "surface", InferSurface(searchText), 0.72, "Report names a surface material that may be missing from OSM.", source);
        }

        if (ContainsAny(searchText, "uneven", "cracked", "broken pavement", "pothole"))
        {
            AddCandidate(candidates, "smoothness", "bad", 0.68, "Report describes uneven or broken pedestrian surface.", source);
        }

        if (ContainsAny(searchText, "stairs", "steps", "stairway"))
        {
            AddCandidate(candidates, "obstacle", "stairs", 0.82, "Report indicates steps on the pedestrian path.", source);
            AddCandidate(candidates, "wheelchair", "no", 0.76, "Steps generally make this segment non-wheelchair-accessible unless an alternate ramp exists.", source);
        }

        if (ContainsAny(searchText, "no sidewalk", "missing sidewalk", "no pavement", "missing pavement"))
        {
            AddCandidate(candidates, "sidewalk_presence", "missing", 0.80, "Report indicates no continuous pedestrian path.", source);
        }

        if (ContainsAny(searchText, "steep", "slope", "incline", "gradient"))
        {
            AddCandidate(candidates, "incline", "steep", 0.66, "Report indicates grade may be difficult for mobility devices.", source);
        }

        if (ContainsAny(searchText, "dark", "poor lighting", "no lights", "streetlight out", "unlit"))
        {
            AddCandidate(candidates, "lit", "no", 0.64, "Report indicates missing or failed lighting.", source);
        }

        return candidates
            .Where(candidate => candidate.Confidence >= _options.MinimumCandidateConfidence)
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Attribute, StringComparer.Ordinal)
            .ToList();
    }

    private List<MissingOsmAttributeCandidate> BuildAccessibilityCandidates(InfrastructureAccessibilityProfile profile)
    {
        var candidates = new List<MissingOsmAttributeCandidate>();
        var missing = profile.MissingFields.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (missing.Contains("surface"))
        {
            AddCandidate(candidates, "surface", "verify_material", 0.74, "Surface is missing; route comfort and wheelchair usefulness depend on material.", "accessibility_profile_gap");
        }

        if (missing.Contains("smoothness"))
        {
            AddCandidate(candidates, "smoothness", "verify_surface_quality", 0.72, "Smoothness is missing; uneven pavement can be more important than route distance.", "accessibility_profile_gap");
        }

        if (missing.Contains("width_metres") || missing.Contains("door_width_metres") || missing.Contains("toilets_door_width_metres"))
        {
            AddCandidate(candidates, "width_metres", "measure_clear_width", 0.78, "Clear width is missing; wheelchair, stroller, and mobility-aid users need measured passable width.", "accessibility_profile_gap");
        }

        if (missing.Contains("kerb") || missing.Contains("curb_ramp"))
        {
            AddCandidate(candidates, "curb_ramp", "verify_flush_or_lowered", 0.80, "Curb ramp / kerb data is missing; crossings with raised kerbs can invalidate accessible routes.", "accessibility_profile_gap");
        }

        if (missing.Contains("incline_percent"))
        {
            AddCandidate(candidates, "incline", "measure_percent_grade", 0.68, "Incline is missing; slope changes travel feasibility for manual wheelchair users.", "accessibility_profile_gap");
        }

        if (missing.Contains("tactile_paving"))
        {
            AddCandidate(candidates, "tactile_paving", "verify_present_absent", 0.64, "Tactile paving is missing; blind and low-vision users need crossing cues.", "accessibility_profile_gap");
        }

        if (missing.Contains("toilets_wheelchair_access") || missing.Contains("toilets_grab_bars"))
        {
            AddCandidate(candidates, "toilets:wheelchair", "verify_accessible_toilet_features", 0.76, "Accessible restroom details are incomplete; verify wheelchair access, grab bars, and turning space.", "accessibility_profile_gap");
        }

        if (missing.Contains("last_verified_at"))
        {
            AddCandidate(candidates, "last_verified_at", "field_verify", 0.70, "Profile has no verification timestamp; recent field verification improves trust.", "accessibility_profile_gap");
        }

        if (profile.Photos.Count == 0)
        {
            AddCandidate(candidates, "photos", "add_field_photo", 0.62, "No field photo is attached; photos improve admin review and duplicate resolution.", "accessibility_profile_gap");
        }

        return candidates
            .Where(candidate => candidate.Confidence >= _options.MinimumCandidateConfidence)
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Attribute, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> BuildAccessibilityChecklist(
        InfrastructureAccessibilityProfile profile,
        IReadOnlyCollection<MissingOsmAttributeCandidate> candidates)
    {
        var checklist = new List<string>();
        foreach (var candidate in candidates.Take(8))
        {
            checklist.Add(candidate.Attribute switch
            {
                "surface" => "Record the sidewalk or facility surface material using controlled values such as asphalt, concrete, paving_stones, gravel, or cobblestone.",
                "smoothness" => "Record surface smoothness from excellent/good/intermediate/bad/very_bad based on wheel and mobility-aid comfort.",
                "width_metres" => "Measure the narrowest clear passable width in metres, not the nominal pavement width.",
                "curb_ramp" => "At crossings, verify whether kerbs are flush/lowered/rolled/raised and estimate kerb height if raised.",
                "incline" => "Measure or estimate grade percentage and note if slope direction is unclear.",
                "tactile_paving" => "Mark tactile paving as present or absent at crossing decision points.",
                "toilets:wheelchair" => "Verify wheelchair access, door width, grab bars, turning space, key requirement, and changing table.",
                "photos" => "Attach a clear field photo of the entrance, crossing, toilet, or obstruction without exposing private personal data.",
                _ => $"Verify {candidate.Attribute} with direct field evidence."
            });
        }

        if (profile.Confidence < 0.6)
        {
            checklist.Add("Treat this profile as low confidence until at least one recent field verification is applied.");
        }

        return checklist;
    }

    private static string BuildAccessibilityAdminSummary(
        InfrastructureAccessibilityProfile profile,
        int candidateCount,
        int checklistCount)
    {
        return $"Accessibility profile {profile.VerificationStatus} at confidence {profile.Confidence:0.00}; {profile.MissingFields.Count} missing field(s), {candidateCount} review candidate(s), {checklistCount} checklist item(s).";
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

    private static string BuildAdminSummary(
        HazardReport hazard,
        string suggestedType,
        string severity,
        int duplicateCount,
        int candidateCount)
    {
        return $"{suggestedType} report near {hazard.Location.Y:0.00000},{hazard.Location.X:0.00000}; severity {severity}; {duplicateCount} duplicate candidate(s); {candidateCount} OSM review candidate(s).";
    }

    private static string SuggestSeverity(string suggestedType, string searchText)
    {
        if (ContainsAny(searchText, "cannot pass", "dangerous", "blocked", "no sidewalk", "missing sidewalk")
            || suggestedType is "stairs" or "missing_curb_ramp" or "missing_sidewalk")
        {
            return "high";
        }

        return suggestedType is "low_lighting" or "surface_quality" or "obstruction" ? "medium" : "low";
    }

    private static string InferSurface(string searchText)
    {
        if (searchText.Contains("gravel", StringComparison.OrdinalIgnoreCase)) return "gravel";
        if (searchText.Contains("cobblestone", StringComparison.OrdinalIgnoreCase)) return "cobblestone";
        if (searchText.Contains("mud", StringComparison.OrdinalIgnoreCase)) return "mud";
        return "unpaved";
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDescription(string description)
    {
        var normalized = WhitespaceRegex().Replace(description.Trim(), " ");
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static string NormalizeToken(string token)
    {
        var normalized = TokenRegex().Replace(token.Trim().ToLowerInvariant(), "_");
        return normalized.Trim('_');
    }

    private static double DistanceMetres(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLng = ToRadians(lng2 - lng1);
        var rLat1 = ToRadians(lat1);
        var rLat2 = ToRadians(lat2);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(rLat1) * Math.Cos(rLat2) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusMetres * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex TokenRegex();

    private sealed record HazardRule(string SuggestedType, string Tag, double Confidence, string[] Keywords);
}
