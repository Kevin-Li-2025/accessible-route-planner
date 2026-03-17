using NetTopologySuite.Geometries;

namespace AccessCity.API.Models
{
    public enum HazardStatus
    {
        Reported,
        UnderReview,
        Resolved,
        Dismissed
    }

    public class HazardReport
    {
        public Guid Id { get; set; }
        public Point Location { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string Description { get; set; } = null!;
        public string PhotoUrl { get; set; } = null!;
        public DateTime ReportedAt { get; set; }
        public HazardStatus Status { get; set; }
    }
}
