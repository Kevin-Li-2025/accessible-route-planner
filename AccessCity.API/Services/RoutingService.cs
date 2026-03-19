using AccessCity.API.Models;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services;

/// <summary>
/// Safety-aware A* routing engine backed by an imported route graph.
/// </summary>
public class RoutingService
{
    private readonly RiskScoringService _riskService;
    private readonly IRouteGraphRepository _routeGraphRepository;

    private const double WalkingSpeed = 1.3;

    private static readonly Dictionary<string, Func<GraphEdge, bool>> EdgeFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["avoid-stairs"] = e => !e.HasStairs,
        ["wheelchair"] = e => !e.HasStairs && e.SurfaceType != "cobblestone" && e.SurfaceType != "gravel",
        ["avoid-cobblestone"] = e => e.SurfaceType != "cobblestone",
        ["avoid-construction"] = e => !e.IsUnderConstruction,
        ["avoid-steep-hills"] = e => !e.IsSteep,
        ["avoid-reported-hazards"] = e => e.BaseSafetyCost < 0.3,
        ["prefer-crossings"] = _ => true,
    };

    private static readonly Dictionary<string, Func<GraphEdge, double>> CostModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low-light-penalty"] = e => 1.0 + (1.0 - e.LightingQuality) * 0.5,
        ["prefer-crossings"] = e => e.HasCrossing ? 0.85 : 1.15,
    };

    public RoutingService(RiskScoringService riskService, IRouteGraphRepository routeGraphRepository)
    {
        _riskService = riskService;
        _routeGraphRepository = routeGraphRepository;
    }

    public async Task<RouteResponse?> FindSafePathAsync(
        RouteRequest request,
        IEnumerable<HazardReport> allHazards,
        CancellationToken cancellationToken = default)
    {
        var directDist = RiskScoringService.HaversineDistance(
            request.Start.Y, request.Start.X,
            request.End.Y, request.End.X);

        if (directDist < 10)
        {
            return new RouteResponse
            {
                Path = new LineString(new[] { request.Start, request.End }),
                Distance = Math.Round(directDist, 1),
                EstimatedTime = Math.Round(directDist / WalkingSpeed, 0),
                SafetyScore = 1.0,
                Warnings = new List<string> { "Origin and destination are very close." }
            };
        }

        var graphData = await _routeGraphRepository.LoadGraphAsync(request.Start, request.End, cancellationToken);
        if (!graphData.HasCoverage)
        {
            return null;
        }

        var graph = graphData.Nodes;
        InjectVirtualNode(graph, 0, request.Start);
        InjectVirtualNode(graph, -1, request.End);

        var path = AStarSearch(graph, 0, -1, request, allHazards);
        if (path is null || path.Count < 2)
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

        return BuildResponse(path, graph, allHazards);
    }

    private List<long>? AStarSearch(
        Dictionary<long, GraphNode> graph,
        long startId,
        long endId,
        RouteRequest request,
        IEnumerable<HazardReport> hazards)
    {
        var hazardList = hazards.ToList();
        var endNode = graph[endId];
        var gScore = new Dictionary<long, double> { [startId] = 0 };
        var fScore = new Dictionary<long, double>
        {
            [startId] = Heuristic(graph[startId].Location, endNode.Location, request.SafetyWeight)
        };
        var cameFrom = new Dictionary<long, long>();
        var open = new PriorityQueue<long, double>();
        var closed = new HashSet<long>();

        open.Enqueue(startId, fScore[startId]);

        while (open.Count > 0)
        {
            var current = open.Dequeue();
            if (current == endId)
            {
                return ReconstructPath(cameFrom, current);
            }

            if (!closed.Add(current))
            {
                continue;
            }

            foreach (var (neighborId, edge) in graph[current].Edges)
            {
                if (closed.Contains(neighborId) || !graph.ContainsKey(neighborId))
                {
                    continue;
                }

                if (!PassesPreferenceFilters(request.Preferences, edge))
                {
                    continue;
                }

                var tentativeG = gScore[current] + ComputeEdgeCost(edge, graph[current], graph[neighborId], request, hazardList);
                if (tentativeG >= gScore.GetValueOrDefault(neighborId, double.MaxValue))
                {
                    continue;
                }

                cameFrom[neighborId] = current;
                gScore[neighborId] = tentativeG;
                var f = tentativeG + Heuristic(graph[neighborId].Location, endNode.Location, request.SafetyWeight);
                fScore[neighborId] = f;
                open.Enqueue(neighborId, f);
            }
        }

        return null;
    }

    private static bool PassesPreferenceFilters(IEnumerable<string> preferences, GraphEdge edge)
    {
        foreach (var preference in preferences)
        {
            if (EdgeFilters.TryGetValue(preference, out var filter) && !filter(edge))
            {
                return false;
            }
        }

        return true;
    }

    private static double Heuristic(Coordinate a, Coordinate b, double safetyWeight)
    {
        var distance = RiskScoringService.HaversineDistance(a.Y, a.X, b.Y, b.X);
        return distance * (1.0 - safetyWeight * 0.3);
    }

    private double ComputeEdgeCost(
        GraphEdge edge,
        GraphNode fromNode,
        GraphNode toNode,
        RouteRequest request,
        List<HazardReport> hazards)
    {
        var weight = Math.Clamp(request.SafetyWeight, 0.0, 1.0);
        var midLat = (fromNode.Location.Y + toNode.Location.Y) / 2.0;
        var midLon = (fromNode.Location.X + toNode.Location.X) / 2.0;
        var liveRisk = _riskService.QuickRisk(midLat, midLon, hazards, radiusMetres: 200);
        var safetyCost = (edge.BaseSafetyCost + liveRisk) / 2.0 * edge.DistanceMetres;

        var modifier = 1.0;
        foreach (var preference in request.Preferences)
        {
            if (CostModifiers.TryGetValue(preference, out var modifierFunc))
            {
                modifier *= modifierFunc(edge);
            }
        }

        var blended = ((1.0 - weight) * edge.DistanceMetres + weight * safetyCost) * modifier;
        return Math.Max(blended, 0.001);
    }

    private static List<long> ReconstructPath(Dictionary<long, long> cameFrom, long current)
    {
        var path = new List<long> { current };
        while (cameFrom.TryGetValue(current, out var previous))
        {
            current = previous;
            path.Add(current);
        }

        path.Reverse();
        return path;
    }

    private RouteResponse BuildResponse(
        List<long> path,
        Dictionary<long, GraphNode> graph,
        IEnumerable<HazardReport> hazards)
    {
        var hazardList = hazards.ToList();
        var coordinates = path.Select(id => graph[id].Location).ToArray();
        var lineString = new LineString(coordinates);
        var warnings = new List<string>();
        var steps = new List<RouteStep>();

        double totalDistance = 0;
        double safetySum = 0;

        for (var i = 0; i < path.Count - 1; i++)
        {
            var fromNode = graph[path[i]];
            var toNode = graph[path[i + 1]];
            var edge = fromNode.Edges[path[i + 1]];

            totalDistance += edge.DistanceMetres;
            var midLat = (fromNode.Location.Y + toNode.Location.Y) / 2.0;
            var midLon = (fromNode.Location.X + toNode.Location.X) / 2.0;
            var risk = _riskService.QuickRisk(midLat, midLon, hazardList);
            var safety = 1.0 - risk;
            safetySum += safety * edge.DistanceMetres;

            steps.Add(new RouteStep
            {
                From = new Point(fromNode.Location),
                To = new Point(toNode.Location),
                Distance = Math.Round(edge.DistanceMetres, 1),
                SafetyScore = Math.Round(safety, 3),
                Instruction = GenerateInstruction(fromNode, toNode, edge, i, path.Count - 1)
            });

            if (edge.HasStairs)
            {
                warnings.Add($"Step {i + 1}: This segment contains stairs.");
            }

            if (edge.LightingQuality < 0.3)
            {
                warnings.Add($"Step {i + 1}: Poor street lighting detected.");
            }

            if (edge.IsUnderConstruction)
            {
                warnings.Add($"Step {i + 1}: Active construction zone.");
            }

            if (risk > 0.7)
            {
                warnings.Add($"Step {i + 1}: Elevated risk area (score {risk:F2}).");
            }
        }

        var averageSafety = totalDistance > 0 ? safetySum / totalDistance : 1.0;

        return new RouteResponse
        {
            Path = lineString,
            Distance = Math.Round(totalDistance, 1),
            EstimatedTime = Math.Round(totalDistance / WalkingSpeed, 0),
            SafetyScore = Math.Round(averageSafety, 3),
            Warnings = warnings.Distinct().ToList(),
            Steps = steps
        };
    }

    private static string GenerateInstruction(GraphNode from, GraphNode to, GraphEdge edge, int stepIndex, int totalSteps)
    {
        var bearing = CalculateBearing(from.Location, to.Location);
        var direction = BearingToCardinal(bearing);
        var distanceText = edge.DistanceMetres < 100
            ? $"{edge.DistanceMetres:F0}m"
            : $"{edge.DistanceMetres / 1000.0:F2}km";

        if (stepIndex == 0)
        {
            return $"Head {direction} for {distanceText}.";
        }

        if (stepIndex == totalSteps - 1)
        {
            return $"Continue {direction} for {distanceText} to reach your destination.";
        }

        var surfaceNote = edge.SurfaceType != "asphalt"
            ? $" (surface: {edge.SurfaceType})"
            : string.Empty;

        return $"Continue {direction} for {distanceText}{surfaceNote}.";
    }

    private static double CalculateBearing(Coordinate from, Coordinate to)
    {
        var dLon = ToRad(to.X - from.X);
        var lat1 = ToRad(from.Y);
        var lat2 = ToRad(to.Y);
        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2) -
                Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        var bearing = Math.Atan2(y, x);
        return (ToDeg(bearing) + 360) % 360;
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

    private static void InjectVirtualNode(Dictionary<long, GraphNode> graph, long virtualId, Coordinate coordinate)
    {
        var virtualNode = new GraphNode
        {
            Id = virtualId,
            Location = coordinate
        };

        graph[virtualId] = virtualNode;

        var nearestNodeIds = graph.Values
            .Where(node => node.Id > 0)
            .OrderBy(node => RiskScoringService.HaversineDistance(coordinate.Y, coordinate.X, node.Location.Y, node.Location.X))
            .Take(6)
            .Select(node => node.Id)
            .ToList();

        foreach (var nearestNodeId in nearestNodeIds)
        {
            var node = graph[nearestNodeId];
            var distance = RiskScoringService.HaversineDistance(coordinate.Y, coordinate.X, node.Location.Y, node.Location.X);

            virtualNode.Edges[nearestNodeId] = new GraphEdge
            {
                TargetNodeId = nearestNodeId,
                DistanceMetres = distance,
                BaseSafetyCost = 0.1,
                SurfaceType = "asphalt",
                LightingQuality = 0.8
            };

            node.Edges[virtualId] = new GraphEdge
            {
                TargetNodeId = virtualId,
                DistanceMetres = distance,
                BaseSafetyCost = 0.1,
                SurfaceType = "asphalt",
                LightingQuality = 0.8
            };
        }
    }

    private static double ToRad(double degrees) => degrees * Math.PI / 180.0;
    private static double ToDeg(double radians) => radians * 180.0 / Math.PI;
}
