using NetTopologySuite.Geometries;
using NpgsqlTypes;

namespace AccessCity.API.Models
{
    public enum HazardStatus
    {
        Reported,
        UnderReview,
        Resolved,
        Dismissed
    }

    public enum DatabaseHazardStatus
    {
        [PgName("reported")]
        Reported,
        [PgName("under_review")]
        UnderReview,
        [PgName("verified")]
        Verified,
        [PgName("action_planned")]
        ActionPlanned,
        [PgName("in_progress")]
        InProgress,
        [PgName("resolved")]
        Resolved,
        [PgName("rejected")]
        Rejected,
        [PgName("duplicate")]
        Duplicate
    }

    public class HazardReport
    {
        public Guid Id { get; set; }
        public Point Location { get; set; } = null!;
        public string Type { get; set; } = null!;
        public string Description { get; set; } = null!;
        public string PhotoUrl { get; set; } = string.Empty;
        public DateTime ReportedAt { get; set; }
        public HazardStatus Status { get; set; }
        public string Source { get; set; } = "user";
        public string? ReporterUserId { get; set; }
    }
}
