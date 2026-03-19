using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;

namespace AccessCity.Tests;

public class SpatialCacheTests : IClassFixture<AccessCityApiFactory>
{
    private readonly ISpatialCacheService _spatialCache;
    private readonly AccessCityApiFactory _factory;

    public SpatialCacheTests(AccessCityApiFactory factory)
    {
        _factory = factory;
        var scope = factory.Services.CreateScope();
        _spatialCache = scope.ServiceProvider.GetRequiredService<ISpatialCacheService>();
    }

    [Fact]
    public async Task SpatialCache_Should_Fall_Back_To_Postgres_Query()
    {
        using var freshFactory = new AccessCityApiFactory();
        using var scope = freshFactory.Services.CreateScope();
        var spatialCache = scope.ServiceProvider.GetRequiredService<ISpatialCacheService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var hazard = new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = new Point(-1.8904, 52.4862) { SRID = 4326 },
            Type = "pothole",
            Status = HazardStatus.Reported,
            Description = "Test pothole",
            PhotoUrl = "http://example.com/photo.jpg",
            ReportedAt = DateTime.UtcNow
        };

        dbContext.Hazards.Add(hazard);
        await dbContext.SaveChangesAsync();

        var bounds = new Envelope(-1.9, -1.88, 52.48, 52.49);
        var results = await spatialCache.GetHazardsInBoundsAsync(bounds);

        Assert.NotEmpty(results);
        Assert.Contains(results, h => h.Id == hazard.Id);

        var outOfBounds = new Envelope(0, 1, 0, 1);
        var emptyResults = await spatialCache.GetHazardsInBoundsAsync(outOfBounds);
        Assert.Empty(emptyResults);
    }

    [Fact]
    public async Task BulkUpdate_Should_Insert_Multiple_Hazards()
    {
        var hazards = new List<HazardReport>
        {
            new() { Id = Guid.NewGuid(), Location = new Point(0, 0) { SRID = 4326 }, Type = "t1", Status = HazardStatus.Reported, Description = "d1", PhotoUrl = "p1", ReportedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), Location = new Point(1, 1) { SRID = 4326 }, Type = "t2", Status = HazardStatus.Reported, Description = "d2", PhotoUrl = "p2", ReportedAt = DateTime.UtcNow }
        };

        await _spatialCache.BulkUpdateHazardsAsync(hazards);

        var bounds = new Envelope(-0.1, 1.1, -0.1, 1.1);
        var results = await _spatialCache.GetHazardsInBoundsAsync(bounds);

        Assert.True(results.Count >= 2);
    }

    [Fact]
    public async Task GetMapBundle_Returns_Db_And_Cache_Backfilled_Hazards()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var hazard = new HazardReport
            {
                Id = Guid.NewGuid(),
                Location = new Point(-1.5, 52.0) { SRID = 4326 },
                Type = "test",
                Status = HazardStatus.Reported,
                Description = "desc",
                PhotoUrl = "url",
                ReportedAt = DateTime.UtcNow
            };

            dbContext.Hazards.Add(hazard);
            await dbContext.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/OfflineMap/bundle?minLat=51.9&minLng=-1.6&maxLat=52.1&maxLng=-1.4");

        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("infrastructure", content);
        Assert.Contains("test", content);
    }
}
