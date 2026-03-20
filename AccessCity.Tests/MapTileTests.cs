using System.Net;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;

namespace AccessCity.Tests;

public class MapTileTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;

    public MapTileTests(AccessCityApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetTile_Returns_Pbf_Data_When_Db_Hazard_Exists()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var spatialCache = scope.ServiceProvider.GetRequiredService<ISpatialCacheService>();

            var hazard = new HazardReport
            {
                Id = Guid.NewGuid(),
                Location = new Point(-1.8904, 52.4862) { SRID = 4326 },
                Type = "test-hazard",
                Status = HazardStatus.Reported,
                Description = "db-backed tile hazard",
                PhotoUrl = "url",
                ReportedAt = DateTime.UtcNow
            };

            dbContext.Hazards.Add(hazard);
            await dbContext.SaveChangesAsync();
            await spatialCache.UpdateHazardCacheAsync(hazard);
        }

        var response = await client.GetAsync("/api/v1/tiles/13/4052/2743.pbf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);

        var data = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(data);
    }

    [Fact]
    public void BloomFilter_Probabilistic_Check()
    {
        var filter = new BloomFilterService(expectedItems: 1000, falsePositiveProbability: 0.01);

        const string item = "tile:13:4052:2743";
        filter.Add(item);

        Assert.True(filter.MightContain(item));
        Assert.False(filter.MightContain("non-existent-item"));
    }
}
