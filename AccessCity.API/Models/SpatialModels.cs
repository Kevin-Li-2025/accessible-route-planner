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
    }
}
