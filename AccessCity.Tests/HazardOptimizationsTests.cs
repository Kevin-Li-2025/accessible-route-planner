using AccessCity.API.Models;
using AccessCity.API.Services;
using NetTopologySuite.Geometries;
using Xunit;

namespace AccessCity.Tests;

public class HazardOptimizationsTests
{
    [Fact]
    public void HazardSpatialIndex_CorrectlyIndexesAndQueriesHazards()
    {
        // Arrange
        var index = new HazardSpatialIndex();
        var hazard1 = new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = new Point(-1.8904, 52.4862) { SRID = 4326 }, // Birmingham City Centre
            Type = "pothole",
            Status = HazardStatus.Reported
        };
        var hazard2 = new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = new Point(-1.9300, 52.4510) { SRID = 4326 }, // Univ of Birmingham (far away, ~3km)
            Type = "broken_sidewalk",
            Status = HazardStatus.Reported
        };
        var hazard3 = new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = new Point(-1.8906, 52.4863) { SRID = 4326 }, // Very close to hazard1 (~15m)
            Type = "obstruction",
            Status = HazardStatus.Reported
        };

        var hazards = new List<HazardReport> { hazard1, hazard2, hazard3 };

        // Act
        index.Rebuild(hazards);

        // Assert
        Assert.True(index.IsWarmedUp);
        Assert.Equal(3, index.Count);

        // Query close to hazard1 (within 50 meters)
        var nearby = index.QueryNearby(52.4862, -1.8904, 50.0);
        Assert.Contains(hazard1, nearby);
        Assert.Contains(hazard3, nearby);
        Assert.DoesNotContain(hazard2, nearby); // Too far

        // Query bounding box containing only Birmingham city centre hazards
        var bbox = index.QueryBoundingBox(-1.891, 52.485, -1.890, 52.487);
        Assert.Contains(hazard1, bbox);
        Assert.Contains(hazard3, bbox);
        Assert.DoesNotContain(hazard2, bbox);
    }

    [Fact]
    public void HazardRiskGrid_ComputesCorrectRiskValues()
    {
        // Arrange
        var index = new HazardSpatialIndex();
        var hazard = new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = new Point(-1.8904, 52.4862) { SRID = 4326 }, // Birmingham
            Type = "broken_sidewalk", // Severity = 0.7
            Status = HazardStatus.Reported
        };
        index.Rebuild(new List<HazardReport> { hazard });

        var grid = new HazardRiskGrid();

        // Act
        grid.Rebuild(index);

        // Assert
        Assert.True(grid.IsReady);

        // At the exact coordinate of the hazard, risk should be close to severity (0.7)
        double exactRisk = grid.GetRisk(52.4862, -1.8904);
        Assert.True(exactRisk > 0.4 && exactRisk <= 1.0, $"Exact risk is {exactRisk}");

        // Far away from the hazard, risk should decay to 0
        double farRisk = grid.GetRisk(52.5500, -1.9500);
        Assert.Equal(0.0, farRisk);
    }
}
