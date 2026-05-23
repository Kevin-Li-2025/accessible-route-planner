using AccessCity.API.Models;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services;

public static class RouteGraphArtifactCodec
{
    public const string SchemaVersion = "packed-route-graph-v1";

    public static PackedRouteGraphArtifact Pack(RouteGraphData graphData)
    {
        var nodes = graphData.Nodes.Values
            .OrderBy(node => node.Id)
            .ToArray();
        var packedNodes = new List<PackedRouteGraphNode>(nodes.Length);
        var packedEdges = new List<PackedRouteGraphEdge>(Math.Max(0, graphData.LoadedEdgeCount));

        foreach (var node in nodes)
        {
            var firstEdgeIndex = packedEdges.Count;
            foreach (var edge in node.Edges.Values.OrderBy(edge => edge.TargetNodeId))
            {
                var edgeWithWeights = EnsureTraversalWeights(edge);
                packedEdges.Add(new PackedRouteGraphEdge(
                    edgeWithWeights.TargetNodeId,
                    edgeWithWeights.DistanceMetres,
                    edgeWithWeights.BaseSafetyCost,
                    edgeWithWeights.SurfaceType,
                    edgeWithWeights.HasStairs,
                    edgeWithWeights.HasCrossing,
                    edgeWithWeights.IsUnderConstruction,
                    edgeWithWeights.LightingQuality,
                    edgeWithWeights.IsSteep,
                    edgeWithWeights.KerbHeight,
                    edgeWithWeights.Smoothness,
                    edgeWithWeights.WidthMetres,
                    edgeWithWeights.HasTactilePaving,
                    edgeWithWeights.HasBarrier,
                    edgeWithWeights.Access,
                    edgeWithWeights.AccessibilityCostVersion,
                    edgeWithWeights.StandardAccessibilityPenaltySeconds,
                    edgeWithWeights.WheelchairAccessibilityPenaltySeconds,
                    edgeWithWeights.StrollerAccessibilityPenaltySeconds,
                    edgeWithWeights.AccessibilityDataQuality,
                    edgeWithWeights.EdgeWeightVersion,
                    edgeWithWeights.StandardTraversalSeconds,
                    edgeWithWeights.WheelchairTraversalSeconds,
                    edgeWithWeights.StrollerTraversalSeconds,
                    edgeWithWeights.Geometry?.Select(coord => new PackedRouteGraphCoordinate(coord.X, coord.Y)).ToArray()));
            }

            packedNodes.Add(new PackedRouteGraphNode(
                node.Id,
                node.Location.X,
                node.Location.Y,
                firstEdgeIndex,
                packedEdges.Count - firstEdgeIndex));
        }

        return new PackedRouteGraphArtifact(
            SchemaVersion,
            RouteEdgeCostModel.Version,
            RouteEdgeCostModel.EdgeWeightVersion,
            graphData.ShardKey,
            graphData.LoadedEdgeCount,
            graphData.IsTruncated,
            graphData.SpatialBucketSizeDegrees,
            packedNodes.ToArray(),
            packedEdges.ToArray());
    }

    public static RouteGraphData Unpack(PackedRouteGraphArtifact artifact)
    {
        if (!IsCompatible(artifact))
        {
            throw new InvalidOperationException(
                $"Route graph artifact {artifact.SchemaVersion}/cost-v{artifact.EdgeCostVersion}/weight-v{artifact.EdgeWeightVersion} is not compatible.");
        }

        var nodes = artifact.Nodes.ToDictionary(
            node => node.Id,
            node => new GraphNode
            {
                Id = node.Id,
                Location = new Coordinate(node.X, node.Y)
            });

        foreach (var node in artifact.Nodes)
        {
            if (!nodes.TryGetValue(node.Id, out var graphNode))
            {
                continue;
            }

            var end = Math.Min(artifact.Edges.Length, node.FirstEdgeIndex + node.EdgeCount);
            for (var i = node.FirstEdgeIndex; i < end; i++)
            {
                var edge = artifact.Edges[i];
                graphNode.Edges[edge.TargetNodeId] = new GraphEdge
                {
                    TargetNodeId = edge.TargetNodeId,
                    DistanceMetres = edge.DistanceMetres,
                    BaseSafetyCost = edge.BaseSafetyCost,
                    SurfaceType = edge.SurfaceType,
                    HasStairs = edge.HasStairs,
                    HasCrossing = edge.HasCrossing,
                    IsUnderConstruction = edge.IsUnderConstruction,
                    LightingQuality = edge.LightingQuality,
                    IsSteep = edge.IsSteep,
                    KerbHeight = edge.KerbHeight,
                    Smoothness = edge.Smoothness,
                    WidthMetres = edge.WidthMetres,
                    HasTactilePaving = edge.HasTactilePaving,
                    HasBarrier = edge.HasBarrier,
                    Access = edge.Access,
                    AccessibilityCostVersion = edge.AccessibilityCostVersion,
                    StandardAccessibilityPenaltySeconds = edge.StandardAccessibilityPenaltySeconds,
                    WheelchairAccessibilityPenaltySeconds = edge.WheelchairAccessibilityPenaltySeconds,
                    StrollerAccessibilityPenaltySeconds = edge.StrollerAccessibilityPenaltySeconds,
                    AccessibilityDataQuality = edge.AccessibilityDataQuality,
                    EdgeWeightVersion = edge.EdgeWeightVersion,
                    StandardTraversalSeconds = edge.StandardTraversalSeconds,
                    WheelchairTraversalSeconds = edge.WheelchairTraversalSeconds,
                    StrollerTraversalSeconds = edge.StrollerTraversalSeconds,
                    Geometry = edge.Geometry?.Select(coord => new Coordinate(coord.X, coord.Y)).ToArray()
                };
            }
        }

        var graphData = new RouteGraphData
        {
            Nodes = nodes,
            IsTruncated = artifact.IsTruncated,
            ShardKey = artifact.ShardKey,
            LoadedEdgeCount = artifact.LoadedEdgeCount,
            SpatialBucketSizeDegrees = artifact.SpatialBucketSizeDegrees
        };
        RouteGraphSpatialIndex.BuildSpatialBuckets(graphData);
        return graphData;
    }

    public static bool IsCompatible(PackedRouteGraphArtifact artifact) =>
        string.Equals(artifact.SchemaVersion, SchemaVersion, StringComparison.Ordinal)
        && artifact.EdgeCostVersion == RouteEdgeCostModel.Version
        && artifact.EdgeWeightVersion == RouteEdgeCostModel.EdgeWeightVersion;

    private static GraphEdge EnsureTraversalWeights(GraphEdge edge)
    {
        if (edge.EdgeWeightVersion == RouteEdgeCostModel.EdgeWeightVersion)
        {
            return edge;
        }

        var copy = new GraphEdge
        {
            TargetNodeId = edge.TargetNodeId,
            DistanceMetres = edge.DistanceMetres,
            BaseSafetyCost = edge.BaseSafetyCost,
            SurfaceType = edge.SurfaceType,
            HasStairs = edge.HasStairs,
            HasCrossing = edge.HasCrossing,
            IsUnderConstruction = edge.IsUnderConstruction,
            LightingQuality = edge.LightingQuality,
            IsSteep = edge.IsSteep,
            KerbHeight = edge.KerbHeight,
            Smoothness = edge.Smoothness,
            WidthMetres = edge.WidthMetres,
            HasTactilePaving = edge.HasTactilePaving,
            HasBarrier = edge.HasBarrier,
            Access = edge.Access,
            AccessibilityCostVersion = edge.AccessibilityCostVersion,
            StandardAccessibilityPenaltySeconds = edge.StandardAccessibilityPenaltySeconds,
            WheelchairAccessibilityPenaltySeconds = edge.WheelchairAccessibilityPenaltySeconds,
            StrollerAccessibilityPenaltySeconds = edge.StrollerAccessibilityPenaltySeconds,
            AccessibilityDataQuality = edge.AccessibilityDataQuality,
            Geometry = edge.Geometry
        };
        RouteEdgeCostModel.PopulateTraversalWeights(copy);
        return copy;
    }
}

public static class RouteGraphSpatialIndex
{
    public static void BuildSpatialBuckets(RouteGraphData graphData)
    {
        graphData.SpatialBuckets.Clear();
        var bucketSize = graphData.SpatialBucketSizeDegrees;
        foreach (var node in graphData.Nodes.Values)
        {
            var bucket = (
                X: (int)Math.Floor(node.Location.X / bucketSize),
                Y: (int)Math.Floor(node.Location.Y / bucketSize));
            if (!graphData.SpatialBuckets.TryGetValue(bucket, out var nodeIds))
            {
                nodeIds = new List<long>();
                graphData.SpatialBuckets[bucket] = nodeIds;
            }

            nodeIds.Add(node.Id);
        }
    }
}

public sealed record PackedRouteGraphArtifact(
    string SchemaVersion,
    int EdgeCostVersion,
    int EdgeWeightVersion,
    string? ShardKey,
    int LoadedEdgeCount,
    bool IsTruncated,
    double SpatialBucketSizeDegrees,
    PackedRouteGraphNode[] Nodes,
    PackedRouteGraphEdge[] Edges);

public sealed record PackedRouteGraphNode(
    long Id,
    double X,
    double Y,
    int FirstEdgeIndex,
    int EdgeCount);

public sealed record PackedRouteGraphEdge(
    long TargetNodeId,
    double DistanceMetres,
    double BaseSafetyCost,
    string SurfaceType,
    bool HasStairs,
    bool HasCrossing,
    bool IsUnderConstruction,
    double LightingQuality,
    bool IsSteep,
    double KerbHeight,
    string? Smoothness,
    double? WidthMetres,
    bool HasTactilePaving,
    bool HasBarrier,
    string? Access,
    int AccessibilityCostVersion,
    double StandardAccessibilityPenaltySeconds,
    double WheelchairAccessibilityPenaltySeconds,
    double StrollerAccessibilityPenaltySeconds,
    double AccessibilityDataQuality,
    int EdgeWeightVersion,
    double StandardTraversalSeconds,
    double WheelchairTraversalSeconds,
    double StrollerTraversalSeconds,
    PackedRouteGraphCoordinate[]? Geometry);

public sealed record PackedRouteGraphCoordinate(double X, double Y);
