using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Xunit;

namespace AccessCity.Tests
{
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
        public async Task SpatialCache_Should_Return_Hazards_In_Bounds()
        {
            // 1. Arrange: Create a hazard
            var hazard = new HazardReport
            {
                Id = Guid.NewGuid(),
                Location = new Point(-1.8904, 52.4862) { SRID = 4326 },
                Type = "pothole",
                Status = HazardStatus.Reported,
                Description = "Test pothole",
                PhotoUrl = "http://example.com/photo.jpg"
            };

            // 2. Act: Insert into cache
            await _spatialCache.UpdateHazardCacheAsync(hazard);

            // 3. Assert: Query with a bounding box that contains the point
            var bounds = new Envelope(-1.9, -1.88, 52.48, 52.49);
            var results = await _spatialCache.GetHazardsInBoundsAsync(bounds);

            Assert.NotEmpty(results);
            Assert.Contains(results, h => h.Id == hazard.Id);

            // 4. Act: Query with a bounding box that DOES NOT contain the point
            var outOfBounds = new Envelope(0, 1, 0, 1);
            var emptyResults = await _spatialCache.GetHazardsInBoundsAsync(outOfBounds);

            Assert.Empty(emptyResults);
        }

        [Fact]
        public async Task BulkUpdate_Should_Insert_Multiple_Hazards()
        {
            var hazards = new List<HazardReport>
            {
                new HazardReport { Id = Guid.NewGuid(), Location = new Point(0, 0), Type = "t1", Status = HazardStatus.Reported, Description = "d1", PhotoUrl = "p1" },
                new HazardReport { Id = Guid.NewGuid(), Location = new Point(1, 1), Type = "t2", Status = HazardStatus.Reported, Description = "d2", PhotoUrl = "p2" }
            };

            await _spatialCache.BulkUpdateHazardsAsync(hazards);

            var bounds = new Envelope(-0.1, 1.1, -0.1, 1.1);
            var results = await _spatialCache.GetHazardsInBoundsAsync(bounds);

            Assert.Equal(2, results.Count);
        }

        [Fact]
        public async Task GetMapBundle_Returns_Cached_Hazards()
        {
            // 1. Arrange
            var client = await _factory.CreateAuthenticatedClientAsync();
            var hazard = new HazardReport
            {
                Id = Guid.NewGuid(),
                Location = new Point(-1.5, 52.0),
                Type = "test",
                Status = HazardStatus.Reported,
                Description = "desc",
                PhotoUrl = "url"
            };
            await _spatialCache.UpdateHazardCacheAsync(hazard);

            // 2. Act
            var response = await client.GetAsync("/api/OfflineMap/bundle?minLat=51.9&minLng=-1.6&maxLat=52.1&maxLng=-1.4");

            // 3. Assert
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains(hazard.Id.ToString(), content);
        }
    }
}
