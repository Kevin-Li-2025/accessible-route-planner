using NetTopologySuite.Geometries;

namespace AccessCity.API.Models
{
    // ── Request / Response DTOs ────────────────────────────────────

    /// <summary>Client request for a safety-aware route.</summary>
    public class RouteRequest
    {
        public Coordinate Start { get; set; } = null!;
        public Coordinate End   { get; set; } = null!;

        /// <summary>
        /// User accessibility / comfort preferences.  
        /// Supported flags: "avoid-stairs", "avoid-cobblestone", "wheelchair",
        /// "low-light-penalty", "prefer-crossings", "avoid-construction".
        /// </summary>
        public List<string> Preferences { get; set; } = new();

        /// <summary>
        /// Weight given to safety vs. distance (0 = shortest path, 1 = safest path).
        /// Default 0.5 gives balanced routing.
        /// </summary>
        public double SafetyWeight { get; set; } = 0.5;
    }

    /// <summary>Full route response returned to the client.</summary>
    public class RouteResponse
    {
        public LineString?     Path          { get; set; }
        public double          Distance      { get; set; }
        public double          EstimatedTime { get; set; }
        public double          SafetyScore   { get; set; }
        public List<string>    Warnings      { get; set; } = new();
        public List<RouteStep> Steps         { get; set; } = new();
    }

    /// <summary>One leg / turn-by-turn instruction.</summary>
    public class RouteStep
    {
        public Point      From        { get; set; } = null!;
        public Point      To          { get; set; } = null!;
        public double     Distance    { get; set; }
        public double     SafetyScore { get; set; }
        public string     Instruction { get; set; } = string.Empty;
    }

    // ── Risk Scoring DTOs ──────────────────────────────────────────

    /// <summary>Request for a predictive risk score at a location.</summary>
    public class RiskScoreRequest
    {
        public double Latitude  { get; set; }
        public double Longitude { get; set; }

        /// <summary>Radius in metres to consider for hazard density.</summary>
        public double RadiusMetres { get; set; } = 500;
    }

    /// <summary>Detailed risk breakdown returned to the client.</summary>
    public class RiskScoreResponse
    {
        /// <summary>Overall risk score (0 = perfectly safe, 1 = maximum risk).</summary>
        public double OverallRisk           { get; set; }

        /// <summary>Risk from proximity to reported hazards.</summary>
        public double HazardProximityRisk    { get; set; }

        /// <summary>Risk from density of hazards in the area.</summary>
        public double HazardDensityRisk     { get; set; }

        /// <summary>Risk estimated from infrastructure quality indicators.</summary>
        public double InfrastructureRisk    { get; set; }

        /// <summary>Number of active hazards within the search radius.</summary>
        public int    NearbyHazardCount     { get; set; }

        public List<NearbyHazard> NearbyHazards { get; set; } = new();
    }

    /// <summary>A nearby hazard surfaced in the risk response.</summary>
    public class NearbyHazard
    {
        public Guid   Id             { get; set; }
        public string Type           { get; set; } = string.Empty;
        public double DistanceMetres { get; set; }
        public double RiskWeight     { get; set; }
    }

    // ── Internal graph model (used by the routing engine) ──────────

    /// <summary>A node in the routing graph representing an intersection or waypoint.</summary>
    public class GraphNode
    {
        public long       Id       { get; set; }
        public Coordinate Location { get; set; } = null!;

        /// <summary>Adjacency list: neighbour node id → edge.</summary>
        public Dictionary<long, GraphEdge> Edges { get; set; } = new();
    }

    /// <summary>A directed edge between two graph nodes.</summary>
    public class GraphEdge
    {
        public long   TargetNodeId       { get; set; }
        public double DistanceMetres     { get; set; }

        /// <summary>Base safety cost (higher = less safe). Range 0-1.</summary>
        public double BaseSafetyCost     { get; set; }

        /// <summary>Surface type, e.g. "asphalt", "cobblestone", "gravel".</summary>
        public string SurfaceType        { get; set; } = "asphalt";

        /// <summary>True if this edge contains stairs / steps.</summary>
        public bool   HasStairs          { get; set; }

        /// <summary>True if a marked pedestrian crossing exists on this edge.</summary>
        public bool   HasCrossing        { get; set; }

        /// <summary>True when active construction / temporary closure is reported.</summary>
        public bool   IsUnderConstruction { get; set; }

        /// <summary>Estimated street-lighting quality (0 = none, 1 = well-lit).</summary>
        public double LightingQuality    { get; set; } = 0.8;
    }
}
