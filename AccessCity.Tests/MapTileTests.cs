using System.Net;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using AccessCity.API.Services;

namespace AccessCity.Tests
{
    public class MapTileTests : IClassFixture<AccessCityApiFactory>
    {
        private readonly HttpClient _client;
        private readonly AccessCityApiFactory _factory;

        public MapTileTests(AccessCityApiFactory factory)
        {
            _factory = factory;
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task GetTile_Returns_Pbf_Data_When_Hazards_Exist()
        {
            // 1. Seed a hazard in the spatial cache
            using (var scope = _factory.Services.CreateScope())
            {
                var spatialCache = scope.ServiceProvider.GetRequiredService<ISpatialCacheService>();
                var hazard = new AccessCity.API.Models.HazardReport
                {
                    Id = Guid.NewGuid(),
                    Location = new NetTopologySuite.Geometries.Point(-1.8904, 52.4862),
                    Type = "test-hazard",
                    Status = AccessCity.API.Models.HazardStatus.Reported,
                    Description = "test",
                    PhotoUrl = "url"
                };
                await spatialCache.UpdateHazardCacheAsync(hazard);
            }

            // 2. Request the tile containing that coordinate (Z=13, X=4052, Y=2743 for Birmingham)
            // Note: Approximate tile for -1.8904, 52.4862 at Z13 is 4052/2743
            var response = await _client.GetAsync("/api/tiles/13/4052/2743.pbf");

            // 3. Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/x-protobuf", response.Content.Headers.ContentType?.MediaType);
            
            var data = await response.Content.ReadAsByteArrayAsync();
            Assert.NotEmpty(data);
        }

        [Fact]
        public void BloomFilter_Probabilistic_Check()
        {
            var filter = new BloomFilterService(expectedItems: 1000, falsePositiveProbability: 0.01);
            
            string item = "tile:13:4052:2743";
            filter.Add(item);
            
            Assert.True(filter.MightContain(item));
            Assert.False(filter.MightContain("non-existent-item"));
        }
    }
}
