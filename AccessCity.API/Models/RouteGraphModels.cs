using System.Text.Json;
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
    public JsonDocument Tags { get; set; } = JsonDocument.Parse("{}");

    public RouteNode FromNode { get; set; } = null!;
    public RouteNode ToNode { get; set; } = null!;
}

public sealed class RouteGraphData
{
    public Dictionary<long, GraphNode> Nodes { get; init; } = new();
    public bool IsTruncated { get; init; }
    public bool HasCoverage => Nodes.Count > 0;
}
