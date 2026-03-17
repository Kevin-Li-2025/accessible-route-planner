using NetTopologySuite.Geometries;
using AccessCity.API.Models;

namespace AccessCity.API.Services
{
    /// <summary>
    /// Safety-aware A* routing engine.
    /// 
    /// The engine constructs a street graph from PostGIS data (or, for the PoC,
    /// from an in-memory synthetic grid), then finds the optimal path by minimising
    /// a blended cost function:
    /// 
    ///     cost(e) = (1 − w) · distance(e)  +  w · risk(e)
    /// 
    /// where w ∈ [0,1] is the user's safety-weight preference.
    /// 
    /// Accessibility preferences (wheelchair, avoid-stairs, …) are applied as hard
    /// constraints that prune edges before the search begins.
    /// </summary>
    public class RoutingService
    {
        private readonly RiskScoringService _riskService;

        private const double WalkingSpeed = 1.3;

        private static readonly Dictionary<string, Func<GraphEdge, bool>> EdgeFilters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["avoid-stairs"]        = e => !e.HasStairs,
            ["wheelchair"]          = e => !e.HasStairs && e.SurfaceType != "cobblestone" && e.SurfaceType != "gravel",
            ["avoid-cobblestone"]   = e => e.SurfaceType != "cobblestone",
            ["avoid-construction"]  = e => !e.IsUnderConstruction,
            ["avoid-steep-hills"]   = e => !e.IsSteep,
            ["avoid-reported-hazards"] = e => e.BaseSafetyCost < 0.3,
            ["prefer-crossings"]    = e => true,
        };

        private static readonly Dictionary<string, Func<GraphEdge, double>> CostModifiers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["low-light-penalty"]  = e => 1.0 + (1.0 - e.LightingQuality) * 0.5,
            ["prefer-crossings"]   = e => e.HasCrossing ? 0.85 : 1.15,
        };

        public RoutingService(RiskScoringService riskService)
        {
            _riskService = riskService;
        }

        /// <summary>
        /// Compute the safest / most accessible route from start to end.
        /// </summary>
        public RouteResponse FindSafePath(
            RouteRequest request,
            IEnumerable<HazardReport> allHazards)
        {
            double directDist = RiskScoringService.HaversineDistance(
                request.Start.Y, request.Start.X,
                request.End.Y, request.End.X);

            // Optimization for very long distances to avoid memory overflow in synthetic grid
            double latStep = 0.0007;
            double lonStep = 0.0010;

            if (directDist > 10000) // > 10km
            {
                latStep = 0.005; // ~500m
                lonStep = 0.007;
            }
            if (directDist > 50000) // > 50km
            {
                latStep = 0.02; // ~2.2km
                lonStep = 0.03;
            }

            var graph = BuildGraph(request.Start, request.End, allHazards, latStep, lonStep);
            long startId = FindNearest(graph, request.Start);
            long endId   = FindNearest(graph, request.End);

            if (startId == endId)
            {
                return new RouteResponse
                {
                    Path        = new LineString(new[] { request.Start, request.End }),
                    Distance    = RiskScoringService.HaversineDistance(
                                      request.Start.Y, request.Start.X,
                                      request.End.Y,   request.End.X),
                    SafetyScore = 1.0,
                    Warnings    = new List<string> { "Origin and destination are very close." }
                };
            }

            var path = AStarSearch(graph, startId, endId, request, allHazards);

            if (path == null || path.Count < 2)
            {
                return new RouteResponse
                {
                    SafetyScore = 0,
                    Warnings    = new List<string>
                    {
                        "No accessible route found. Try relaxing your accessibility preferences."
                    }
                };
            }

            return BuildResponse(path, graph, request, allHazards);
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
                                                       request, hazardList);

                    double tentativeG = gScore[current] + edgeCost;

                    if (tentativeG < gScore.GetValueOrDefault(neighbourId, double.MaxValue))
                    {
                        cameFrom[neighbourId] = current;
                        gScore[neighbourId]   = tentativeG;
                        double f = tentativeG +
                                   Heuristic(graph[neighbourId].Location, endNode.Location, request.SafetyWeight);
                        fScore[neighbourId] = f;
                        open.Enqueue(neighbourId, f);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Admissible heuristic: straight-line Haversine distance, downscaled by
        /// safety weight so the search explores safer detours when w is high.
        /// </summary>
        private static double Heuristic(Coordinate a, Coordinate b, double safetyWeight)
        {
            double dist = RiskScoringService.HaversineDistance(a.Y, a.X, b.Y, b.X);
            return dist * (1.0 - safetyWeight * 0.3);
        }

        /// <summary>
        /// Blended edge cost combining distance, hazard risk, and preference modifiers.
        /// </summary>
        private double ComputeEdgeCost(
            GraphEdge edge,
            GraphNode fromNode,
            GraphNode toNode,
            RouteRequest request,
            List<HazardReport> hazards)
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

        private RouteResponse BuildResponse(
            List<long> path,
            Dictionary<long, GraphNode> graph,
            RouteRequest request,
            IEnumerable<HazardReport> hazards)
        {
            var hazardList  = hazards.ToList();
            var coordinates = path.Select(id => graph[id].Location).ToArray();
            var lineString  = new LineString(coordinates);

            double totalDist   = 0;
            double safetySum   = 0;
            var steps          = new List<RouteStep>();
            var warnings       = new List<string>();

            for (int i = 0; i < path.Count - 1; i++)
            {
                var fromNode = graph[path[i]];
                var toNode   = graph[path[i + 1]];
                var edge     = fromNode.Edges[path[i + 1]];

                double segDist = edge.DistanceMetres;
                totalDist += segDist;

                double midLat = (fromNode.Location.Y + toNode.Location.Y) / 2.0;
                double midLon = (fromNode.Location.X + toNode.Location.X) / 2.0;
                double segRisk = _riskService.QuickRisk(midLat, midLon, hazardList);
                double segSafety = 1.0 - segRisk;
                safetySum += segSafety * segDist;

                string instruction = GenerateInstruction(fromNode, toNode, edge, i, path.Count - 1);

                steps.Add(new RouteStep
                {
                    From        = new Point(fromNode.Location),
                    To          = new Point(toNode.Location),
                    Distance    = Math.Round(segDist, 1),
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

            return new RouteResponse
            {
                Path          = lineString,
                Distance      = Math.Round(totalDist, 1),
                EstimatedTime = Math.Round(totalDist / WalkingSpeed, 0),
                SafetyScore   = Math.Round(avgSafety, 3),
                Warnings      = warnings.Distinct().ToList(),
                Steps         = steps
            };
        }

        /// <summary>
        /// Generate a human-readable turn-by-turn instruction for a segment.
        /// </summary>
        private static string GenerateInstruction(
            GraphNode from, GraphNode to, GraphEdge edge  , int stepIndex, int totalSteps)
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

        /// <summary>
        /// Build a walkable routing graph.
        /// 
        /// In production this queries PostGIS for the road / footpath network
        /// (OpenStreetMap imports via osm2pgsql or pg_routing).
        /// 
        /// For the PoC we generate a fine-grained grid around the bounding box
        /// of start ↔ end, with hazard-aware edge weights.
        /// </summary>
        private Dictionary<long, GraphNode> BuildGraph(
            Coordinate start, Coordinate end,
            IEnumerable<HazardReport> hazards,
            double latStep = 0.0007,
            double lonStep = 0.0010)
        {
            var hazardList = hazards.ToList();
            double minLat = Math.Min(start.Y, end.Y) - latStep * 5; 
            double maxLat = Math.Max(start.Y, end.Y) + latStep * 5;
            double minLon = Math.Min(start.X, end.X) - lonStep * 5;
            double maxLon = Math.Max(start.X, end.X) + lonStep * 5;
            
            // Limit total nodes to prevent OOM
            int rows = Math.Min((int)Math.Ceiling((maxLat - minLat) / latStep), 150);
            int cols = Math.Min((int)Math.Ceiling((maxLon - minLon) / lonStep), 150);
            
            // Adjust back max to fit rows/cols if capped
            maxLat = minLat + rows * latStep;
            maxLon = minLon + cols * lonStep;

            var graph = new Dictionary<long, GraphNode>();
            var coordToId = new Dictionary<(int row, int col), long>();
            long nextId = 1;
            for (int r = 0; r <= rows; r++)
            {
                for (int c = 0; c <= cols; c++)
                {
                    double lat = minLat + r * latStep;
                    double lon = minLon + c * lonStep;
                    long id = nextId++;

                    graph[id] = new GraphNode
                    {
                        Id       = id,
                        Location = new Coordinate(lon, lat)
                    };
                    coordToId[(r, c)] = id;
                }
            }
            int[] dr = { -1, -1, -1,  0, 0,  1, 1, 1 };
            int[] dc = { -1,  0,  1, -1, 1, -1, 0, 1 };

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
                            toNode.Location.Y,   toNode.Location.X);
                        double midLat = (fromNode.Location.Y + toNode.Location.Y) / 2.0;
                        double midLon = (fromNode.Location.X + toNode.Location.X) / 2.0;
                        double riskAtMid = _riskService.QuickRisk(midLat, midLon, hazardList, 150);
                        int seed = HashCode.Combine(
                            Math.Round(midLat, 5),
                            Math.Round(midLon, 5));
                        var localRng = new Random(seed);

                        string surface = localRng.NextDouble() switch
                        {
                            < 0.70 => "asphalt",
                            < 0.85 => "paving_stones",
                            < 0.92 => "cobblestone",
                            < 0.97 => "gravel",
                            _      => "unpaved"
                        };

                        bool hasStairs       = localRng.NextDouble() < 0.03;
                        bool hasCrossing     = localRng.NextDouble() < 0.25;
                        bool underConstruct  = localRng.NextDouble() < 0.02;
                        bool isSteep         = localRng.NextDouble() < 0.05;
                        double lighting      = 0.4 + localRng.NextDouble() * 0.6;

                        fromNode.Edges[toId] = new GraphEdge
                        {
                            TargetNodeId        = toId,
                            DistanceMetres      = dist,
                            BaseSafetyCost      = riskAtMid,
                            SurfaceType         = surface,
                            HasStairs           = hasStairs,
                            HasCrossing         = hasCrossing,
                            IsUnderConstruction = underConstruct,
                            LightingQuality     = lighting,
                            IsSteep             = isSteep
                        };
                    }
                }
            }
            InjectVirtualNode(graph, 0, start, coordToId, rows, cols, latStep, lonStep, minLat, minLon, hazardList);
            InjectVirtualNode(graph, -1, end,  coordToId, rows, cols, latStep, lonStep, minLat, minLon, hazardList);

            return graph;
        }

        /// <summary>
        /// Insert a virtual node (start or end point) and connect it to
        /// its 4 nearest grid nodes so the A* can reach it.
        /// </summary>
        private void InjectVirtualNode(
            Dictionary<long, GraphNode> graph,
            long virtualId,
            Coordinate coord,
            Dictionary<(int, int), long> coordToId,
            int rows, int cols,
            double latStep, double lonStep,
            double minLat, double minLon,
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
                        coord.Y, coord.X,
                        gridNode.Location.Y, gridNode.Location.X);

                    var edge = new GraphEdge
                    {
                        TargetNodeId   = gridId,
                        DistanceMetres = dist,
                        BaseSafetyCost = 0.1,
                        SurfaceType    = "asphalt",
                        LightingQuality = 0.8
                    };
                    vNode.Edges[gridId]      = edge;
                    gridNode.Edges[virtualId] = new GraphEdge
                    {
                        TargetNodeId   = virtualId,
                        DistanceMetres = dist,
                        BaseSafetyCost = 0.1,
                        SurfaceType    = "asphalt",
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
    }
}
