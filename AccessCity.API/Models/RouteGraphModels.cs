using System.Text.Json;
using AccessCity.API.Services;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Models;

public class RouteNode
{
    public long Id { get; set; }
    public Point Location { get; set; } = null!;
    public JsonDocument Tags { get; set; } = JsonDocument.Parse("{}");
}

public class RouteEdge
{
    public long Id { get; set; }
    public long FromNodeId { get; set; }
    public long ToNodeId { get; set; }
    public long? SourceWayId { get; set; }
    public LineString Geometry { get; set; } = null!;
    public double DistanceMetres { get; set; }
    public double BaseSafetyCost { get; set; }
    public string SurfaceType { get; set; } = "asphalt";
    public bool HasStairs { get; set; }
    public bool HasCrossing { get; set; }
    public bool IsUnderConstruction { get; set; }
    public double LightingQuality { get; set; } = 0.5;
    public bool IsSteep { get; set; }
    public double KerbHeight { get; set; }
    public string? Smoothness { get; set; }
    public double? WidthMetres { get; set; }
    public bool HasTactilePaving { get; set; }
    public bool HasBarrier { get; set; }
    public string? Access { get; set; }
    public int AccessibilityCostVersion { get; set; }
    public double StandardAccessibilityPenaltySeconds { get; set; }
    public double WheelchairAccessibilityPenaltySeconds { get; set; }
    public double StrollerAccessibilityPenaltySeconds { get; set; }
    public double AccessibilityDataQuality { get; set; } = 1.0;
    public JsonDocument Tags { get; set; } = JsonDocument.Parse("{}");

    public RouteNode FromNode { get; set; } = null!;
    public RouteNode ToNode { get; set; } = null!;
}

public sealed class RouteGraphData
{
    public Dictionary<long, GraphNode> Nodes { get; init; } = new();
    public Dictionary<(int X, int Y), List<long>> SpatialBuckets { get; } = new();
    public RouteGraphPreprocessingData? Preprocessing { get; set; }
    public ContractionHierarchySet? ContractionHierarchies { get; set; }
    public double SpatialBucketSizeDegrees { get; init; } = 0.001;
    public string? ShardKey { get; init; }
    public IReadOnlyList<string> SourceShardKeys { get; init; } = Array.Empty<string>();
    public int LoadedEdgeCount { get; init; }
    public bool IsTruncated { get; init; }
    public bool HasCoverage => Nodes.Count > 0;
}

public sealed class RouteGraphPreprocessingData
{
    public string Algorithm { get; init; } = "ALT";
    public int AlgorithmVersion { get; init; }
    public string WeightVersion { get; init; } = string.Empty;
    public long[] LandmarkNodeIds { get; init; } = Array.Empty<long>();
    public Dictionary<long, RouteGraphNodePreprocessing> NodeDistances { get; init; } = new();
    public bool HasLandmarks => LandmarkNodeIds.Length > 0 && NodeDistances.Count > 0;
}

public sealed class RouteGraphNodePreprocessing
{
    public float[] FromLandmarkSeconds { get; init; } = Array.Empty<float>();
    public float[] ToLandmarkSeconds { get; init; } = Array.Empty<float>();
}

public sealed record RouteGraphCoverageStatus(
    long RouteNodeCount,
    long RouteEdgeCount,
    bool HasCoverage,
    string Version,
    long? LatestOsmRunId,
    string? LatestOsmRunStatus,
    DateTime? LatestOsmRunFinishedAtUtc,
    string? LatestOsmSourceName,
    string? Warning);
