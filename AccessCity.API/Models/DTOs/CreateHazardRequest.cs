using NetTopologySuite.Geometries;

namespace AccessCity.API.Models.DTOs
{
    public class CreateHazardRequest
    {
        public Coordinate Location { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string Description { get; set; } = null!;
        public string? PhotoUrl { get; set; }
        public string? Source { get; set; }
    }
}
