using AccessCity.API.Models;
using AccessCity.API.Services.External;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services;

/// <summary>
/// Safety-aware routing engine using OSRM for real road-following routes.
/// 
/// Strategy:
///   1. Get a real road route from OSRM (foot profile)
///   2. Check if route passes near reported hazards
///   3. If hazards are close, compute avoidance waypoints and re-query OSRM
///   4. Score each segment for safety using RiskScoringService
///   5. Fallback to synthetic grid if OSRM is unavailable
/// </summary>
public class RoutingService
{
    private readonly RiskScoringService _riskService;
    private readonly PredictiveRiskModel _aiRisk;
    private readonly IOsrmClient _osrmClient;

    private const double WalkingSpeed = 1.3;
    private const double HazardAvoidanceRadiusMetres = 50.0;
    private const double HazardWaypointOffsetMetres = 100.0;

    public RoutingService(RiskScoringService riskService, PredictiveRiskModel aiRisk, IOsrmClient osrmClient)
    {
        _riskService = riskService;
        _aiRisk = aiRisk;
        _osrmClient = osrmClient;
    }

    /// <summary>
    /// Compute the safest / most accessible route from start to end.
    /// Uses OSRM for real road geometry, with hazard-aware rerouting.
    /// </summary>
    public async Task<RouteResponse> FindSafePathAsync(
        RouteRequest request,
        IEnumerable<HazardReport> allHazards)
    {
        var hazardList = allHazards
            .Where(h => h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview)
            .ToList();

        // Step 1: Proactively try OSRM with alternatives
        var alternatives = await _osrmClient.GetAlternativeRoutesAsync(request.Start, request.End);

        if (alternatives != null && alternatives.Count > 0)
        {
            var scoredRoutes = alternatives.Select(r => new
            {
                Route = r,
                Cost = ScoreRoute(r, hazardList, request.SafetyWeight)
            })
            .OrderBy(x => x.Cost)
            .ToList();

            var bestRoute = scoredRoutes.First().Route;

            // Step 2: If the best OSRM route still has severe hazards, try waypoint-based avoidance
            var severeHazards = FindHazardsNearRoute(bestRoute.Coordinates, hazardList);
            if (severeHazards.Count > 0 && request.SafetyWeight > 0.3)
            {
                var rerouted = await AttemptWaypointRerouteAsync(request, bestRoute, severeHazards, hazardList);
                if (rerouted != null)
                {
                    double rerouteCost = ScoreRoute(rerouted, hazardList, request.SafetyWeight);
                    if (rerouteCost < scoredRoutes.First().Cost)
                        bestRoute = rerouted;
                }
            }

            return BuildOsrmResponse(bestRoute, hazardList, request);
        }

        // Fallback: synthetic grid (for when OSRM is unavailable)
        return FindSafePathFallback(request, hazardList);
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

    // ──────── OSRM-based routing ────────

    /// <summary>
    /// Find hazards that are dangerously close to the route path.
    /// </summary>
    private List<HazardReport> FindHazardsNearRoute(
        List<Coordinate> routeCoords,
        List<HazardReport> hazards)
    {
        var result = new List<HazardReport>();

        foreach (var hazard in hazards)
        {
            double minDist = double.MaxValue;

            // Sample every Nth point for performance (routes can have 100s of points)
            int step = Math.Max(1, routeCoords.Count / 50);
            for (int i = 0; i < routeCoords.Count; i += step)
            {
                double dist = RiskScoringService.HaversineDistance(
                    routeCoords[i].Y, routeCoords[i].X,
                    hazard.Location.Y, hazard.Location.X);

                if (dist < minDist) minDist = dist;
                if (minDist < HazardAvoidanceRadiusMetres) break;
            }

            if (minDist < HazardAvoidanceRadiusMetres)
                result.Add(hazard);
        }

        return result;
    }

    private async Task<OsrmRouteResult?> AttemptWaypointRerouteAsync(
        RouteRequest request,
        OsrmRouteResult primaryRoute,
        List<HazardReport> nearbyHazards,
        List<HazardReport> allHazards)
    {
        var waypoints = ComputeAvoidanceWaypoints(primaryRoute.Coordinates, nearbyHazards);
        if (waypoints.Count > 0)
        {
            var rerouted = await _osrmClient.GetRouteAsync(request.Start, request.End, waypoints);
            if (rerouted != null && rerouted.Coordinates.Count >= 2)
            {
                // Verify the reroute is reasonable (not > 2× the original distance)
                if (rerouted.DistanceMetres < primaryRoute.DistanceMetres * 2.0)
                    return rerouted;
            }
        }

        return null;
    }

    private double ScoreRoute(OsrmRouteResult route, List<HazardReport> hazards, double safetyWeight)
    {
        double normalizedDist = route.DistanceMetres / 1000.0; // km
        double totalRisk = ComputeRouteTotalRisk(route.Coordinates, hazards);
        
        // Balanced score: 0 is perfect, higher is worse
        // We normalize risk by number of points sampled in ComputeRouteTotalRisk (roughly 30)
        double normalizedRisk = totalRisk / 30.0;

        return (normalizedDist * (1.0 - safetyWeight)) + (normalizedRisk * safetyWeight * 5.0);
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
            // Find the closest point on the route to this hazard
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

            // Compute perpendicular offset direction
            int prevIdx = Math.Max(0, closestIdx - 1);
            int nextIdx = Math.Min(routeCoords.Count - 1, closestIdx + 1);

            double dLon = routeCoords[nextIdx].X - routeCoords[prevIdx].X;
            double dLat = routeCoords[nextIdx].Y - routeCoords[prevIdx].Y;

            // Perpendicular direction (rotate 90 degrees)
            double perpLon = -dLat;
            double perpLat = dLon;
            double perpLen = Math.Sqrt(perpLon * perpLon + perpLat * perpLat);

            if (perpLen < 1e-10) continue;

            perpLon /= perpLen;
            perpLat /= perpLen;

            // Offset in degrees (approximate: 1 degree lat ≈ 111,320m)
            double offsetDegLat = (HazardWaypointOffsetMetres / 111320.0) * perpLat;
            double offsetDegLon = (HazardWaypointOffsetMetres /
                (111320.0 * Math.Cos(hazard.Location.Y * Math.PI / 180.0))) * perpLon;

            // Choose the offset direction AWAY from the hazard
            double waypointLon1 = routeCoords[closestIdx].X + offsetDegLon;
            double waypointLat1 = routeCoords[closestIdx].Y + offsetDegLat;
            double waypointLon2 = routeCoords[closestIdx].X - offsetDegLon;
            double waypointLat2 = routeCoords[closestIdx].Y - offsetDegLat;

            double dist1 = RiskScoringService.HaversineDistance(
                waypointLat1, waypointLon1,
                hazard.Location.Y, hazard.Location.X);
            double dist2 = RiskScoringService.HaversineDistance(
                waypointLat2, waypointLon2,
                hazard.Location.Y, hazard.Location.X);

            // Pick the waypoint that is farther from the hazard
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
    /// Build the standard RouteResponse from an OSRM result.
    /// </summary>
    private RouteResponse BuildOsrmResponse(
        OsrmRouteResult osrmRoute,
        List<HazardReport> hazards,
        RouteRequest request)
    {
        var coordinates = osrmRoute.Coordinates.ToArray();
        var lineString = new LineString(coordinates);

        var steps = new List<RouteStep>();
        var warnings = new List<string>();
        double safetySum = 0;
        double totalDist = osrmRoute.DistanceMetres;

        // Build steps from OSRM step data
        if (osrmRoute.Steps.Count > 0)
        {
            int stepIdx = 0;
            foreach (var osrmStep in osrmRoute.Steps)
            {
                if (osrmStep.Geometry.Count < 2) continue;
                if (osrmStep.Distance < 0.1) continue; // Skip zero-distance steps

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

                // Generate warnings for segments near hazards
                if (segRisk > 0.5)
                    warnings.Add($"Step {stepIdx + 1}: Elevated risk area (score {segRisk:F2}).");

                // Check for specific hazard types nearby
                foreach (var hazard in hazards)
                {
                    double dist = RiskScoringService.HaversineDistance(
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

                stepIdx++;
            }
        }
        else
        {
            // No detailed steps — build from raw coordinates
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
            EstimatedTime = Math.Round(osrmRoute.DurationSeconds, 0),
            SafetyScore = Math.Round(Math.Clamp(avgSafety, 0, 1), 3),
            Warnings = warnings.Distinct().ToList(),
            Steps = steps
        };
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

    // ──────── Fallback: Synthetic Grid (kept for resilience) ────────

    private static readonly Dictionary<string, Func<GraphEdge, bool>> EdgeFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["avoid-stairs"] = e => !e.HasStairs,
        ["wheelchair"] = e => !e.HasStairs && e.SurfaceType != "cobblestone" && e.SurfaceType != "gravel",
        ["avoid-cobblestone"] = e => e.SurfaceType != "cobblestone",
        ["avoid-construction"] = e => !e.IsUnderConstruction,
        ["avoid-steep-hills"] = e => !e.IsSteep,
        ["avoid-reported-hazards"] = e => e.BaseSafetyCost < 0.3,
        ["prefer-crossings"] = e => true,
    };

    private static readonly Dictionary<string, Func<GraphEdge, double>> CostModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low-light-penalty"] = e => 1.0 + (1.0 - e.LightingQuality) * 0.5,
        ["prefer-crossings"] = e => e.HasCrossing ? 0.85 : 1.15,
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
                SafetyScore = 1.0,
                Warnings = new List<string> { "Origin and destination are very close." }
            };
        }

        var path = AStarSearch(graph, startId, endId, request, hazardList);

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

        return BuildFallbackResponse(path, graph, request, hazardList);
    }

    private List<long>? AStarSearch(
        Dictionary<long, GraphNode> graph,
        long startId, long endId,
        RouteRequest request,
        List<HazardReport> hazards)
    {
        var endNode = graph[endId];
        var gScore = new Dictionary<long, double> { [startId] = 0 };
        var fScore = new Dictionary<long, double>
        {
            [startId] = Heuristic(graph[startId].Location, endNode.Location, request.SafetyWeight)
        };
        var cameFrom = new Dictionary<long, long>();
        var open = new PriorityQueue<long, double>();
        open.Enqueue(startId, fScore[startId]);
        var closed = new HashSet<long>();

        while (open.Count > 0)
        {
            long current = open.Dequeue();

            if (current == endId)
                return ReconstructPath(cameFrom, current);

            if (!closed.Add(current)) continue;

            var currentNode = graph[current];

            foreach (var (neighbourId, edge) in currentNode.Edges)
            {
                if (closed.Contains(neighbourId)) continue;
                if (!graph.ContainsKey(neighbourId)) continue;

                bool passesFilters = true;
                foreach (var pref in request.Preferences)
                {
                    if (EdgeFilters.TryGetValue(pref, out var filter) && !filter(edge))
                    {
                        passesFilters = false;
                        break;
                    }
                }
                if (!passesFilters) continue;

                double edgeCost = ComputeEdgeCost(edge, currentNode, graph[neighbourId],
                                                   request, hazards);
                double tentativeG = gScore[current] + edgeCost;

                if (tentativeG < gScore.GetValueOrDefault(neighbourId, double.MaxValue))
                {
                    cameFrom[neighbourId] = current;
                    gScore[neighbourId] = tentativeG;
                    double f = tentativeG +
                               Heuristic(graph[neighbourId].Location, endNode.Location, request.SafetyWeight);
                    fScore[neighbourId] = f;
                    open.Enqueue(neighbourId, f);
                }
            }
        }

        return null;
    }

    private static double Heuristic(Coordinate a, Coordinate b, double safetyWeight)
    {
        double dist = RiskScoringService.HaversineDistance(a.Y, a.X, b.Y, b.X);
        return dist * (1.0 - safetyWeight * 0.3);
    }

    private double ComputeEdgeCost(
        GraphEdge edge, GraphNode fromNode, GraphNode toNode,
        RouteRequest request, List<HazardReport> hazards)
    {
        double w = Math.Clamp(request.SafetyWeight, 0.0, 1.0);
        double distCost = edge.DistanceMetres;
        double midLat = (fromNode.Location.Y + toNode.Location.Y) / 2.0;
        double midLon = (fromNode.Location.X + toNode.Location.X) / 2.0;

        double liveRisk = _riskService.QuickRisk(midLat, midLon, hazards, radiusMetres: 200);
        double safetyCost = (edge.BaseSafetyCost + liveRisk) / 2.0 * edge.DistanceMetres;

        double modifier = 1.0;
        foreach (var pref in request.Preferences)
        {
            if (CostModifiers.TryGetValue(pref, out var fn))
                modifier *= fn(edge);
        }

        double blended = ((1.0 - w) * distCost + w * safetyCost) * modifier;
        return Math.Max(blended, 0.001);
    }

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
            "Real road calculation is unavailable for this distance/area. An approximate straight-path mesh is shown." 
        };

        for (int i = 0; i < path.Count - 1; i++)
        {
            var fromNode = graph[path[i]];
            var toNode = graph[path[i + 1]];
            var edge = fromNode.Edges[path[i + 1]];

            double segDist = edge.DistanceMetres;
            totalDist += segDist;

            double midLat = (fromNode.Location.Y + toNode.Location.Y) / 2.0;
            double midLon = (fromNode.Location.X + toNode.Location.X) / 2.0;
            double segRisk = _riskService.QuickRisk(midLat, midLon, hazards);
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
                warnings.Add($"Step {i + 1}: Active construction zone \u2014 proceed with caution.");
            if (segRisk > 0.7)
                warnings.Add($"Step {i + 1}: Elevated risk area (score {segRisk:F2}).");
        }

        double avgSafety = totalDist > 0 ? safetySum / totalDist : 1.0;

        return new RouteResponse
        {
            Path = lineString,
            Distance = Math.Round(totalDist, 1),
            EstimatedTime = Math.Round(totalDist / WalkingSpeed, 0),
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
        var rng = new Random(42);

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
                    double riskAtMid = _riskService.QuickRisk(midLat, midLon, hazards, 150);

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
        >= 337.5 or < 22.5   => "north",
        >= 22.5  and < 67.5  => "northeast",
        >= 67.5  and < 112.5 => "east",
        >= 112.5 and < 157.5 => "southeast",
        >= 157.5 and < 202.5 => "south",
        >= 202.5 and < 247.5 => "southwest",
        >= 247.5 and < 292.5 => "west",
        _                    => "northwest"
    };

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}
