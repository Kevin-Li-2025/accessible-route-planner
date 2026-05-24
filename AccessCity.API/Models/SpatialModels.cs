using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Models
{
    public class PointOfInterest
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = null!;
        public string Category { get; set; } = null!;
        public Point Location { get; set; } = null!;
        public Dictionary<string, string> AccessibilityTags { get; set; } = new();
        public InfrastructureAccessibilityProfile AccessibilityProfile { get; set; } = new();
    }

    public sealed class MapOverlayFeature
    {
        public string Type { get; set; } = "Feature";
        public Geometry Geometry { get; set; } = null!;
        public object Properties { get; set; } = null!;
    }

    public sealed class MapOverlayResponse
    {
        public string Type { get; set; } = "FeatureCollection";
        public string Layer { get; set; } = string.Empty;
        public IReadOnlyList<MapOverlayFeature> Features { get; set; } = Array.Empty<MapOverlayFeature>();
    }
}
