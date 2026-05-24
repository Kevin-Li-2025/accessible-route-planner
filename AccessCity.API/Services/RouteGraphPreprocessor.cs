using AccessCity.API.Configuration;
using AccessCity.API.Models;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services;

public static class RouteGraphPreprocessor
{
    public const int AltAlgorithmVersion = 1;
    public const string AltWeightVersion = "min-traversal-seconds-v1";
    public const double MaxLowerBoundSpeedMetresPerSecond = 2.0;
    private const double LandmarkDistanceQuantizationSafetySeconds = 0.001;
    private const double StaleQueueDistanceToleranceSeconds = 1e-9;

    public static void TryAttachPreprocessing(RouteGraphData graphData, RoutingOptions options)
    {
        graphData.Preprocessing = BuildAltPreprocessing(graphData, options);

        if (options.RouteGraphContractionHierarchyEnabled
            && graphData.Nodes.Count >= 2
            && graphData.Nodes.Count <= Math.Max(1, options.RouteGraphMaxContractionHierarchyNodes))
        {
            graphData.ContractionHierarchies = ContractionHierarchy.BuildForAllProfiles(graphData.Nodes);
        }
    }

    public static RouteGraphPreprocessingData? BuildAltPreprocessing(RouteGraphData graphData, RoutingOptions options)
    {
        if (!options.RouteGraphAltPreprocessingEnabled
            || graphData.IsTruncated
            || graphData.Nodes.Count < 2
            || graphData.Nodes.Count > Math.Max(1, options.RouteGraphMaxAltPreprocessedNodes))
        {
            return null;
        }

        var landmarkCount = Math.Clamp(options.RouteGraphAltLandmarkCount, 0, 16);
        if (landmarkCount == 0)
        {
            return null;
        }

        var nodes = graphData.Nodes.Values
            .OrderBy(node => node.Id)
            .ToArray();
        var nodeIndexById = new Dictionary<long, int>(nodes.Length);
        for (var i = 0; i < nodes.Length; i++)
        {
            nodeIndexById[nodes[i].Id] = i;
        }

        var landmarks = SelectLandmarks(nodes, landmarkCount);
        if (landmarks.Count == 0)
        {
            return null;
        }

        var forwardAdjacency = BuildForwardAdjacency(nodes, nodeIndexById);
        var reverseAdjacency = BuildReverseAdjacency(forwardAdjacency);
        var forward = new List<double[]>(landmarks.Count);
        var reverse = new List<double[]>(landmarks.Count);

        foreach (var landmarkId in landmarks)
        {
            if (!nodeIndexById.TryGetValue(landmarkId, out var landmarkIndex))
            {
                continue;
            }

            forward.Add(RunDijkstra(forwardAdjacency, landmarkIndex));
            reverse.Add(RunDijkstra(reverseAdjacency, landmarkIndex));
        }

        if (forward.Count == 0 || reverse.Count == 0)
        {
            return null;
        }

        var effectiveLandmarks = landmarks.Take(forward.Count).ToArray();
        var nodeDistances = new Dictionary<long, RouteGraphNodePreprocessing>(nodes.Length);
        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            var fromLandmark = new float[effectiveLandmarks.Length];
            var toLandmark = new float[effectiveLandmarks.Length];
            for (var i = 0; i < effectiveLandmarks.Length; i++)
            {
                fromLandmark[i] = EncodePreprocessedSeconds(forward[i][nodeIndex]);
                toLandmark[i] = EncodePreprocessedSeconds(reverse[i][nodeIndex]);
            }

            nodeDistances[nodes[nodeIndex].Id] = new RouteGraphNodePreprocessing
            {
                FromLandmarkSeconds = fromLandmark,
                ToLandmarkSeconds = toLandmark
            };
        }

        return new RouteGraphPreprocessingData
        {
            Algorithm = "ALT",
            AlgorithmVersion = AltAlgorithmVersion,
            WeightVersion = AltWeightVersion,
            LandmarkNodeIds = effectiveLandmarks,
            NodeDistances = nodeDistances
        };
    }

    public static double ComputeAltLowerBoundSeconds(
        RouteGraphPreprocessingData? preprocessing,
        long fromNodeId,
        long targetNodeId)
    {
        if (preprocessing?.HasLandmarks != true
            || !string.Equals(preprocessing.Algorithm, "ALT", StringComparison.Ordinal)
            || preprocessing.AlgorithmVersion != AltAlgorithmVersion
            || !string.Equals(preprocessing.WeightVersion, AltWeightVersion, StringComparison.Ordinal)
            || !preprocessing.NodeDistances.TryGetValue(fromNodeId, out var from)
            || !preprocessing.NodeDistances.TryGetValue(targetNodeId, out var target))
        {
            return 0;
        }

        var landmarkCount = Math.Min(
            preprocessing.LandmarkNodeIds.Length,
            Math.Min(
                Math.Min(from.FromLandmarkSeconds.Length, from.ToLandmarkSeconds.Length),
                Math.Min(target.FromLandmarkSeconds.Length, target.ToLandmarkSeconds.Length)));

        var lowerBound = 0.0;
        for (var i = 0; i < landmarkCount; i++)
        {
            lowerBound = Math.Max(lowerBound, DifferenceIfFinite(target.FromLandmarkSeconds[i], from.FromLandmarkSeconds[i]));
            lowerBound = Math.Max(lowerBound, DifferenceIfFinite(from.ToLandmarkSeconds[i], target.ToLandmarkSeconds[i]));
        }

        return lowerBound;
    }

    private static double DifferenceIfFinite(float a, float b)
    {
        if (!float.IsFinite(a) || !float.IsFinite(b))
        {
            return 0;
        }

        var safetyMarginSeconds = LandmarkDistanceQuantizationSafetySeconds
                                  + FloatRoundoffSafetySeconds(a)
                                  + FloatRoundoffSafetySeconds(b);
        return Math.Max(0, (double)a - b - safetyMarginSeconds);
    }

    private static float EncodePreprocessedSeconds(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 ? (float)Math.Round(seconds, 3) : float.PositiveInfinity;

    private static double FloatRoundoffSafetySeconds(float seconds)
    {
        var next = MathF.BitIncrement(seconds);
        return float.IsFinite(next) ? Math.Abs((double)next - seconds) : 0;
    }

    private static IReadOnlyList<long> SelectLandmarks(IReadOnlyList<GraphNode> ordered, int landmarkCount)
    {
        if (ordered.Count == 0)
        {
            return Array.Empty<long>();
        }

        var minX = ordered.Min(node => node.Location.X);
        var maxX = ordered.Max(node => node.Location.X);
        var minY = ordered.Min(node => node.Location.Y);
        var maxY = ordered.Max(node => node.Location.Y);
        var targets = new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(minX, maxY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate((minX + maxX) / 2.0, (minY + maxY) / 2.0)
        };

        var selected = new List<GraphNode>(landmarkCount);
        foreach (var target in targets)
        {
            if (selected.Count >= landmarkCount)
            {
                break;
            }

            var nearest = ordered
                .Where(node => selected.All(existing => existing.Id != node.Id))
                .MinBy(node => SquaredDistance(node.Location, target));
            if (nearest is not null)
            {
                selected.Add(nearest);
            }
        }

        while (selected.Count < Math.Min(landmarkCount, ordered.Count))
        {
            var next = ordered
                .Where(node => selected.All(existing => existing.Id != node.Id))
                .MaxBy(node => selected.Min(existing => SquaredDistance(node.Location, existing.Location)));
            if (next is null)
            {
                break;
            }

            selected.Add(next);
        }

        return selected
            .Select(node => node.Id)
            .ToArray();
    }

    private static double SquaredDistance(Coordinate a, Coordinate b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static PreprocessingEdge[][] BuildForwardAdjacency(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyDictionary<long, int> nodeIndexById)
    {
        var adjacency = new PreprocessingEdge[nodes.Count][];
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            var edges = new List<PreprocessingEdge>(node.Edges.Count);
            foreach (var edge in node.Edges.Values)
            {
                if (nodeIndexById.TryGetValue(edge.TargetNodeId, out var targetIndex))
                {
                    edges.Add(new PreprocessingEdge(targetIndex, LowerBoundTraversalSeconds(edge)));
                }
            }

            adjacency[i] = edges.ToArray();
        }

        return adjacency;
    }

    private static PreprocessingEdge[][] BuildReverseAdjacency(IReadOnlyList<PreprocessingEdge[]> forwardAdjacency)
    {
        var reverse = new List<PreprocessingEdge>[forwardAdjacency.Count];
        for (var i = 0; i < reverse.Length; i++)
        {
            reverse[i] = new List<PreprocessingEdge>();
        }

        for (var sourceIndex = 0; sourceIndex < forwardAdjacency.Count; sourceIndex++)
        {
            foreach (var edge in forwardAdjacency[sourceIndex])
            {
                reverse[edge.TargetIndex].Add(new PreprocessingEdge(sourceIndex, edge.CostSeconds));
            }
        }

        var adjacency = new PreprocessingEdge[reverse.Length][];
        for (var i = 0; i < reverse.Length; i++)
        {
            adjacency[i] = reverse[i].ToArray();
        }

        return adjacency;
    }

    private static double[] RunDijkstra(IReadOnlyList<PreprocessingEdge[]> adjacency, int startIndex)
    {
        var distances = new double[adjacency.Count];
        Array.Fill(distances, double.PositiveInfinity);
        distances[startIndex] = 0;

        var queue = new PriorityQueue<(int NodeIndex, double Distance), double>();
        queue.Enqueue((startIndex, 0), 0);

        while (queue.Count > 0)
        {
            var (current, currentDistance) = queue.Dequeue();
            if (currentDistance > distances[current] + StaleQueueDistanceToleranceSeconds)
            {
                continue;
            }

            foreach (var edge in adjacency[current])
            {
                var tentative = currentDistance + edge.CostSeconds;
                if (tentative >= distances[edge.TargetIndex])
                {
                    continue;
                }

                distances[edge.TargetIndex] = tentative;
                queue.Enqueue((edge.TargetIndex, tentative), tentative);
            }
        }

        return distances;
    }

    private readonly record struct PreprocessingEdge(int TargetIndex, double CostSeconds);

    private static double LowerBoundTraversalSeconds(GraphEdge edge)
        => Math.Max(0.001, edge.DistanceMetres / MaxLowerBoundSpeedMetresPerSecond);
}
