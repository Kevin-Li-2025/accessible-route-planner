using AccessCity.API.Configuration;
using AccessCity.API.Models;
using AccessCity.API.Services.External;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using System.Diagnostics;

namespace AccessCity.API.Services;

public interface IRoutingService
{
    Task<RouteResponse> FindSafePathAsync(
        RouteRequest request,
        IEnumerable<HazardReport> allHazards,
        CancellationToken cancellationToken = default);

    Task<SafePathOptionsResponse> FindSafePathWithVariantsAsync(
        RouteRequest request,
        IEnumerable<HazardReport> allHazards,
        CancellationToken cancellationToken = default);

    RouteResponse FindSafePath(RouteRequest request, IEnumerable<HazardReport> hazards);
}

/// <summary>
/// Safety-aware routing engine with real spatial awareness.
/// 
/// Strategy (3-tier fallback):
///   1. Get real road routes from OSRM (foot profile) with alternatives
///   2. Score each route against PostGIS obstacle data (stairs, barriers, surfaces)
///      from the imported OSM graph for accessibility-aware selection
///   3. If hazards are close, compute avoidance waypoints and re-query OSRM
///   4. If OSRM unavailable, try A* on the real imported OSM road graph (PostGIS)
///   5. Last resort: synthetic grid fallback
/// </summary>
public class RoutingService : IRoutingService
{
    private readonly IRiskScoringService _riskService;
    private readonly IPredictiveRiskModel _aiRisk;
    private readonly IOsrmClient _osrmClient;
    private readonly IRouteGraphRepository _graphRepo;
    private readonly IRouteGraphStatusService _routeGraphStatus;
    private readonly IRiskTileCacheService _tileCache;
    private readonly IRouteCacheService _routeCache;
    private readonly IHazardRiskGrid _hazardRiskGrid;
    private readonly IHazardSpatialIndex _hazardSpatialIndex;
    private readonly RoutingOptions _routingOptions;

    private const double WalkingSpeed = 1.3;
    private const double MaxHeuristicSpeedMetresPerSecond = 2.0;
    private const double MinimumCostMultiplier = 0.8;
    private const double HazardAvoidanceRadiusMetres = 50.0;
    private const double HazardWaypointOffsetMetres = 100.0;
    private const string RelaxedAccessibilityGraphWarning =
        "No fully verified accessible path was found on the imported road network. Showing the lowest-cost route from the accessibility graph with explicit obstacle warnings.";

    /// <summary>Profile-specific edge filters for accessibility routing.</summary>
    private static readonly Dictionary<string, Func<GraphEdge, bool>> ProfileFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["manual-wheelchair"] = e => IsWheelFriendly(e, maxKerbHeightMetres: 0.03, allowCobblestone: false),
        ["power-wheelchair"] = e => IsWheelFriendly(e, maxKerbHeightMetres: 0.05, allowCobblestone: true),
        ["stroller"] = e => IsWheelFriendly(e, maxKerbHeightMetres: 0.05, allowCobblestone: true),
    };

    public RoutingService(
        IRiskScoringService riskService,
        IPredictiveRiskModel aiRisk,
        IOsrmClient osrmClient,
        IRouteGraphRepository graphRepo,
        IRouteGraphStatusService routeGraphStatus,
        IRiskTileCacheService tileCache,
        IRouteCacheService routeCache,
        IHazardRiskGrid hazardRiskGrid,
        IHazardSpatialIndex hazardSpatialIndex,
        IOptions<RoutingOptions> routingOptions)
    {
        _riskService = riskService;
        _aiRisk = aiRisk;
        _osrmClient = osrmClient;
        _graphRepo = graphRepo;
        _routeGraphStatus = routeGraphStatus;
        _tileCache = tileCache;
        _routeCache = routeCache;
        _hazardRiskGrid = hazardRiskGrid;
        _hazardSpatialIndex = hazardSpatialIndex;
        _routingOptions = routingOptions.Value;
    }

    /// <summary>
    /// Compute the safest / most accessible route from start to end.
    /// Uses OSRM for real road geometry, with obstacle-aware scoring and
    /// hazard-aware rerouting. Falls back to the imported OSM graph or a
    /// synthetic grid if OSRM is unavailable.
    /// </summary>
    public async Task<RouteResponse> FindSafePathAsync(
        RouteRequest request,
        IEnumerable<HazardReport> allHazards,
        CancellationToken cancellationToken = default)
    {
        var hazardList = allHazards
            .Where(h => h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview)
            .ToList();
        var contextFingerprint = await BuildRouteContextFingerprintAsync(hazardList, cancellationToken);

        // Route-level cache: return instantly for identical request + preference + risk context.
        var cacheKey = _routeCache.BuildKey(
            request.Start.Y, request.Start.X, request.End.Y, request.End.X,
            request.Profile ?? "standard", request.SafetyWeight,
            request.Preferences, contextFingerprint);
        var cached = await _routeCache.TryGetAsync(cacheKey);
        if (cached != null) return cached;

        RouteResponse response;
        RouteGraphData? graphForScoring = null;
        List<string> accessibilityGraphWarnings = new();
        RealGraphRouteAttempt? verifiedGraphAttempt = null;

        if (RequiresVerifiedAccessibility(request))
        {
            verifiedGraphAttempt = await TryFindSafePathOnRealGraphAsync(
                request,
                hazardList,
                cancellationToken);
            if (HasDrawableRoute(verifiedGraphAttempt.Response))
            {
                await CacheRouteAsync(cacheKey, verifiedGraphAttempt.Response!);
                return verifiedGraphAttempt.Response!;
            }

            graphForScoring = verifiedGraphAttempt.GraphData;
            accessibilityGraphWarnings = verifiedGraphAttempt.Warnings;
        }

        // ── Tier 1: OSRM with alternatives + PostGIS obstacle scoring ──
        var alternatives = _routingOptions.ExternalOsrmEnabled
            ? await _osrmClient.GetAlternativeRoutesAsync(request.Start, request.End, cancellationToken)
            : null;

        if (alternatives != null && alternatives.Count > 0
            && !IsOsrmDetourExcessive(request.Start, request.End, alternatives))
        {
            var graphData = graphForScoring ?? await TryLoadRouteGraphAsync(request, cancellationToken);
            response = await BuildBestOsrmRouteResponseAsync(request, hazardList, alternatives, graphData, cancellationToken);
            AddWarnings(response, accessibilityGraphWarnings);
            await CacheRouteAsync(cacheKey, response);
            return response;
        }

        // ── Tier 2: Real imported OSM graph (PostGIS) ──
        var realGraphAttempt = verifiedGraphAttempt
            ?? await TryFindSafePathOnRealGraphAsync(
                request,
                hazardList,
                cancellationToken);
        if (HasDrawableRoute(realGraphAttempt.Response))
        {
            await CacheRouteAsync(cacheKey, realGraphAttempt.Response!);
            return realGraphAttempt.Response!;
        }

        accessibilityGraphWarnings.AddRange(realGraphAttempt.Warnings);

        // ── Tier 3: Synthetic grid fallback ──
        response = FindSafePathFallback(request, hazardList);
        AddWarnings(response, accessibilityGraphWarnings);
        await CacheRouteAsync(cacheKey, response);
        return response;
    }

    private async Task CacheRouteAsync(string cacheKey, RouteResponse response)
    {
        try
        {
            await _routeCache.SetAsync(cacheKey, response);
        }
        catch
        {
            // Cache failures must never turn a successful route computation into a 5xx response.
        }
    }

    private async Task<string> BuildRouteContextFingerprintAsync(
        IEnumerable<HazardReport> hazards,
        CancellationToken cancellationToken)
    {
        var hazardContext = RouteRequestFingerprint.HazardContext(hazards);
        var graphVersion = await _routeGraphStatus.GetVersionAsync(cancellationToken);
        return $"{hazardContext}:graph:{graphVersion}";
    }

    /// <summary>
    /// User-weighted recommendation (same as <see cref="FindSafePathAsync"/> when OSRM succeeds) plus labelled OSRM
    /// alternatives: shortest distance, lowest composite risk (safetyWeight=1), and fastest estimated walk time.
    /// When OSRM returns only one geometry or all collapse to the same path, <see cref="SafePathOptionsResponse.Variants"/> may be empty.
    /// </summary>
    public async Task<SafePathOptionsResponse> FindSafePathWithVariantsAsync(
        RouteRequest request,
        IEnumerable<HazardReport> allHazards,
        CancellationToken cancellationToken = default)
    {
        var hazardList = allHazards
            .Where(h => h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview)
            .ToList();

        if (RequiresVerifiedAccessibility(request))
        {
            var rec = await FindSafePathAsync(request, hazardList, cancellationToken);
            return new SafePathOptionsResponse
            {
                Recommended = rec,
                Variants = new List<RoutedOptionVariant>(),
                Diagnostics = BuildSingleRouteDiagnostics(rec)
            };
        }

        var alternatives = _routingOptions.ExternalOsrmEnabled
            ? await _osrmClient.GetAlternativeRoutesAsync(request.Start, request.End, cancellationToken)
            : null;
        if (alternatives == null || alternatives.Count == 0
            || IsOsrmDetourExcessive(request.Start, request.End, alternatives))
        {
            var rec = await FindSafePathAsync(request, hazardList, cancellationToken);
            return new SafePathOptionsResponse
            {
                Recommended = rec,
                Variants = new List<RoutedOptionVariant>(),
                Diagnostics = BuildSingleRouteDiagnostics(rec)
            };
        }

        var graphData = await TryLoadRouteGraphAsync(request, cancellationToken);
        var recommended = await BuildBestOsrmRouteResponseAsync(request, hazardList, alternatives, graphData, cancellationToken);
        var optionMetrics = BuildRouteOptionMetrics(alternatives, hazardList, request, graphData);

        var shortestRaw = alternatives.OrderBy(a => a.DistanceMetres).First();
        var safestRaw = alternatives
            .OrderBy(a => optionMetrics[a].FullSafetyCompositeCost)
            .ThenBy(a => optionMetrics[a].AccessibilityPenaltySeconds)
            .First();
        var fastestRaw = alternatives.OrderBy(a => a.DurationSeconds).First();

        var candidateVariants = new List<RoutedOptionVariant>
        {
            new()
            {
                Kind = "shortest_distance",
                Description =
                    "Shortest walking distance among OSRM alternatives; may trade off safety or obstacle avoidance.",
                Route = BuildOsrmResponse(shortestRaw, hazardList, request, graphData),
                Metrics = CloneMetric(optionMetrics[shortestRaw], "shortest_distance")
            },
            new()
            {
                Kind = "lowest_composite_risk",
                Description =
                    "Lowest composite risk and obstacle penalty (full safety weight) among OSRM alternatives.",
                Route = BuildOsrmResponse(safestRaw, hazardList, request, graphData),
                Metrics = CloneMetric(optionMetrics[safestRaw], "lowest_composite_risk")
            },
            new()
            {
                Kind = "fastest_time",
                Description = "Shortest OSRM estimated walk time among alternatives.",
                Route = BuildOsrmResponse(fastestRaw, hazardList, request, graphData),
                Metrics = CloneMetric(optionMetrics[fastestRaw], "fastest_time")
            }
        };

        var variants = candidateVariants
            .Where(v => v.Route.Path != null && v.Route.Distance > 0)
            .Where(v => !RoutesNearlyEquivalent(recommended, v.Route, 12.0))
            .ToList();

        variants = DedupeVariantsBySimilarity(variants, 10.0);

        return new SafePathOptionsResponse
        {
            Recommended = recommended,
            Variants = variants,
            Diagnostics = BuildOptionSetDiagnostics(recommended, optionMetrics.Values)
        };
    }

    private async Task<RouteGraphData?> TryLoadRouteGraphAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        foreach (var loadOptions in BuildRouteGraphLoadAttempts())
        {
            try
            {
                var graphData = await LoadRouteGraphAttemptAsync(request, loadOptions, cancellationToken);
                if (HasUsableGraphEndpointCoverage(graphData, request, MaxGraphSnapDistanceMetres, out _, out _))
                {
                    return graphData;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Continue to wider graph attempts or degraded OSRM/fallback behavior.
            }
        }

        return null;
    }

    private async Task<RealGraphRouteAttempt> TryFindSafePathOnRealGraphAsync(
        RouteRequest request,
        List<HazardReport> hazardList,
        CancellationToken cancellationToken)
    {
        RouteGraphData? lastUsableGraph = null;
        var warnings = new List<string>();
        foreach (var loadOptions in BuildRouteGraphLoadAttempts())
        {
            try
            {
                var graphData = await LoadRouteGraphAttemptAsync(request, loadOptions, cancellationToken);
                if (!HasUsableGraphEndpointCoverage(graphData, request, MaxGraphSnapDistanceMetres, out _, out _))
                {
                    continue;
                }

                lastUsableGraph = graphData;
                var graphResponse = FindSafePathOnRealGraph(request, hazardList, graphData);
                if (HasDrawableRoute(graphResponse))
                {
                    return new RealGraphRouteAttempt(graphResponse, graphData, warnings);
                }

                warnings = graphResponse.Warnings;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Keep the request successful by trying wider graph slices, OSRM, or fallback routing.
            }
        }

        return new RealGraphRouteAttempt(null, lastUsableGraph, warnings);
    }

    private Task<RouteGraphData> LoadRouteGraphAttemptAsync(
        RouteRequest request,
        RouteGraphLoadOptions? loadOptions,
        CancellationToken cancellationToken) =>
        loadOptions is null
            ? _graphRepo.LoadGraphAsync(request.Start, request.End, cancellationToken)
            : _graphRepo.LoadGraphAsync(request.Start, request.End, loadOptions, cancellationToken);

    private IEnumerable<RouteGraphLoadOptions?> BuildRouteGraphLoadAttempts()
    {
        yield return null;
        if (!_routingOptions.RouteGraphAdaptiveCorridorWideningEnabled
            || !_routingOptions.RouteGraphCorridorSlicingEnabled)
        {
            yield break;
        }

        var attempts = Math.Clamp(_routingOptions.RouteGraphAdaptiveCorridorWideningAttempts, 0, 4);
        var basePadding = Math.Max(1.0, _routingOptions.RouteGraphCorridorPaddingMetres);
        var multiplier = Math.Clamp(_routingOptions.RouteGraphAdaptiveCorridorWideningMultiplier, 1.1, 4.0);
        var maxPadding = Math.Max(basePadding, _routingOptions.RouteGraphAdaptiveCorridorMaxPaddingMetres);
        var previousPadding = basePadding;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var padding = Math.Min(maxPadding, basePadding * Math.Pow(multiplier, attempt));
            if (padding <= previousPadding + 1.0)
            {
                continue;
            }

            previousPadding = padding;
            yield return new RouteGraphLoadOptions(padding);
            if (Math.Abs(padding - maxPadding) < 0.001)
            {
                yield break;
            }
        }
    }

    private double MaxGraphSnapDistanceMetres => Math.Max(1.0, _routingOptions.RouteGraphMaxSnapDistanceMetres);

    private sealed record RealGraphRouteAttempt(
        RouteResponse? Response,
        RouteGraphData? GraphData,
        List<string> Warnings);

    private static bool HasDrawableRoute(RouteResponse? response) =>
        response?.Path is not null
        && response.Path.Coordinates.Length >= 2
        && response.Distance > 0;

    private static void AddWarnings(RouteResponse response, IEnumerable<string> warnings)
    {
        var newWarnings = warnings
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .ToArray();
        if (newWarnings.Length == 0)
        {
            return;
        }

        response.Warnings = newWarnings
            .Concat(response.Warnings)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<RouteResponse> BuildBestOsrmRouteResponseAsync(
        RouteRequest request,
        List<HazardReport> hazardList,
        List<OsrmRouteResult> alternatives,
        RouteGraphData? graphData,
        CancellationToken cancellationToken)
    {
        var scoredRoutes = alternatives
            .Select(r => new
            {
                Route = r,
                Cost = ScoreRoute(r, hazardList, request.SafetyWeight, request.Profile, graphData)
            })
            .OrderBy(x => x.Cost)
            .ToList();

        var bestRoute = scoredRoutes.First().Route;
        var severeHazards = FindHazardsNearRoute(bestRoute.Coordinates, hazardList);
        if (severeHazards.Count > 0 && request.SafetyWeight > 0.3)
        {
            var rerouted = await AttemptWaypointRerouteAsync(request, bestRoute, severeHazards, hazardList, cancellationToken);
            if (rerouted != null)
            {
                double rerouteCost = ScoreRoute(rerouted, hazardList, request.SafetyWeight, request.Profile, graphData);
                if (rerouteCost < scoredRoutes.First().Cost)
                    bestRoute = rerouted;
            }
        }

        return BuildOsrmResponse(bestRoute, hazardList, request, graphData);
    }

    private static bool RoutesNearlyEquivalent(RouteResponse a, RouteResponse b, double distanceToleranceMetres)
    {
        if (a.Path == null || b.Path == null) return false;
        return Math.Abs(a.Distance - b.Distance) < distanceToleranceMetres
               && Math.Abs(a.SafetyScore - b.SafetyScore) < 0.04
               && Math.Abs(a.EstimatedTime - b.EstimatedTime) < 8.0;
    }

    private static List<RoutedOptionVariant> DedupeVariantsBySimilarity(
        List<RoutedOptionVariant> variants,
        double distanceToleranceMetres)
    {
        var result = new List<RoutedOptionVariant>();
        foreach (var v in variants)
        {
            if (v.Route.Path == null) continue;
            bool duplicate = result.Any(r =>
                Math.Abs(r.Route.Distance - v.Route.Distance) < distanceToleranceMetres
                && Math.Abs(r.Route.SafetyScore - v.Route.SafetyScore) < 0.03);
            if (!duplicate)
                result.Add(v);
        }

        return result;
    }

    private Dictionary<OsrmRouteResult, RouteTradeoffMetrics> BuildRouteOptionMetrics(
        IReadOnlyList<OsrmRouteResult> alternatives,
        List<HazardReport> hazards,
        RouteRequest request,
        RouteGraphData? graphData)
    {
        var metrics = alternatives.ToDictionary(
            route => route,
            route =>
            {
                var riskExposure = ComputeRouteTotalRisk(route.Coordinates, hazards);
                var accessibilityPenalty = graphData is { HasCoverage: true }
                    ? ComputeObstaclePenalty(route.Coordinates, graphData, request.Profile) * 60.0
                    : 0;
                var compositeCost = ScoreRoute(route, hazards, request.SafetyWeight, request.Profile, graphData);
                var fullSafetyCompositeCost = ScoreRoute(route, hazards, 1.0, request.Profile, graphData);

                return new RouteTradeoffMetrics
                {
                    DistanceMetres = Math.Round(route.DistanceMetres, 1),
                    EstimatedTimeMinutes = CalculateEstimatedMinutes(route.DistanceMetres, route.DurationSeconds),
                    RiskExposure = Math.Round(riskExposure, 4),
                    AccessibilityPenaltySeconds = Math.Round(accessibilityPenalty, 2),
                    CompositeCost = Math.Round(compositeCost, 4),
                    FullSafetyCompositeCost = Math.Round(fullSafetyCompositeCost, 4)
                };
            });

        MarkParetoEfficient(metrics.Values);
        return metrics;
    }

    private static RouteTradeoffMetrics CloneMetric(RouteTradeoffMetrics metric, string kind) => new()
    {
        Kind = kind,
        DistanceMetres = metric.DistanceMetres,
        EstimatedTimeMinutes = metric.EstimatedTimeMinutes,
        RiskExposure = metric.RiskExposure,
        AccessibilityPenaltySeconds = metric.AccessibilityPenaltySeconds,
        CompositeCost = metric.CompositeCost,
        FullSafetyCompositeCost = metric.FullSafetyCompositeCost,
        ParetoEfficient = metric.ParetoEfficient
    };

    private static RouteOptionSetDiagnostics BuildOptionSetDiagnostics(
        RouteResponse recommended,
        IEnumerable<RouteTradeoffMetrics> metrics)
    {
        var metricList = metrics.ToList();
        if (metricList.Count == 0)
        {
            return BuildSingleRouteDiagnostics(recommended);
        }

        var bestTime = metricList.Min(metric => metric.EstimatedTimeMinutes);
        var bestRisk = metricList.Min(metric => metric.RiskExposure);
        return new RouteOptionSetDiagnostics
        {
            CandidateCount = metricList.Count,
            ParetoEfficientCount = metricList.Count(metric => metric.ParetoEfficient),
            RecommendedRegretSeconds = Math.Round(Math.Max(0, recommended.EstimatedTime - bestTime) * 60.0, 2),
            RecommendedRiskRegret = Math.Round(Math.Max(0, (1.0 - recommended.SafetyScore) - bestRisk), 4),
            RecommendedPerformance = recommended.Performance,
            Frontier = metricList
                .Where(metric => metric.ParetoEfficient)
                .OrderBy(metric => metric.CompositeCost)
                .Take(5)
                .ToList()
        };
    }

    private static RouteOptionSetDiagnostics BuildSingleRouteDiagnostics(RouteResponse route)
    {
        var riskExposure = Math.Round(1.0 - route.SafetyScore, 4);
        return new RouteOptionSetDiagnostics
        {
            CandidateCount = route.Path is null ? 0 : 1,
            ParetoEfficientCount = route.Path is null ? 0 : 1,
            RecommendedRegretSeconds = 0,
            RecommendedRiskRegret = 0,
            RecommendedPerformance = route.Performance,
            Frontier = route.Path is null
                ? new List<RouteTradeoffMetrics>()
                :
                [
                    new RouteTradeoffMetrics
                    {
                        Kind = "recommended",
                        DistanceMetres = route.Distance,
                        EstimatedTimeMinutes = route.EstimatedTime,
                        RiskExposure = riskExposure,
                        CompositeCost = Math.Round(route.Distance / 1000.0 + riskExposure, 4),
                        FullSafetyCompositeCost = Math.Round(riskExposure, 4),
                        ParetoEfficient = true
                    }
                ]
        };
    }

    private static void MarkParetoEfficient(IEnumerable<RouteTradeoffMetrics> metrics)
    {
        var metricList = metrics.ToList();
        foreach (var candidate in metricList)
        {
            candidate.ParetoEfficient = !metricList.Any(other =>
                !ReferenceEquals(candidate, other)
                && Dominates(other, candidate));
        }
    }

    private static bool Dominates(RouteTradeoffMetrics a, RouteTradeoffMetrics b)
    {
        var noWorse = a.DistanceMetres <= b.DistanceMetres
                      && a.EstimatedTimeMinutes <= b.EstimatedTimeMinutes
                      && a.RiskExposure <= b.RiskExposure
                      && a.AccessibilityPenaltySeconds <= b.AccessibilityPenaltySeconds;
        var strictlyBetter = a.DistanceMetres < b.DistanceMetres
                             || a.EstimatedTimeMinutes < b.EstimatedTimeMinutes
                             || a.RiskExposure < b.RiskExposure
                             || a.AccessibilityPenaltySeconds < b.AccessibilityPenaltySeconds;
        return noWorse && strictlyBetter;
    }

    /// <summary>
    /// Synchronous fallback — uses the old synthetic grid approach.
    /// </summary>
    public RouteResponse FindSafePath(
        RouteRequest request,
        IEnumerable<HazardReport> allHazards)
    {
        return FindSafePathFallback(request, allHazards.ToList());
    }

    // ──────── Tier 2: Real OSM Graph routing ────────

    /// <summary>
    /// A* search on the real imported OSM graph loaded from PostGIS.
    /// This graph has real surface types, stair info, barrier data, kerb heights, etc.
    /// </summary>
    private RouteResponse FindSafePathOnRealGraph(
        RouteRequest request,
        List<HazardReport> hazardList,
        RouteGraphData graphData)
    {
        long startId = FindNearest(graphData, request.Start);
        long endId = FindNearest(graphData, request.End);

        if (startId == endId)
        {
            double directDist = RiskScoringService.HaversineDistance(
                request.Start.Y, request.Start.X, request.End.Y, request.End.X);
            return new RouteResponse
            {
                Path = new LineString(new[] { request.Start, request.End }),
                Distance = directDist,
                EstimatedTime = CalculateEstimatedMinutes(directDist, null),
                SafetyScore = 1.0,
                Warnings = new List<string> { "Origin and destination are very close." },
                Steps = new List<RouteStep>
                {
                    new()
                    {
                        From = new Point(request.Start),
                        To = new Point(request.End),
                        Distance = Math.Round(directDist, 1),
                        SafetyScore = 1.0,
                        Instruction = "Proceed directly to your destination."
                    }
                }
            };
        }

        // Use Contraction Hierarchies only for static, deterministic shortest-path
        // queries. The CH artifact does not encode live hazards, safety weighting,
        // or per-request comfort preferences, so safe-path requests still need A*/ALT.
        AStarSearchResult? searchResult = null;
        List<long>? path = null;
        if (CanUseContractionHierarchy(request, hazardList))
        {
            var chData = ContractionHierarchy.ResolveForProfile(
                graphData.ContractionHierarchies, request.Profile);
            if (chData is not null)
            {
                var chResult = ContractionHierarchy.Query(chData, startId, endId);
                if (chResult.PathNodeIds is { Length: >= 2 })
                {
                    var candidatePath = chResult.PathNodeIds.ToList();
                    if (IsRealGraphPathTraversable(candidatePath, graphData.Nodes, request))
                    {
                        path = candidatePath;
                    }
                }
            }
        }

        if (path is null)
        {
            searchResult = AStarSearch(graphData.Nodes, startId, endId, request, hazardList, graphData.Preprocessing);
            path = searchResult.Path;
        }

        var usedRelaxedAccessibilitySearch = false;
        if ((path == null || path.Count < 2) && RequiresVerifiedAccessibility(request))
        {
            searchResult = AStarSearch(
                graphData.Nodes,
                startId,
                endId,
                request,
                hazardList,
                graphData.Preprocessing,
                enforceHardFilters: false);
            path = searchResult.Path;
            usedRelaxedAccessibilitySearch = path is { Count: >= 2 };
        }

        if (path == null || path.Count < 2)
        {
            return new RouteResponse
            {
                SafetyScore = 0,
                Warnings = new List<string>
                {
                    "No accessible route found on the imported road network. Try relaxing your accessibility preferences."
                }
            };
        }

        var response = BuildRealGraphResponse(path, graphData.Nodes, request, hazardList);
        if (searchResult is not null)
        {
            response.Performance = searchResult.Diagnostics;
            response.Performance.UsedRelaxedAccessibilitySearch = usedRelaxedAccessibilitySearch;
        }

        if (usedRelaxedAccessibilitySearch)
        {
            response.Warnings.Insert(0, RelaxedAccessibilityGraphWarning);
            response.Warnings = response.Warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return response;
    }

    private static bool CanUseContractionHierarchy(RouteRequest request, IReadOnlyCollection<HazardReport> hazards)
    {
        return request.SafetyWeight <= 0.001
               && hazards.Count == 0
               && request.Preferences.Count == 0;
    }

    private static bool IsRealGraphPathTraversable(
        IReadOnlyList<long> path,
        IReadOnlyDictionary<long, GraphNode> graph,
        RouteRequest request)
    {
        if (path.Count < 2)
        {
            return false;
        }

        var filters = BuildEdgeFilterChain(request);
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (!graph.TryGetValue(path[i], out var fromNode)
                || !fromNode.Edges.TryGetValue(path[i + 1], out var edge))
            {
                return false;
            }

            foreach (var filter in filters)
            {
                if (!filter(edge))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Build RouteResponse from a path on the real OSM graph, using actual
    /// edge geometry so routes follow real roads.
    /// </summary>
    private RouteResponse BuildRealGraphResponse(
        List<long> path,
        Dictionary<long, GraphNode> graph,
        RouteRequest request,
        List<HazardReport> hazards)
    {
        var allCoordinates = new List<Coordinate>();
        double totalDist = 0;
        double safetySum = 0;
        var steps = new List<RouteStep>();
        var warnings = new List<string>();
        var traversedEdges = new List<GraphEdge>();

        for (int i = 0; i < path.Count - 1; i++)
        {
            var fromNode = graph[path[i]];
            var toNode = graph[path[i + 1]];
            var edge = fromNode.Edges[path[i + 1]];
            traversedEdges.Add(edge);

            double segDist = edge.DistanceMetres;
            totalDist += segDist;

            // Use real edge geometry if available, otherwise straight line
            Coordinate[] segCoords;
            if (edge.Geometry != null && edge.Geometry.Length >= 2)
            {
                segCoords = edge.Geometry;
            }
            else
            {
                segCoords = new[] { fromNode.Location, toNode.Location };
            }

            if (i == 0)
                allCoordinates.AddRange(segCoords);
            else
                allCoordinates.AddRange(segCoords.Skip(1)); // avoid duplicate junctions

            double midLat = (fromNode.Location.Y + toNode.Location.Y) / 2.0;
            double midLon = (fromNode.Location.X + toNode.Location.X) / 2.0;
            double segRisk = _riskService.QuickRisk(midLat, midLon, hazards);
            double segSafety = 1.0 - segRisk;
            safetySum += segSafety * segDist;

            string instruction = GenerateRealGraphInstruction(fromNode, toNode, edge, i, path.Count - 1);

            steps.Add(new RouteStep
            {
                From = new Point(fromNode.Location),
                To = new Point(toNode.Location),
                Distance = Math.Round(segDist, 1),
                SafetyScore = Math.Round(segSafety, 3),
                Instruction = instruction
            });

            // Generate real spatial awareness warnings from actual OSM data
            if (edge.HasStairs)
                warnings.Add($"Step {i + 1}: This segment contains stairs — {FormatAccessibilityNote("stairs", request.Profile)}.");
            if (edge.HasBarrier)
                warnings.Add($"Step {i + 1}: Physical barrier detected on this path.");
            if (IsWheelchairAccessRestricted(edge) && IsAccessibilityProfile(request.Profile))
                warnings.Add($"Step {i + 1}: OSM marks this segment as not wheelchair accessible.");
            if (edge.KerbHeight > 0.03)
                warnings.Add($"Step {i + 1}: Raised kerb ({edge.KerbHeight * 100:F0}cm) — may affect wheelchair access.");
            if (edge.SurfaceType is "cobblestone" or "gravel" or "unpaved")
                warnings.Add($"Step {i + 1}: Surface is {edge.SurfaceType} — may be difficult for wheeled mobility.");
            if (edge.LightingQuality < 0.3)
                warnings.Add($"Step {i + 1}: Poor street lighting detected.");
            if (edge.IsUnderConstruction)
                warnings.Add($"Step {i + 1}: Active construction zone — proceed with caution.");
            if (edge.IsSteep)
                warnings.Add($"Step {i + 1}: Steep gradient ahead.");
            if (edge.WidthMetres.HasValue && edge.WidthMetres < 0.9)
                warnings.Add($"Step {i + 1}: Narrow path ({edge.WidthMetres:F1}m) — limited wheelchair access.");
            if (segRisk > 0.7)
                warnings.Add($"Step {i + 1}: Elevated risk area (score {segRisk:F2}).");
        }

        double avgSafety = totalDist > 0 ? safetySum / totalDist : 1.0;
        var dataQualityWarning = BuildAccessibilityDataQualitySummary(traversedEdges, request.Profile);
        if (dataQualityWarning is not null)
        {
            warnings.Add(dataQualityWarning);
        }

        if (allCoordinates.Count < 2)
        {
            allCoordinates = path.Select(id => graph[id].Location).ToList();
        }

        return new RouteResponse
        {
            Path = new LineString(allCoordinates.ToArray()),
            Distance = Math.Round(totalDist, 1),
            EstimatedTime = CalculateEstimatedMinutes(totalDist, null),
            SafetyScore = Math.Round(Math.Clamp(avgSafety, 0, 1), 3),
            Warnings = warnings.Distinct().ToList(),
            Steps = steps
        };
    }

    private static string GenerateRealGraphInstruction(
        GraphNode from, GraphNode to, GraphEdge edge, int stepIndex, int totalSteps)
    {
        double bearing = CalculateBearing(from.Location, to.Location);
        string direction = BearingToCardinal(bearing);
        string distText = edge.DistanceMetres < 100
            ? $"{edge.DistanceMetres:F0}m"
            : $"{edge.DistanceMetres / 1000.0:F2}km";

        if (stepIndex == 0)
            return $"Head {direction} for {distText}.";
        if (stepIndex == totalSteps - 1)
            return $"Continue {direction} for {distText} to reach your destination.";

        var notes = new List<string>();
        if (edge.SurfaceType != "asphalt" && edge.SurfaceType != "paving_stones")
            notes.Add($"surface: {edge.SurfaceType}");
        if (edge.HasStairs) notes.Add("stairs");
        if (edge.IsSteep) notes.Add("steep");

        string surfaceNote = notes.Count > 0 ? $" ({string.Join(", ", notes)})" : "";
        return $"Continue {direction} for {distText}{surfaceNote}.";
    }

    private static string FormatAccessibilityNote(string obstacle, string profile) => profile switch
    {
        "manual-wheelchair" or "power-wheelchair" => $"{obstacle} are not wheelchair accessible",
        "stroller" => $"{obstacle} are difficult with a stroller",
        _ => $"{obstacle} present"
    };

    private static bool RequiresVerifiedAccessibility(RouteRequest request)
    {
        if (IsAccessibilityProfile(request.Profile))
        {
            return true;
        }

        return request.Preferences.Any(preference =>
            string.Equals(preference, "wheelchair", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAccessibilityProfile(string? profile) =>
        string.Equals(profile, "manual-wheelchair", StringComparison.OrdinalIgnoreCase)
        || string.Equals(profile, "power-wheelchair", StringComparison.OrdinalIgnoreCase)
        || string.Equals(profile, "stroller", StringComparison.OrdinalIgnoreCase);

    private static bool HasUnknownSurface(GraphEdge edge) =>
        string.IsNullOrWhiteSpace(edge.SurfaceType)
        || string.Equals(edge.SurfaceType, "unknown", StringComparison.OrdinalIgnoreCase);

    private static double ComputeAccessibilityDataGapPenalty(GraphEdge edge, string? profile)
    {
        var strict = IsAccessibilityProfile(profile);
        double penalty = 0;

        if (HasUnknownSurface(edge))
        {
            penalty += strict ? 0.20 : 0.05;
        }

        if (strict && !edge.WidthMetres.HasValue)
        {
            penalty += 0.20;
        }

        if (strict && string.IsNullOrWhiteSpace(edge.Smoothness))
        {
            penalty += 0.15;
        }

        return penalty;
    }

    private static string? BuildAccessibilityDataQualitySummary(IEnumerable<GraphEdge> edges, string? profile)
    {
        var edgeList = edges as IReadOnlyCollection<GraphEdge> ?? edges.ToList();
        if (edgeList.Count == 0)
        {
            return null;
        }

        var strict = IsAccessibilityProfile(profile);
        var missingSurface = edgeList.Count(HasUnknownSurface);
        var missingWidth = strict ? edgeList.Count(edge => !edge.WidthMetres.HasValue) : 0;
        var missingSmoothness = strict ? edgeList.Count(edge => string.IsNullOrWhiteSpace(edge.Smoothness)) : 0;

        var issues = new List<string>();
        if (missingSurface > 0) issues.Add($"{missingSurface} missing surface");
        if (missingWidth > 0) issues.Add($"{missingWidth} missing width");
        if (missingSmoothness > 0) issues.Add($"{missingSmoothness} missing smoothness");

        if (issues.Count == 0)
        {
            return null;
        }

        return $"Accessibility data confidence is lower: {string.Join(", ", issues)} tag(s) on the selected route rely on inferred defaults.";
    }

    private static bool IsWheelFriendly(GraphEdge edge, double maxKerbHeightMetres, bool allowCobblestone)
    {
        if (edge.HasStairs || edge.HasBarrier || IsAccessBlocked(edge))
        {
            return false;
        }

        if (edge.KerbHeight > maxKerbHeightMetres)
        {
            return false;
        }

        if (edge.WidthMetres.HasValue && edge.WidthMetres < 0.9)
        {
            return false;
        }

        if (!SmoothnessAllowsWheels(edge.Smoothness))
        {
            return false;
        }

        return edge.SurfaceType.ToLowerInvariant() switch
        {
            "gravel" or "unpaved" or "sand" or "dirt" or "earth" or "grass" => false,
            "cobblestone" or "sett" => allowCobblestone,
            _ => true
        };
    }

    private static bool IsAccessBlocked(GraphEdge edge)
    {
        if (string.IsNullOrWhiteSpace(edge.Access))
        {
            return false;
        }

        var access = edge.Access.ToLowerInvariant();
        return access.Contains("access=no", StringComparison.Ordinal)
               || access.Contains("access=private", StringComparison.Ordinal)
               || access.Contains("foot=no", StringComparison.Ordinal)
               || access.Contains("wheelchair=no", StringComparison.Ordinal);
    }

    private static bool IsPedestrianAccessBlocked(GraphEdge edge)
    {
        if (string.IsNullOrWhiteSpace(edge.Access))
        {
            return false;
        }

        var access = edge.Access.ToLowerInvariant();
        return access.Contains("access=no", StringComparison.Ordinal)
               || access.Contains("access=private", StringComparison.Ordinal)
               || access.Contains("foot=no", StringComparison.Ordinal);
    }

    private static bool IsWheelchairAccessRestricted(GraphEdge edge) =>
        !string.IsNullOrWhiteSpace(edge.Access)
        && edge.Access.Contains("wheelchair=no", StringComparison.OrdinalIgnoreCase);

    private static bool SmoothnessAllowsWheels(string? smoothness)
    {
        if (string.IsNullOrWhiteSpace(smoothness))
        {
            return true;
        }

        return smoothness.ToLowerInvariant() switch
        {
            "bad" or "very_bad" or "horrible" or "very_horrible" or "impassable" => false,
            _ => true
        };
    }

    // ──────── OSRM-based routing with obstacle awareness ────────

    /// <summary>
    /// Find hazards that are dangerously close to the route path.
    /// Uses the in-memory R-Tree spatial index to pre-filter candidates,
    /// reducing complexity from O(H × R) to O(K × R) where K ≪ H.
    /// </summary>
    private List<HazardReport> FindHazardsNearRoute(
        List<Coordinate> routeCoords,
        List<HazardReport> hazards)
    {
        // Compute the bounding box of the route polyline + buffer
        IReadOnlyList<HazardReport> candidates;
        if (_hazardSpatialIndex.IsWarmedUp && routeCoords.Count > 0)
        {
            double minLon = double.MaxValue, maxLon = double.MinValue;
            double minLat = double.MaxValue, maxLat = double.MinValue;
            foreach (var c in routeCoords)
            {
                if (c.X < minLon) minLon = c.X;
                if (c.X > maxLon) maxLon = c.X;
                if (c.Y < minLat) minLat = c.Y;
                if (c.Y > maxLat) maxLat = c.Y;
            }
            // Expand by avoidance radius (~50m ≈ 0.00045 degrees)
            double bufferDeg = HazardAvoidanceRadiusMetres / 111_320.0;
            candidates = _hazardSpatialIndex.QueryBoundingBox(
                minLon - bufferDeg, minLat - bufferDeg,
                maxLon + bufferDeg, maxLat + bufferDeg);
        }
        else
        {
            candidates = hazards;
        }

        var result = new List<HazardReport>();
        foreach (var hazard in candidates)
        {
            var minDist = DistancePointToPolylineMetres(hazard.Location.Coordinate, routeCoords);
            if (minDist < HazardAvoidanceRadiusMetres)
                result.Add(hazard);
        }

        return result;
    }

    private static double DistancePointToPolylineMetres(Coordinate point, IReadOnlyList<Coordinate> line)
    {
        if (line.Count == 0)
        {
            return double.MaxValue;
        }

        if (line.Count == 1)
        {
            return RiskScoringService.HaversineDistance(point.Y, point.X, line[0].Y, line[0].X);
        }

        var best = double.MaxValue;
        for (var i = 0; i < line.Count - 1; i++)
        {
            best = Math.Min(best, DistancePointToSegmentMetres(point, line[i], line[i + 1]));
            if (best < HazardAvoidanceRadiusMetres)
            {
                return best;
            }
        }

        return best;
    }

    private static double DistancePointToSegmentMetres(Coordinate point, Coordinate a, Coordinate b)
    {
        var lat0 = point.Y * Math.PI / 180.0;
        var metresPerDegreeLon = 111_320.0 * Math.Max(0.1, Math.Cos(lat0));
        const double metresPerDegreeLat = 111_320.0;

        var ax = (a.X - point.X) * metresPerDegreeLon;
        var ay = (a.Y - point.Y) * metresPerDegreeLat;
        var bx = (b.X - point.X) * metresPerDegreeLon;
        var by = (b.Y - point.Y) * metresPerDegreeLat;

        var dx = bx - ax;
        var dy = by - ay;
        var lenSq = dx * dx + dy * dy;
        if (lenSq <= 1e-9)
        {
            return Math.Sqrt(ax * ax + ay * ay);
        }

        var t = Math.Clamp(-(ax * dx + ay * dy) / lenSq, 0.0, 1.0);
        var closestX = ax + t * dx;
        var closestY = ay + t * dy;
        return Math.Sqrt(closestX * closestX + closestY * closestY);
    }

    private async Task<OsrmRouteResult?> AttemptWaypointRerouteAsync(
        RouteRequest request,
        OsrmRouteResult primaryRoute,
        List<HazardReport> nearbyHazards,
        List<HazardReport> allHazards,
        CancellationToken cancellationToken)
    {
        var waypoints = ComputeAvoidanceWaypoints(primaryRoute.Coordinates, nearbyHazards);
        if (waypoints.Count > 0)
        {
            var rerouted = await _osrmClient.GetRouteAsync(request.Start, request.End, waypoints, cancellationToken);
            if (rerouted != null && rerouted.Coordinates.Count >= 2)
            {
                if (rerouted.DistanceMetres < primaryRoute.DistanceMetres * 2.0)
                    return rerouted;
            }
        }

        return null;
    }

    /// <summary>
    /// Score an OSRM route considering hazards, distance, AND real obstacle data from PostGIS.
    /// When graphData is available, routes passing through stairs/barriers/bad surfaces
    /// are penalized based on the user's profile.
    /// </summary>
    private double ScoreRoute(
        OsrmRouteResult route,
        List<HazardReport> hazards,
        double safetyWeight,
        string profile,
        RouteGraphData? graphData)
    {
        double normalizedDist = route.DistanceMetres / 1000.0; // km
        double totalRisk = ComputeRouteTotalRisk(route.Coordinates, hazards);
        double normalizedRisk = totalRisk / 30.0;

        double obstaclePenalty = 0;

        // Check OSRM route against real PostGIS obstacle data
        if (graphData != null && graphData.HasCoverage)
        {
            obstaclePenalty = ComputeObstaclePenalty(route.Coordinates, graphData, profile);
        }

        return (normalizedDist * (1.0 - safetyWeight))
            + (normalizedRisk * safetyWeight * 5.0)
            + (obstaclePenalty * safetyWeight * 3.0);
    }

    /// <summary>
    /// Cross-reference OSRM route coordinates against real imported OSM edges
    /// to detect obstacles (stairs, barriers, bad surfaces) along the path.
    /// Returns a penalty score [0, N] where N grows with obstacle severity.
    /// </summary>
    private static double ComputeObstaclePenalty(
        List<Coordinate> routeCoords,
        RouteGraphData graphData,
        string profile)
    {
        if (!graphData.HasCoverage) return 0;

        double penalty = 0;
        var checkedEdges = new HashSet<(long, long)>();

        // Sample route at intervals and find nearest graph edges
        int step = Math.Max(1, routeCoords.Count / 40);
        for (int i = 0; i < routeCoords.Count; i += step)
        {
            var coord = routeCoords[i];

            var nearestNode = FindNearestNode(graphData, coord, maxDistanceMetres: 50.0);
            if (nearestNode is null) continue;

            // Check all edges from this node for obstacles
            foreach (var (targetId, edge) in nearestNode.Edges)
            {
                if (!checkedEdges.Add((nearestNode.Id, targetId))) continue;

                // Profile-specific penalties
                if (ProfileFilters.TryGetValue(profile, out var filter) && !filter(edge))
                {
                    // This edge is impassable for the user's profile
                    penalty += 2.0;
                }

                // Universal penalties (applied regardless of profile)
                if (edge.HasStairs) penalty += 1.5;
                if (edge.HasBarrier) penalty += 1.0;
                if (edge.SurfaceType is "cobblestone") penalty += 0.3;
                if (edge.SurfaceType is "gravel" or "unpaved") penalty += 0.5;
                if (edge.IsSteep) penalty += 0.4;
                if (edge.KerbHeight > 0.05) penalty += 0.6;
                if (edge.WidthMetres.HasValue && edge.WidthMetres < 0.9) penalty += 0.4;
                penalty += ComputeAccessibilityDataGapPenalty(edge, profile);
            }
        }

        return penalty;
    }

    /// <summary>
    /// Compute avoidance waypoints by pushing the route perpendicular to the
    /// direction of travel at hazard locations.
    /// </summary>
    private List<Coordinate> ComputeAvoidanceWaypoints(
        List<Coordinate> routeCoords,
        List<HazardReport> hazards)
    {
        var waypoints = new List<Coordinate>();

        foreach (var hazard in hazards)
        {
            int closestIdx = 0;
            double closestDist = double.MaxValue;

            for (int i = 0; i < routeCoords.Count; i++)
            {
                double dist = RiskScoringService.HaversineDistance(
                    routeCoords[i].Y, routeCoords[i].X,
                    hazard.Location.Y, hazard.Location.X);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestIdx = i;
                }
            }

            int prevIdx = Math.Max(0, closestIdx - 1);
            int nextIdx = Math.Min(routeCoords.Count - 1, closestIdx + 1);

            double dLon = routeCoords[nextIdx].X - routeCoords[prevIdx].X;
            double dLat = routeCoords[nextIdx].Y - routeCoords[prevIdx].Y;

            double perpLon = -dLat;
            double perpLat = dLon;
            double perpLen = Math.Sqrt(perpLon * perpLon + perpLat * perpLat);

            if (perpLen < 1e-10) continue;

            perpLon /= perpLen;
            perpLat /= perpLen;

            double offsetDegLat = (HazardWaypointOffsetMetres / 111320.0) * perpLat;
            double offsetDegLon = (HazardWaypointOffsetMetres /
                (111320.0 * Math.Cos(hazard.Location.Y * Math.PI / 180.0))) * perpLon;

            double waypointLon1 = routeCoords[closestIdx].X + offsetDegLon;
            double waypointLat1 = routeCoords[closestIdx].Y + offsetDegLat;
            double waypointLon2 = routeCoords[closestIdx].X - offsetDegLon;
            double waypointLat2 = routeCoords[closestIdx].Y - offsetDegLat;

            double dist1 = RiskScoringService.EquirectangularDistance(
                waypointLat1, waypointLon1,
                hazard.Location.Y, hazard.Location.X);
            double dist2 = RiskScoringService.EquirectangularDistance(
                waypointLat2, waypointLon2,
                hazard.Location.Y, hazard.Location.X);

            if (dist1 > dist2)
                waypoints.Add(new Coordinate(waypointLon1, waypointLat1));
            else
                waypoints.Add(new Coordinate(waypointLon2, waypointLat2));
        }

        return waypoints;
    }

    /// <summary>
    /// Compute cumulative risk score along a route.
    /// </summary>
    private double ComputeRouteTotalRisk(List<Coordinate> coords, List<HazardReport> hazards)
    {
        double totalRisk = 0;
        int step = Math.Max(1, coords.Count / 30);

        for (int i = 0; i < coords.Count; i += step)
        {
            totalRisk += _aiRisk.QuickPredictiveRisk(coords[i].Y, coords[i].X, hazards, 200);
        }

        return totalRisk;
    }

    /// <summary>
    /// Build the standard RouteResponse from an OSRM result,
    /// enriched with real obstacle data from PostGIS when available.
    /// </summary>
    private RouteResponse BuildOsrmResponse(
        OsrmRouteResult osrmRoute,
        List<HazardReport> hazards,
        RouteRequest request,
        RouteGraphData? graphData)
    {
        var coordinates = osrmRoute.Coordinates.ToArray();
        var lineString = new LineString(coordinates);

        var steps = new List<RouteStep>();
        var warnings = new List<string>();
        double safetySum = 0;
        double totalDist = osrmRoute.DistanceMetres;

        if (RequiresVerifiedAccessibility(request) && (graphData is null || !graphData.HasCoverage))
        {
            warnings.Add("Verified accessibility graph data is unavailable for this route; the OSRM foot route may not satisfy all mobility constraints.");
        }

        if (osrmRoute.Steps.Count > 0)
        {
            int stepIdx = 0;
            foreach (var osrmStep in osrmRoute.Steps)
            {
                if (osrmStep.Geometry.Count < 2) continue;
                if (osrmStep.Distance < 0.1) continue;

                var from = osrmStep.Geometry.First();
                var to = osrmStep.Geometry.Last();

                double midLat = (from.Y + to.Y) / 2.0;
                double midLon = (from.X + to.X) / 2.0;
                double segRisk = _aiRisk.QuickPredictiveRisk(midLat, midLon, hazards, 200);
                double segSafety = 1.0 - segRisk;
                safetySum += segSafety * osrmStep.Distance;

                string instruction = FormatOsrmInstruction(osrmStep, stepIdx, osrmRoute.Steps.Count);

                steps.Add(new RouteStep
                {
                    From = new Point(from),
                    To = new Point(to),
                    Distance = Math.Round(osrmStep.Distance, 1),
                    SafetyScore = Math.Round(segSafety, 3),
                    Instruction = instruction
                });

                // Warnings from risk scoring
                if (segRisk > 0.5)
                    warnings.Add($"Step {stepIdx + 1}: Elevated risk area (score {segRisk:F2}).");

                // Warnings from reported hazards — use R-Tree spatial index
                // to avoid O(S × H) brute-force loop over all hazards per step.
                var nearbyHazards = _hazardSpatialIndex.IsWarmedUp
                    ? _hazardSpatialIndex.QueryNearby(midLat, midLon, 100)
                    : (IReadOnlyList<HazardReport>)hazards;
                foreach (var hazard in nearbyHazards)
                {
                    if (hazard.Location is null) continue;
                    double dist = RiskScoringService.EquirectangularDistance(
                        midLat, midLon, hazard.Location.Y, hazard.Location.X);
                    if (dist < 100)
                    {
                        string warnMsg = hazard.Type switch
                        {
                            "construction" => $"Step {stepIdx + 1}: Active construction zone nearby — proceed with caution.",
                            "poor_lighting" => $"Step {stepIdx + 1}: Poor street lighting detected.",
                            "pothole" => $"Step {stepIdx + 1}: Reported pothole nearby — watch your step.",
                            "obstruction" => $"Step {stepIdx + 1}: Footpath obstruction reported nearby.",
                            "missing_curb_ramp" => $"Step {stepIdx + 1}: Missing kerb ramp — limited wheelchair access.",
                            "broken_pavement" => $"Step {stepIdx + 1}: Broken pavement reported nearby.",
                            "steep_gradient" => $"Step {stepIdx + 1}: Steep gradient ahead.",
                            "missing_crossing" => $"Step {stepIdx + 1}: No pedestrian crossing — use caution.",
                            _ => $"Step {stepIdx + 1}: Hazard ({hazard.Type}) reported nearby."
                        };
                        warnings.Add(warnMsg);
                    }
                }

                // Spatial obstacle warnings from real PostGIS data
                if (graphData != null && graphData.HasCoverage)
                {
                    var obstacleWarnings = GetObstacleWarningsForSegment(
                        osrmStep.Geometry, graphData, request.Profile, stepIdx + 1);
                    warnings.AddRange(obstacleWarnings);
                }

                stepIdx++;
            }
        }
        else
        {
            for (int i = 0; i < coordinates.Length - 1; i++)
            {
                double segDist = RiskScoringService.HaversineDistance(
                    coordinates[i].Y, coordinates[i].X,
                    coordinates[i + 1].Y, coordinates[i + 1].X);

                double midLat = (coordinates[i].Y + coordinates[i + 1].Y) / 2.0;
                double midLon = (coordinates[i].X + coordinates[i + 1].X) / 2.0;
                double segRisk = _aiRisk.QuickPredictiveRisk(midLat, midLon, hazards, 200);
                double segSafety = 1.0 - segRisk;
                safetySum += segSafety * segDist;
            }
        }

        double avgSafety = totalDist > 0 ? safetySum / totalDist : 1.0;

        return new RouteResponse
        {
            Path = lineString,
            Distance = Math.Round(totalDist, 1),
            EstimatedTime = CalculateEstimatedMinutes(totalDist, osrmRoute.DurationSeconds),
            SafetyScore = Math.Round(Math.Clamp(avgSafety, 0, 1), 3),
            Warnings = warnings.Distinct().ToList(),
            Steps = steps
        };
    }

    /// <summary>
    /// Calculated estimated walking time in minutes.
    /// Uses OSRM duration if realistic, otherwise falls back to a standard 1.3 m/s walking speed.
    /// </summary>
    private static double CalculateEstimatedMinutes(double distanceMetres, double? osrmDurationSeconds)
    {
        // 1.3 m/s is the standard crosswalk design speed (3 mph).
        double standardSeconds = distanceMetres / WalkingSpeed;

        // If OSRM says we can walk it at > 4 m/s (14.4 km/h), it's probably using 
        // a bike profile or has bad metadata. We clamp it to the standard speed.
        double finalSeconds = osrmDurationSeconds.HasValue && osrmDurationSeconds.Value > (standardSeconds * 0.3)
            ? osrmDurationSeconds.Value
            : standardSeconds;

        return Math.Round(finalSeconds / 60.0, 1);
    }

    /// <summary>
    /// Detect when OSRM returns unrealistic detours. Compares every alternative's
    /// distance against the straight-line (Haversine) distance. If ALL alternatives
    /// exceed 3x the crow-flies distance, OSRM is likely routing around a
    /// pedestrianised zone or tunnel and should be bypassed.
    /// </summary>
    private static bool IsOsrmDetourExcessive(Coordinate start, Coordinate end, List<OsrmRouteResult> alternatives)
    {
        const double MaxDetourRatio = 3.0;

        double straightLine = RiskScoringService.HaversineDistance(start.Y, start.X, end.Y, end.X);
        if (straightLine < 50) return false; // Too short to judge

        return alternatives.All(a => a.DistanceMetres / straightLine > MaxDetourRatio);
    }

    /// <summary>
    /// Check an OSRM step segment against real PostGIS obstacle data and generate warnings.
    /// </summary>
    private static List<string> GetObstacleWarningsForSegment(
        List<Coordinate> segmentGeometry,
        RouteGraphData graphData,
        string profile,
        int stepNumber)
    {
        var warnings = new List<string>();
        var checkedNodes = new HashSet<long>();
        var incompleteNearbyAccessibilityData = false;

        // Sample the segment at midpoint and endpoints
        var samplePoints = new List<Coordinate>();
        if (segmentGeometry.Count > 0) samplePoints.Add(segmentGeometry.First());
        if (segmentGeometry.Count > 2)
        {
            samplePoints.Add(segmentGeometry[segmentGeometry.Count / 2]);
        }
        if (segmentGeometry.Count > 1) samplePoints.Add(segmentGeometry.Last());

        foreach (var point in samplePoints)
        {
            // Find nearby graph nodes from the prebuilt shard index instead of scanning the whole graph.
            foreach (var node in FindNodesNear(graphData, point, radiusMetres: 30.0))
            {
                if (!checkedNodes.Add(node.Id)) continue;

                foreach (var (_, edge) in node.Edges)
                {
                    if (edge.HasStairs && profile is "manual-wheelchair" or "power-wheelchair" or "stroller")
                        warnings.Add($"Step {stepNumber}: ⚠️ Stairs detected nearby — not {profile} accessible.");
                    if (edge.HasBarrier)
                        warnings.Add($"Step {stepNumber}: Physical barrier detected on nearby path.");
                    if (edge.KerbHeight > 0.05 && profile is "manual-wheelchair" or "power-wheelchair")
                        warnings.Add($"Step {stepNumber}: High kerb ({edge.KerbHeight * 100:F0}cm) nearby — may block {profile}.");
                    if (edge.SurfaceType is "cobblestone" or "gravel" or "unpaved" &&
                        profile is "manual-wheelchair" or "power-wheelchair")
                        warnings.Add($"Step {stepNumber}: {edge.SurfaceType} surface nearby — difficult for {profile}.");
                    if (IsAccessibilityProfile(profile)
                        && (HasUnknownSurface(edge)
                            || !edge.WidthMetres.HasValue
                            || string.IsNullOrWhiteSpace(edge.Smoothness)))
                    {
                        incompleteNearbyAccessibilityData = true;
                    }
                }
            }
        }

        if (incompleteNearbyAccessibilityData)
        {
            warnings.Add($"Step {stepNumber}: Nearby accessibility tags are incomplete; verify surface, width, or smoothness before relying on this route.");
        }

        return warnings;
    }

    /// <summary>
    /// Format OSRM maneuver into a human-readable instruction.
    /// </summary>
    private static string FormatOsrmInstruction(OsrmStepResult step, int index, int total)
    {
        string distText = step.Distance < 100
            ? $"{step.Distance:F0}m"
            : $"{step.Distance / 1000.0:F2}km";

        string streetInfo = !string.IsNullOrEmpty(step.StreetName)
            ? $" on {step.StreetName}"
            : "";

        string cardinal = "";
        if (step.Geometry.Count >= 2)
        {
            double bearing = CalculateBearing(step.Geometry.First(), step.Geometry.Last());
            cardinal = $" {BearingToCardinal(bearing)}";
        }

        if (index == 0)
            return $"Head{cardinal}{streetInfo} for {distText}.";

        if (index == total - 1 || step.ManeuverType == "arrive")
            return $"Arrive at your destination{streetInfo}.";

        string direction = step.ManeuverModifier switch
        {
            "left" => "Turn left",
            "right" => "Turn right",
            "slight left" => "Bear left",
            "slight right" => "Bear right",
            "sharp left" => "Turn sharp left",
            "sharp right" => "Turn sharp right",
            "straight" => "Continue straight",
            "uturn" => "Make a U-turn",
            _ => step.ManeuverType switch
            {
                "turn" => "Turn",
                "new name" => "Continue",
                "depart" => "Depart",
                "merge" => "Merge",
                "fork" => "Take the fork",
                "roundabout" => "Enter the roundabout",
                _ => "Continue"
            }
        };

        return $"{direction}{cardinal}{streetInfo} for {distText}.";
    }

    // ──────── Fallback: Synthetic Grid (last resort) ────────

    private static readonly Dictionary<string, Func<GraphEdge, bool>> EdgeFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["avoid-stairs"] = e => !e.HasStairs,
        ["wheelchair"] = e => !e.HasStairs && e.SurfaceType != "cobblestone" && e.SurfaceType != "gravel",
        ["avoid-cobblestone"] = e => e.SurfaceType != "cobblestone",
        ["avoid-construction"] = e => !e.IsUnderConstruction,
        ["avoid-steep-hills"] = e => !e.IsSteep,
        ["avoid-reported-hazards"] = e => true,
        ["prefer-crossings"] = e => true,
    };

    private static readonly Dictionary<string, Func<GraphEdge, double>> CostModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low-light-penalty"] = e => 1.0 + (1.0 - e.LightingQuality) * 0.5,
        ["prefer-crossings"] = e => e.HasCrossing ? 0.85 : 1.15,
        ["avoid-reported-hazards"] = e => 1.0 + Math.Clamp(e.BaseSafetyCost, 0.0, 1.0) * 2.0,
        ["avoid-construction"] = e => e.IsUnderConstruction ? 4.0 : 1.0,
        ["avoid-cobblestone"] = e => e.SurfaceType is "cobblestone" or "sett" ? 2.5 : 1.0,
        ["avoid-steep-hills"] = e => e.IsSteep ? 2.0 : 1.0,
    };

    private RouteResponse FindSafePathFallback(
        RouteRequest request,
        List<HazardReport> hazardList)
    {
        double directDist = RiskScoringService.HaversineDistance(
            request.Start.Y, request.Start.X,
            request.End.Y, request.End.X);

        double latStep = 0.0007;
        double lonStep = 0.0010;

        if (directDist > 10000)
        {
            latStep = 0.005;
            lonStep = 0.007;
        }
        if (directDist > 50000)
        {
            latStep = 0.02;
            lonStep = 0.03;
        }

        var graph = BuildGraph(request.Start, request.End, hazardList, latStep, lonStep);
        long startId = FindNearest(graph, request.Start);
        long endId = FindNearest(graph, request.End);

        if (startId == endId)
        {
            return new RouteResponse
            {
                Path = new LineString(new[] { request.Start, request.End }),
                Distance = directDist,
                EstimatedTime = CalculateEstimatedMinutes(directDist, null),
                SafetyScore = 1.0,
                Warnings = new List<string> { "Origin and destination are very close." },
                Steps = new List<RouteStep>
                {
                    new()
                    {
                        From = new Point(request.Start),
                        To = new Point(request.End),
                        Distance = Math.Round(directDist, 1),
                        SafetyScore = 1.0,
                        Instruction = "Proceed directly to your destination."
                    }
                }
            };
        }

        var searchResult = AStarSearch(graph, startId, endId, request, hazardList);
        var path = searchResult.Path;

        if (path == null || path.Count < 2)
        {
            return new RouteResponse
            {
                SafetyScore = 0,
                Warnings = new List<string>
                {
                    "No accessible route found. Try relaxing your accessibility preferences."
                }
            };
        }

        var response = BuildFallbackResponse(path, graph, request, hazardList);
        response.Performance = searchResult.Diagnostics;
        return response;
    }

    // ──────── A* search (shared between real graph and fallback) ────────

    private AStarSearchResult AStarSearch(
        Dictionary<long, GraphNode> graph,
        long startId, long endId,
        RouteRequest request,
        List<HazardReport> hazards,
        RouteGraphPreprocessingData? preprocessing = null,
        bool enforceHardFilters = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var endNode = graph[endId];
        var diagnostics = new RoutePerformanceDiagnostics
        {
            Algorithm = preprocessing?.HasLandmarks == true
                ? "astar-alt-accessibility-risk"
                : "astar-accessibility-risk",
            UsedAltHeuristic = preprocessing?.HasLandmarks == true
        };
        var gScore = new Dictionary<long, double> { [startId] = 0 };
        var fScore = new Dictionary<long, double>
        {
            [startId] = Heuristic(graph, startId, endId, endNode.Location, preprocessing)
        };
        var cameFrom = new Dictionary<long, long>();
        var open = new PriorityQueue<long, double>();
        open.Enqueue(startId, fScore[startId]);
        diagnostics.QueuePushes = 1;
        var closed = new HashSet<long>();
        var riskContext = RouteRiskContext.Create(
            request,
            hazards,
            _hazardRiskGrid,
            _riskService);

        // Build combined edge filter from preferences AND profile.
        // In relaxed mode, profile penalties still affect ComputeEdgeCost, but
        // the graph remains connected enough to return a degraded-confidence route.
        var edgeFilterChain = BuildEdgeFilterChain(request, enforceHardFilters);

        while (open.Count > 0)
        {
            long current = open.Dequeue();

            if (current == endId)
            {
                stopwatch.Stop();
                diagnostics.FoundPath = true;
                diagnostics.SearchMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
                riskContext.CopyCountersTo(diagnostics);
                return new AStarSearchResult(ReconstructPath(cameFrom, current), diagnostics);
            }

            if (!closed.Add(current)) continue;
            diagnostics.NodesExpanded++;

            var currentNode = graph[current];

            foreach (var (neighbourId, edge) in currentNode.Edges)
            {
                diagnostics.EdgesScanned++;
                if (closed.Contains(neighbourId)) continue;
                if (!graph.ContainsKey(neighbourId)) continue;

                // Apply preference + profile filters
                bool passesFilters = true;
                foreach (var filter in edgeFilterChain)
                {
                    if (!filter(edge))
                    {
                        passesFilters = false;
                        break;
                    }
                }
                if (!passesFilters)
                {
                    diagnostics.EdgesRejectedByFilter++;
                    continue;
                }

                double edgeCost = ComputeEdgeCost(
                    edge,
                    currentNode,
                    graph[neighbourId],
                    request,
                    riskContext);
                double tentativeG = gScore[current] + edgeCost;

                if (tentativeG < gScore.GetValueOrDefault(neighbourId, double.MaxValue))
                {
                    diagnostics.EdgesRelaxed++;
                    cameFrom[neighbourId] = current;
                    gScore[neighbourId] = tentativeG;
                    double f = tentativeG +
                               Heuristic(graph, neighbourId, endId, endNode.Location, preprocessing);
                    fScore[neighbourId] = f;
                    open.Enqueue(neighbourId, f);
                    diagnostics.QueuePushes++;
                }
            }
        }

        stopwatch.Stop();
        diagnostics.FoundPath = false;
        diagnostics.SearchMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3);
        riskContext.CopyCountersTo(diagnostics);
        return new AStarSearchResult(null, diagnostics);
    }

    private sealed record AStarSearchResult(List<long>? Path, RoutePerformanceDiagnostics Diagnostics);

    /// <summary>
    /// Build a combined filter chain from user preferences AND mobility profile.
    /// This ensures A* never traverses edges that are impassable for the user.
    /// </summary>
    private static List<Func<GraphEdge, bool>> BuildEdgeFilterChain(
        RouteRequest request,
        bool enforceHardFilters = true)
    {
        var filters = new List<Func<GraphEdge, bool>>();
        if (!enforceHardFilters)
        {
            filters.Add(edge => !IsPedestrianAccessBlocked(edge));
            return filters;
        }

        // Profile-based filter (always applied)
        if (ProfileFilters.TryGetValue(request.Profile, out var profileFilter))
        {
            filters.Add(profileFilter);
        }

        // User preference filters
        foreach (var pref in request.Preferences)
        {
            if (EdgeFilters.TryGetValue(pref, out var filter))
                filters.Add(filter);
        }

        return filters;
    }

    private static double Heuristic(Coordinate a, Coordinate b)
    {
        // Equirectangular approximation: 6-8x faster than Haversine,
        // accurate to <0.1% for city-scale distances (<50km).
        double dist = RiskScoringService.EquirectangularDistance(a.Y, a.X, b.Y, b.X);
        return dist / MaxHeuristicSpeedMetresPerSecond;
    }

    private static double Heuristic(
        IReadOnlyDictionary<long, GraphNode> graph,
        long nodeId,
        long endId,
        Coordinate endLocation,
        RouteGraphPreprocessingData? preprocessing)
    {
        if (!graph.TryGetValue(nodeId, out var node))
        {
            return 0;
        }

        var directLowerBound = Heuristic(node.Location, endLocation);
        var altLowerBound = RouteGraphPreprocessor.ComputeAltLowerBoundSeconds(preprocessing, nodeId, endId);
        return Math.Max(directLowerBound, altLowerBound);
    }

    private double ComputeEdgeCost(
        GraphEdge edge, GraphNode fromNode, GraphNode toNode,
        RouteRequest request,
        RouteRiskContext riskContext)
    {
        double w = Math.Clamp(request.SafetyWeight, 0.0, 1.0);
        double baseSeconds = edge.DistanceMetres / ResolveProfileSpeed(request.Profile);
        var liveRisk = riskContext.GetRisk(fromNode, toNode);
        double edgeRisk = Math.Clamp((edge.BaseSafetyCost + liveRisk) / 2.0, 0.0, 1.0);
        double riskPenaltySeconds = baseSeconds * edgeRisk * 4.0 * w;
        double accessibilityPenaltySeconds = Math.Max(
            0,
            RouteEdgeCostModel.ResolveTraversalSeconds(edge, request.Profile) - baseSeconds) * w;

        double modifier = 1.0;
        foreach (var pref in request.Preferences)
        {
            if (CostModifiers.TryGetValue(pref, out var fn))
                modifier *= fn(edge);
        }

        modifier = Math.Max(MinimumCostMultiplier, modifier);
        var cost = (baseSeconds + riskPenaltySeconds + accessibilityPenaltySeconds) * modifier;
        return Math.Max(cost, edge.DistanceMetres / MaxHeuristicSpeedMetresPerSecond);
    }

    private static double ResolveProfileSpeed(string? profile) => profile switch
    {
        "manual-wheelchair" => 0.9,
        "power-wheelchair" => 1.1,
        "stroller" => 1.1,
        _ => WalkingSpeed
    };

    private static List<long> ReconstructPath(Dictionary<long, long> cameFrom, long current)
    {
        var path = new List<long> { current };
        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            path.Add(current);
        }
        path.Reverse();
        return path;
    }

    private sealed class RouteRiskContext
    {
        private readonly bool _enabled;
        private readonly bool _useGrid;
        private readonly IReadOnlyList<HazardReport> _hazards;
        private readonly IHazardRiskGrid _hazardRiskGrid;
        private readonly IRiskScoringService _riskService;
        private readonly Dictionary<(long From, long To), double> _memo = new();
        private int _lookups;
        private int _cacheHits;
        private int _cacheMisses;

        private RouteRiskContext(
            bool enabled,
            bool useGrid,
            IReadOnlyList<HazardReport> hazards,
            IHazardRiskGrid hazardRiskGrid,
            IRiskScoringService riskService)
        {
            _enabled = enabled;
            _useGrid = useGrid;
            _hazards = hazards;
            _hazardRiskGrid = hazardRiskGrid;
            _riskService = riskService;
        }

        public static RouteRiskContext Create(
            RouteRequest request,
            IReadOnlyList<HazardReport> hazards,
            IHazardRiskGrid hazardRiskGrid,
            IRiskScoringService riskService)
        {
            var safetyWeight = Math.Clamp(request.SafetyWeight, 0.0, 1.0);
            var enabled = safetyWeight > 0.001 && (hazardRiskGrid.IsReady || hazards.Count > 0);
            return new RouteRiskContext(enabled, hazardRiskGrid.IsReady, hazards, hazardRiskGrid, riskService);
        }

        public double GetRisk(GraphNode fromNode, GraphNode toNode)
        {
            if (!_enabled)
            {
                return 0;
            }

            _lookups++;
            var riskKey = fromNode.Id <= toNode.Id
                ? (fromNode.Id, toNode.Id)
                : (toNode.Id, fromNode.Id);
            if (_memo.TryGetValue(riskKey, out var cached))
            {
                _cacheHits++;
                return cached;
            }

            _cacheMisses++;
            double midLat = (fromNode.Location.Y + toNode.Location.Y) / 2.0;
            double midLon = (fromNode.Location.X + toNode.Location.X) / 2.0;
            var liveRisk = _useGrid
                ? _hazardRiskGrid.GetRisk(midLat, midLon)
                : _riskService.QuickRisk(midLat, midLon, _hazards, radiusMetres: 200);
            _memo[riskKey] = liveRisk;
            return liveRisk;
        }

        public void CopyCountersTo(RoutePerformanceDiagnostics diagnostics)
        {
            diagnostics.RiskLookups = _lookups;
            diagnostics.RiskCacheHits = _cacheHits;
            diagnostics.RiskCacheMisses = _cacheMisses;
        }
    }

    private RouteResponse BuildFallbackResponse(
        List<long> path,
        Dictionary<long, GraphNode> graph,
        RouteRequest request,
        List<HazardReport> hazards)
    {
        var coordinates = path.Select(id => graph[id].Location).ToArray();
        var lineString = new LineString(coordinates);

        double totalDist = 0;
        double safetySum = 0;
        var steps = new List<RouteStep>();
        var warnings = new List<string> {
            "Real road data is unavailable for this area. An approximate mesh-based route is shown."
        };
        var traversedEdges = new List<GraphEdge>();

        for (int i = 0; i < path.Count - 1; i++)
        {
            var fromNode = graph[path[i]];
            var toNode = graph[path[i + 1]];
            var edge = fromNode.Edges[path[i + 1]];
            traversedEdges.Add(edge);

            double segDist = edge.DistanceMetres;
            totalDist += segDist;

            double midLat = (fromNode.Location.Y + toNode.Location.Y) / 2.0;
            double midLon = (fromNode.Location.X + toNode.Location.X) / 2.0;
            // O(1) grid lookup when the precomputed risk grid is available;
            // falls back to the original O(N) QuickRisk linear scan otherwise.
            double segRisk = _hazardRiskGrid.IsReady
                ? _hazardRiskGrid.GetRisk(midLat, midLon)
                : _riskService.QuickRisk(midLat, midLon, hazards);
            double segSafety = 1.0 - segRisk;
            safetySum += segSafety * segDist;

            string instruction = GenerateInstruction(fromNode, toNode, edge, i, path.Count - 1);

            steps.Add(new RouteStep
            {
                From = new Point(fromNode.Location),
                To = new Point(toNode.Location),
                Distance = Math.Round(segDist, 1),
                SafetyScore = Math.Round(segSafety, 3),
                Instruction = instruction
            });

            if (edge.HasStairs)
                warnings.Add($"Step {i + 1}: This segment contains stairs.");
            if (edge.LightingQuality < 0.3)
                warnings.Add($"Step {i + 1}: Poor street lighting detected.");
            if (edge.IsUnderConstruction)
                warnings.Add($"Step {i + 1}: Active construction zone — proceed with caution.");
            if (segRisk > 0.7)
                warnings.Add($"Step {i + 1}: Elevated risk area (score {segRisk:F2}).");
        }

        double avgSafety = totalDist > 0 ? safetySum / totalDist : 1.0;
        var dataQualityWarning = BuildAccessibilityDataQualitySummary(traversedEdges, request.Profile);
        if (dataQualityWarning is not null)
        {
            warnings.Add(dataQualityWarning);
        }

        return new RouteResponse
        {
            Path = lineString,
            Distance = Math.Round(totalDist, 1),
            EstimatedTime = CalculateEstimatedMinutes(totalDist, null),
            SafetyScore = Math.Round(Math.Clamp(avgSafety, 0, 1), 3),
            Warnings = warnings.Distinct().ToList(),
            Steps = steps
        };
    }

    // ──────── Graph building (fallback only) ────────

    private Dictionary<long, GraphNode> BuildGraph(
        Coordinate start, Coordinate end,
        List<HazardReport> hazards,
        double latStep = 0.0007, double lonStep = 0.0010)
    {
        double minLat = Math.Min(start.Y, end.Y) - latStep * 5;
        double maxLat = Math.Max(start.Y, end.Y) + latStep * 5;
        double minLon = Math.Min(start.X, end.X) - lonStep * 5;
        double maxLon = Math.Max(start.X, end.X) + lonStep * 5;

        int rows = Math.Min((int)Math.Ceiling((maxLat - minLat) / latStep), 150);
        int cols = Math.Min((int)Math.Ceiling((maxLon - minLon) / lonStep), 150);

        maxLat = minLat + rows * latStep;
        maxLon = minLon + cols * lonStep;

        var graph = new Dictionary<long, GraphNode>();
        var coordToId = new Dictionary<(int, int), long>();
        long nextId = 1;

        for (int r = 0; r <= rows; r++)
        {
            for (int c = 0; c <= cols; c++)
            {
                double lat = minLat + r * latStep;
                double lon = minLon + c * lonStep;
                long id = nextId++;
                graph[id] = new GraphNode { Id = id, Location = new Coordinate(lon, lat) };
                coordToId[(r, c)] = id;
            }
        }

        int[] dr = { -1, -1, -1, 0, 0, 1, 1, 1 };
        int[] dc = { -1, 0, 1, -1, 1, -1, 0, 1 };

        for (int r = 0; r <= rows; r++)
        {
            for (int c = 0; c <= cols; c++)
            {
                long fromId = coordToId[(r, c)];
                var fromNode = graph[fromId];

                for (int d = 0; d < 8; d++)
                {
                    int nr = r + dr[d];
                    int nc = c + dc[d];
                    if (nr < 0 || nr > rows || nc < 0 || nc > cols) continue;

                    long toId = coordToId[(nr, nc)];
                    var toNode = graph[toId];

                    double dist = RiskScoringService.HaversineDistance(
                        fromNode.Location.Y, fromNode.Location.X,
                        toNode.Location.Y, toNode.Location.X);

                    double midLat = (fromNode.Location.Y + toNode.Location.Y) / 2.0;
                    double midLon = (fromNode.Location.X + toNode.Location.X) / 2.0;
                    // Use O(1) grid lookup for fallback mesh construction when available.
                    double riskAtMid = _hazardRiskGrid.IsReady
                        ? _hazardRiskGrid.GetRisk(midLat, midLon)
                        : _riskService.QuickRisk(midLat, midLon, hazards, 150);

                    int seed = HashCode.Combine(
                        Math.Round(midLat, 5), Math.Round(midLon, 5));
                    var localRng = new Random(seed);

                    string surface = localRng.NextDouble() switch
                    {
                        < 0.70 => "asphalt",
                        < 0.85 => "paving_stones",
                        < 0.92 => "cobblestone",
                        < 0.97 => "gravel",
                        _ => "unpaved"
                    };

                    fromNode.Edges[toId] = new GraphEdge
                    {
                        TargetNodeId = toId,
                        DistanceMetres = dist,
                        BaseSafetyCost = riskAtMid,
                        SurfaceType = surface,
                        HasStairs = localRng.NextDouble() < 0.03,
                        HasCrossing = localRng.NextDouble() < 0.25,
                        IsUnderConstruction = localRng.NextDouble() < 0.02,
                        LightingQuality = 0.4 + localRng.NextDouble() * 0.6,
                        IsSteep = localRng.NextDouble() < 0.05
                    };
                }
            }
        }

        InjectVirtualNode(graph, 0, start, coordToId, rows, cols, latStep, lonStep, minLat, minLon, hazards);
        InjectVirtualNode(graph, -1, end, coordToId, rows, cols, latStep, lonStep, minLat, minLon, hazards);

        return graph;
    }

    private void InjectVirtualNode(
        Dictionary<long, GraphNode> graph, long virtualId, Coordinate coord,
        Dictionary<(int, int), long> coordToId, int rows, int cols,
        double latStep, double lonStep, double minLat, double minLon,
        List<HazardReport> hazards)
    {
        var vNode = new GraphNode { Id = virtualId, Location = coord };
        graph[virtualId] = vNode;
        int r = (int)Math.Round((coord.Y - minLat) / latStep);
        int c = (int)Math.Round((coord.X - minLon) / lonStep);
        r = Math.Clamp(r, 0, rows);
        c = Math.Clamp(c, 0, cols);

        for (int dr = -1; dr <= 1; dr++)
        {
            for (int dc = -1; dc <= 1; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                int nr = r + dr;
                int nc = c + dc;
                if (nr < 0 || nr > rows || nc < 0 || nc > cols) continue;
                if (!coordToId.ContainsKey((nr, nc))) continue;

                long gridId = coordToId[(nr, nc)];
                var gridNode = graph[gridId];

                double dist = RiskScoringService.HaversineDistance(
                    coord.Y, coord.X, gridNode.Location.Y, gridNode.Location.X);

                var edge = new GraphEdge
                {
                    TargetNodeId = gridId,
                    DistanceMetres = dist,
                    BaseSafetyCost = 0.1,
                    SurfaceType = "asphalt",
                    LightingQuality = 0.8
                };
                vNode.Edges[gridId] = edge;
                gridNode.Edges[virtualId] = new GraphEdge
                {
                    TargetNodeId = virtualId,
                    DistanceMetres = dist,
                    BaseSafetyCost = 0.1,
                    SurfaceType = "asphalt",
                    LightingQuality = 0.8
                };
            }
        }
    }

    private static long FindNearest(Dictionary<long, GraphNode> graph, Coordinate point)
    {
        long bestId = graph.Keys.First();
        double bestDist = double.MaxValue;

        foreach (var node in graph.Values)
        {
            double d = RiskScoringService.HaversineDistance(
                point.Y, point.X, node.Location.Y, node.Location.X);
            if (d < bestDist)
            {
                bestDist = d;
                bestId = node.Id;
            }
        }
        return bestId;
    }

    private static long FindNearest(RouteGraphData graphData, Coordinate point)
    {
        var nearest = FindNearestNode(graphData, point, maxDistanceMetres: double.PositiveInfinity);
        return nearest?.Id ?? graphData.Nodes.Keys.First();
    }

    private static bool HasUsableGraphEndpointCoverage(
        RouteGraphData graphData,
        RouteRequest request,
        double maxSnapDistanceMetres,
        out double startSnapDistanceMetres,
        out double endSnapDistanceMetres)
    {
        startSnapDistanceMetres = double.PositiveInfinity;
        endSnapDistanceMetres = double.PositiveInfinity;

        if (!graphData.HasCoverage || graphData.IsTruncated || graphData.Nodes.Count == 0)
        {
            return false;
        }

        var startNode = FindNearestNode(graphData, request.Start, maxDistanceMetres: double.PositiveInfinity);
        var endNode = FindNearestNode(graphData, request.End, maxDistanceMetres: double.PositiveInfinity);
        if (startNode is null || endNode is null)
        {
            return false;
        }

        startSnapDistanceMetres = RiskScoringService.HaversineDistance(
            request.Start.Y, request.Start.X, startNode.Location.Y, startNode.Location.X);
        endSnapDistanceMetres = RiskScoringService.HaversineDistance(
            request.End.Y, request.End.X, endNode.Location.Y, endNode.Location.X);

        return startSnapDistanceMetres <= maxSnapDistanceMetres
               && endSnapDistanceMetres <= maxSnapDistanceMetres;
    }

    private static GraphNode? FindNearestNode(
        RouteGraphData graphData,
        Coordinate point,
        double maxDistanceMetres)
    {
        if (graphData.Nodes.Count == 0)
        {
            return null;
        }

        var candidates = double.IsFinite(maxDistanceMetres)
            ? FindNodesNear(graphData, point, maxDistanceMetres)
            : FindNearestBucketCandidates(graphData, point);

        GraphNode? best = null;
        var bestDist = double.IsFinite(maxDistanceMetres)
            ? maxDistanceMetres
            : double.PositiveInfinity;
        foreach (var node in candidates)
        {
            var dist = RiskScoringService.HaversineDistance(
                point.Y, point.X, node.Location.Y, node.Location.X);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = node;
            }
        }

        return best;
    }

    private static IEnumerable<GraphNode> FindNearestBucketCandidates(
        RouteGraphData graphData,
        Coordinate point)
    {
        if (graphData.SpatialBuckets.Count == 0 || graphData.SpatialBucketSizeDegrees <= 0)
        {
            return graphData.Nodes.Values;
        }

        var bucketSize = graphData.SpatialBucketSizeDegrees;
        var origin = (
            X: (int)Math.Floor(point.X / bucketSize),
            Y: (int)Math.Floor(point.Y / bucketSize));
        var maxRing = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(graphData.SpatialBuckets.Count)));
        GraphNode? best = null;
        var bestDist = double.PositiveInfinity;

        for (var ring = 0; ring <= maxRing; ring++)
        {
            for (var dx = -ring; dx <= ring; dx++)
            {
                for (var dy = -ring; dy <= ring; dy++)
                {
                    if (ring > 0 && Math.Abs(dx) != ring && Math.Abs(dy) != ring)
                    {
                        continue;
                    }

                    if (!graphData.SpatialBuckets.TryGetValue((origin.X + dx, origin.Y + dy), out var nodeIds))
                    {
                        continue;
                    }

                    foreach (var nodeId in nodeIds)
                    {
                        if (graphData.Nodes.TryGetValue(nodeId, out var node))
                        {
                            var dist = RiskScoringService.HaversineDistance(
                                point.Y,
                                point.X,
                                node.Location.Y,
                                node.Location.X);
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                best = node;
                            }
                        }
                    }
                }
            }

            if (best is not null && RingLowerBoundMetres(ring + 1, bucketSize, point.Y) > bestDist)
            {
                return [best];
            }
        }

        return best is null ? graphData.Nodes.Values : [best];
    }

    private static double RingLowerBoundMetres(int ring, double bucketSizeDegrees, double latitude)
    {
        if (ring <= 0)
        {
            return 0;
        }

        var metresPerDegreeLon = 111_320.0 * Math.Max(0.1, Math.Cos(latitude * Math.PI / 180.0));
        var minCellOffsetDegrees = Math.Max(0, ring - 1) * bucketSizeDegrees;
        return minCellOffsetDegrees * Math.Min(111_320.0, metresPerDegreeLon);
    }

    private static IEnumerable<GraphNode> FindNodesNear(
        RouteGraphData graphData,
        Coordinate point,
        double radiusMetres)
    {
        if (graphData.SpatialBuckets.Count == 0 || graphData.SpatialBucketSizeDegrees <= 0)
        {
            return graphData.Nodes.Values.Where(node =>
                RiskScoringService.HaversineDistance(
                    point.Y, point.X, node.Location.Y, node.Location.X) <= radiusMetres);
        }

        var bucketSize = graphData.SpatialBucketSizeDegrees;
        var origin = (
            X: (int)Math.Floor(point.X / bucketSize),
            Y: (int)Math.Floor(point.Y / bucketSize));
        var bucketRadius = Math.Max(1, (int)Math.Ceiling((radiusMetres / 111_320.0) / bucketSize) + 1);
        var nodes = new List<GraphNode>();

        for (var dx = -bucketRadius; dx <= bucketRadius; dx++)
        {
            for (var dy = -bucketRadius; dy <= bucketRadius; dy++)
            {
                if (!graphData.SpatialBuckets.TryGetValue((origin.X + dx, origin.Y + dy), out var nodeIds))
                {
                    continue;
                }

                foreach (var nodeId in nodeIds)
                {
                    if (!graphData.Nodes.TryGetValue(nodeId, out var node))
                    {
                        continue;
                    }

                    var dist = RiskScoringService.HaversineDistance(
                        point.Y, point.X, node.Location.Y, node.Location.X);
                    if (dist <= radiusMetres)
                    {
                        nodes.Add(node);
                    }
                }
            }
        }

        return nodes;
    }

    private static string GenerateInstruction(
        GraphNode from, GraphNode to, GraphEdge edge, int stepIndex, int totalSteps)
    {
        double bearing = CalculateBearing(from.Location, to.Location);
        string direction = BearingToCardinal(bearing);
        string distText = edge.DistanceMetres < 100
            ? $"{edge.DistanceMetres:F0}m"
            : $"{edge.DistanceMetres / 1000.0:F2}km";

        if (stepIndex == 0)
            return $"Head {direction} for {distText}.";
        if (stepIndex == totalSteps - 1)
            return $"Continue {direction} for {distText} to reach your destination.";

        string surfaceNote = edge.SurfaceType != "asphalt"
            ? $" (surface: {edge.SurfaceType})"
            : "";

        return $"Continue {direction} for {distText}{surfaceNote}.";
    }

    private static double CalculateBearing(Coordinate from, Coordinate to)
    {
        double dLon = ToRad(to.X - from.X);
        double lat1 = ToRad(from.Y);
        double lat2 = ToRad(to.Y);
        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2) -
                   Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        double brng = Math.Atan2(y, x);
        return (ToDeg(brng) + 360) % 360;
    }

    private static string BearingToCardinal(double bearing) => bearing switch
    {
        >= 337.5 or < 22.5 => "north",
        >= 22.5 and < 67.5 => "northeast",
        >= 67.5 and < 112.5 => "east",
        >= 112.5 and < 157.5 => "southeast",
        >= 157.5 and < 202.5 => "south",
        >= 202.5 and < 247.5 => "southwest",
        >= 247.5 and < 292.5 => "west",
        _ => "northwest"
    };

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}
