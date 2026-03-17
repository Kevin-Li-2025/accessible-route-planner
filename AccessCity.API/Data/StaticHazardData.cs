using AccessCity.API.Models;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Data
{
    public static class StaticHazardData
    {
        public static List<HazardReport> GetActiveHazards()
        {
            return new List<HazardReport>
            {
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0001-4000-8000-000000000001"),
                    Location = new Point(-1.9003, 52.4814),
                    Type = "pothole",
                    Description = "Large pothole on New Street near the station.",
                    ReportedAt = DateTime.UtcNow.AddDays(-3),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0002-4000-8000-000000000002"),
                    Location = new Point(-1.8975, 52.4792),
                    Type = "poor_lighting",
                    Description = "Poorly lit underpass beneath the ring road.",
                    ReportedAt = DateTime.UtcNow.AddDays(-7),
                    Status = HazardStatus.UnderReview
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0003-4000-8000-000000000003"),
                    Location = new Point(-1.9031, 52.4835),
                    Type = "construction",
                    Description = "Active construction near Paradise Circus.",
                    ReportedAt = DateTime.UtcNow.AddDays(-1),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0004-4000-8000-000000000004"),
                    Location = new Point(-1.8950, 52.4780),
                    Type = "missing_curb_ramp",
                    Description = "Missing kerb ramp at the junction of Digbeth High Street.",
                    ReportedAt = DateTime.UtcNow.AddDays(-14),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0005-4000-8000-000000000005"),
                    Location = new Point(-1.9051, 52.4862),
                    Type = "broken_pavement",
                    Description = "Broken pavement on Broad Street near Five Ways.",
                    ReportedAt = DateTime.UtcNow.AddDays(-5),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0006-4000-8000-000000000006"),
                    Location = new Point(-1.8915, 52.4833),
                    Type = "obstruction",
                    Description = "Temporary bollards blocking footpath on Corporation Street.",
                    ReportedAt = DateTime.UtcNow.AddDays(-2),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0007-4000-8000-000000000007"),
                    Location = new Point(-1.8990, 52.4755),
                    Type = "missing_crossing",
                    Description = "No pedestrian crossing on busy A38 Bristol Road section.",
                    ReportedAt = DateTime.UtcNow.AddDays(-10),
                    Status = HazardStatus.UnderReview
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0008-4000-8000-000000000008"),
                    Location = new Point(-1.9080, 52.4510),
                    Type = "steep_gradient",
                    Description = "Steep hill on Bristol Road near University of Birmingham.",
                    ReportedAt = DateTime.UtcNow.AddDays(-20),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-0009-4000-8000-000000000009"),
                    Location = new Point(-1.9300, 52.4510),
                    Type = "uneven_surface",
                    Description = "Uneven cobbled path near the clock tower, UoB campus.",
                    ReportedAt = DateTime.UtcNow.AddDays(-8),
                    Status = HazardStatus.Reported
                },
                new()
                {
                    Id = Guid.Parse("a1b2c3d4-000a-4000-8000-000000000010"),
                    Location = new Point(-1.9275, 52.4525),
                    Type = "missing_tactile",
                    Description = "No tactile paving at the south entrance to campus.",
                    ReportedAt = DateTime.UtcNow.AddDays(-12),
                    Status = HazardStatus.Reported
                }
            };
        }
    }
}
