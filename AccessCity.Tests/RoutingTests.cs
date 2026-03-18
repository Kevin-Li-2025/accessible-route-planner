using AccessCity.API.Models;
using Xunit;
using NetTopologySuite.Geometries;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AccessCity.Tests
{
    public class RoutingTests : IClassFixture<AccessCityApiFactory>
    {
        private readonly AccessCityApiFactory _factory;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            PropertyNameCaseInsensitive = true,
            Converters = { new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() }
        };

        public RoutingTests(AccessCityApiFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task GetSafePath_Returns_Valid_Route()
        {
            var client = await _factory.CreateAuthenticatedClientAsync(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            // Use a simple coordinate dictionary to avoid NTS serialization issues if possible
            var request = new 
            {
                Start = new { X = -1.8904, Y = 52.4862 },
                End = new { X = -1.8904, Y = 52.4862 },
                Preferences = new List<string>(),
                SafetyWeight = 0
            };

            var response = await client.PostAsJsonAsync("/api/routing/safe-path", request, _jsonOptions);
            
            if (response.StatusCode == System.Net.HttpStatusCode.Redirect || 
                response.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
            {
                var location = response.Headers.Location;
                throw new Exception($"Unexpected redirect to {location}");
            }

            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RouteResponse>(_jsonOptions);
            Assert.NotNull(result);
        }

        [Fact]
        public async Task GetRiskScore_Returns_Score()
        {
            var client = await _factory.CreateAuthenticatedClientAsync(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

            double lat = 52.4862;
            double lng = -1.8904;
            double radius = 500;

            var response = await client.GetAsync($"/api/routing/risk-score?lat={lat}&lng={lng}&radius={radius}");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RiskScoreResponse>(_jsonOptions);
            Assert.NotNull(result);
            Assert.True(result.OverallRisk >= 0);
        }
    }
}
