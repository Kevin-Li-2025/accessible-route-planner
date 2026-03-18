using System.Text.Json.Serialization;

namespace AccessCity.API.Models.External
{
    public class OsrmRouteResponse
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("routes")]
        public List<OsrmRoute> Routes { get; set; } = new();

        [JsonPropertyName("waypoints")]
        public List<OsrmWaypoint> Waypoints { get; set; } = new();
    }

    public class OsrmRoute
    {
        [JsonPropertyName("distance")]
        public double Distance { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("geometry")]
        public OsrmGeometry Geometry { get; set; } = new();

        [JsonPropertyName("legs")]
        public List<OsrmLeg> Legs { get; set; } = new();
    }

    public class OsrmGeometry
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "LineString";

        [JsonPropertyName("coordinates")]
        public List<List<double>> Coordinates { get; set; } = new();
    }

    public class OsrmLeg
    {
        [JsonPropertyName("distance")]
        public double Distance { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("steps")]
        public List<OsrmStep> Steps { get; set; } = new();
    }

    public class OsrmStep
    {
        [JsonPropertyName("distance")]
        public double Distance { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("geometry")]
        public OsrmGeometry Geometry { get; set; } = new();

        [JsonPropertyName("maneuver")]
        public OsrmManeuver Maneuver { get; set; } = new();
    }

    public class OsrmManeuver
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("modifier")]
        public string? Modifier { get; set; }

        [JsonPropertyName("location")]
        public List<double> Location { get; set; } = new();
    }

    public class OsrmWaypoint
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("location")]
        public List<double> Location { get; set; } = new();
    }
}
