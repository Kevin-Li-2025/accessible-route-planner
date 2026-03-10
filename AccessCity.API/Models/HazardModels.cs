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
        public Point Location { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public string PhotoUrl { get; set; }
        public DateTime ReportedAt { get; set; }
        public HazardStatus Status { get; set; }
    }
}
