using Microsoft.AspNetCore.Mvc;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Models
{
    public class PointOfInterest
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public Point Location { get; set; }
        public Dictionary<string, string> AccessibilityTags { get; set; } = new();
    }
}
