using AccessCity.API.Models;
using AccessCity.API.Services;
using Xunit;
using NetTopologySuite.Geometries;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AccessCity.Tests
{
    public class RoutingTests : IClassFixture<AccessCityApiFactory>
    {
        private readonly HttpClient _client;
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            PropertyNameCaseInsensitive = true,
            Converters = { new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() }
        };

        public RoutingTests(AccessCityApiFactory factory)
        {
            // Disable redirects to see why it happens and avoid NaN serialization issues in RedirectHandler
            _client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
        }

        [Fact]
        public async Task GetSafePath_Returns_Valid_Route()
        {
            // Use a simple coordinate dictionary to avoid NTS serialization issues if possible
            var request = new 
            {
                Start = new { X = -1.8904, Y = 52.4862 },
                End = new { X = -1.8904, Y = 52.4862 },
                Preferences = new List<string>(),
                SafetyWeight = 0
            };

            var response = await _client.PostAsJsonAsync("/api/routing/safe-path", request, _jsonOptions);
            
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
            double lat = 52.4862;
            double lng = -1.8904;
            double radius = 500;

            var response = await _client.GetAsync($"/api/routing/risk-score?lat={lat}&lng={lng}&radius={radius}");
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RiskScoreResponse>(_jsonOptions);
            Assert.NotNull(result);
            Assert.True(result.OverallRisk >= 0);
        }

        // ──── New tests for OSRM + AI ────

        [Fact]
        public async Task SafePath_RealCoordinates_Returns_Route_With_Steps()
        {
            // Birmingham City Centre → University of Birmingham (real-world coordinates)
            var request = new
            {
                Start = new { X = -1.8985, Y = 52.4814 },   // Birmingham New St Station
                End   = new { X = -1.9300, Y = 52.4510 },   // University of Birmingham
                Preferences = new List<string>(),
                SafetyWeight = 0.5
            };

            var response = await _client.PostAsJsonAsync("/api/routing/safe-path", request, _jsonOptions);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RouteResponse>(_jsonOptions);
            Assert.NotNull(result);
            Assert.True(result.Distance > 0, "Route distance should be > 0");
            Assert.True(result.EstimatedTime > 0, "Estimated walking time should be > 0");
            Assert.True(result.SafetyScore >= 0 && result.SafetyScore <= 1, 
                $"Safety score ({result.SafetyScore}) should be between 0 and 1");
            Assert.NotNull(result.Steps);
            Assert.True(result.Steps.Count > 0, "Should have at least one route step");
        }

        [Fact]
        public async Task SafePath_WithHighSafetyWeight_Returns_Warnings()
        {
            // Route near known hazard area with high safety weight
            var request = new
            {
                Start = new { X = -1.8985, Y = 52.4814 },
                End   = new { X = -1.9300, Y = 52.4510 },
                Preferences = new List<string>(),
                SafetyWeight = 1.0  // Maximum safety preference
            };

            var response = await _client.PostAsJsonAsync("/api/routing/safe-path", request, _jsonOptions);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<RouteResponse>(_jsonOptions);
            Assert.NotNull(result);
            Assert.NotNull(result.Warnings);
            // Safety score should still be valid
            Assert.True(result.SafetyScore >= 0 && result.SafetyScore <= 1);
        }

        [Fact]
        public async Task AiRiskScore_Returns_MultiFactor_Breakdown()
        {
            // Test the AI risk endpoint near UoB campus
            double lat = 52.4514;
            double lng = -1.9305;

            var response = await _client.GetAsync($"/api/routing/ai-risk-score?lat={lat}&lng={lng}&radius=200");
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<PredictiveRiskResult>(content, _jsonOptions);

            Assert.NotNull(result);

            // Overall risk should be between 0 and 1
            Assert.True(result.OverallRisk >= 0 && result.OverallRisk <= 1,
                $"Overall AI risk ({result.OverallRisk}) should be in [0,1]");

            // Sub-scores should all be populated
            Assert.True(result.HazardRisk >= 0, "HazardRisk should be >= 0");
            Assert.True(result.TimeOfDayRisk >= 0, "TimeOfDayRisk should be >= 0");
            Assert.True(result.WeatherRisk >= 0, "WeatherRisk should be >= 0");
            Assert.True(result.CrimeRisk >= 0, "CrimeRisk should be >= 0");
            Assert.True(result.InfrastructureRisk >= 0, "InfrastructureRisk should be >= 0");

            // Risk factors list should contain at least one explanation
            Assert.NotNull(result.RiskFactors);
            Assert.True(result.RiskFactors.Count > 0, "Should have at least one risk factor explanation");
        }

        [Fact]
        public async Task AiRiskScore_InvalidCoordinates_Returns_BadRequest()
        {
            var response = await _client.GetAsync("/api/routing/ai-risk-score?lat=999&lng=999");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
