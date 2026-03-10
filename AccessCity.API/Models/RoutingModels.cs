using NetTopologySuite.Geometries;

namespace AccessCity.API.Models
{
    public class RouteRequest
    {
        public Coordinate Start { get; set; }
        public Coordinate End { get; set; }
        public List<string> Preferences { get; set; } = new();
    }

    public class RouteResponse
    {
        public LineString Path { get; set; }
        public double Distance { get; set; }
        public double SafetyScore { get; set; }
        public List<string> Warnings { get; set; } = new();
    }
}
