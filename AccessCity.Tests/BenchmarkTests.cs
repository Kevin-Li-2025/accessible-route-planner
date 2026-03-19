using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Xunit;
using Xunit.Abstractions;

namespace AccessCity.Tests;

public class BenchmarkTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private readonly ITestOutputHelper _output;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() }
    };

    public BenchmarkTests(AccessCityApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    private record RouteBenchmark(string Name, double StartX, double StartY, double EndX, double EndY);

    [Fact]
    public async Task RunQuantitativeEvaluation()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        
        var testRoutes = new List<RouteBenchmark>
        {
            new("Birmingham New St to Bullring", -1.8989, 52.4777, -1.8943, 52.4776),
            new("Library to Town Hall", -1.9085, 52.4795, -1.9026, 52.4793),
            new("Aston Univ to Moor St", -1.8885, 52.4835, -1.8936, 52.4795),
            new("Digbeth to Southside", -1.8865, 52.4765, -1.8965, 52.4745),
            new("Snow Hill to Colmore Row", -1.8995, 52.4830, -1.8975, 52.4815),
            new("Jewellery Quarter to City", -1.9125, 52.4855, -1.9015, 52.4805),
            new("Five Ways to Brindleyplace", -1.9165, 52.4745, -1.9105, 52.4785),
            new("Edgbaston to Mailbox", -1.9185, 52.4685, -1.9035, 52.4755),
            new("Curzon St to High St", -1.8855, 52.4815, -1.8925, 52.4805),
            new("Grand Central to Queensway", -1.8995, 52.4785, -1.8955, 52.4825)
        };

        _output.WriteLine("| Route Name | Base Dist (km) | Safe Dist (km) | Overhead (%) | Safety Score |");
        _output.WriteLine("| :--- | :--- | :--- | :--- | :--- |");

        int routeId = 0;
        foreach (var route in testRoutes)
        {
            routeId++;
            // Inject a unique hazard for variance in specific routes
            if (routeId == 3) // Aston
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Hazards.Add(new HazardReport { Location = new Point(route.StartX, route.StartY) { SRID = 4326 }, Type = "poor_lighting", Description = "Dark area", Status = HazardStatus.Reported });
                await db.SaveChangesAsync();
            }
            if (routeId == 9) // Curzon
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Hazards.Add(new HazardReport { Location = new Point(route.StartX, route.StartY) { SRID = 4326 }, Type = "broken_pavement", Description = "Damaged", Status = HazardStatus.Reported });
                await db.SaveChangesAsync();
            }

            // 1. Get Base Route (Low safety weight)
            var baseReq = new { Start = new { X = route.StartX, Y = route.StartY }, End = new { X = route.EndX, Y = route.EndY }, Profile = "standard", SafetyWeight = 0.0 };
            var baseResp = await client.PostAsJsonAsync("/api/v1/routing/safe-path", baseReq, JsonOptions);
            var baseData = await baseResp.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);

            // 2. Get Safe Route (High safety weight)
            var safeReq = new { Start = new { X = route.StartX, Y = route.StartY }, End = new { X = route.EndX, Y = route.EndY }, Profile = "standard", SafetyWeight = 0.9 };
            var safeResp = await client.PostAsJsonAsync("/api/v1/routing/safe-path", safeReq, JsonOptions);
            var safeData = await safeResp.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);

            double overhead = ((safeData!.Distance - baseData!.Distance) / baseData.Distance) * 100;
            _output.WriteLine($"| {route.Name} | {baseData.Distance/1000:F2} | {safeData.Distance/1000:F2} | {Math.Max(0, overhead):F1}% | {safeData.SafetyScore:F2} |");
        }
    }

    [Fact]
    public async Task AblationStudy_HazardImpact()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        double startX = -1.9050, startY = 52.4794; 
        double endX = -1.9060, endY = 52.4794;   
        
        _output.WriteLine("\n### Ablation Study: Hazard Removal Impact");
        
        // 1. No Hazard
        var req1 = new { Start = new { X = startX, Y = startY }, End = new { X = endX, Y = endY }, Profile = "standard", SafetyWeight = 1.0 };
        var resp1 = await client.PostAsJsonAsync("/api/v1/routing/safe-path", req1, JsonOptions);
        var data1 = await resp1.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        _output.WriteLine($"Baseline (No Hazards): Distance={data1!.Distance}m, Safety={data1.SafetyScore}");

        // 2. Add Hazard
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbContext.Hazards.Add(new HazardReport { 
                Id = Guid.NewGuid(), 
                Location = new Point(-1.9055, 52.4794) { SRID = 4326 }, 
                Type = "construction", 
                Description = "Test hazard",
                Status = HazardStatus.Reported 
            });
            await dbContext.SaveChangesAsync();
        }

        var resp2 = await client.PostAsJsonAsync("/api/v1/routing/safe-path", req1, JsonOptions);
        var content2 = await resp2.Content.ReadAsStringAsync();
        _output.WriteLine($"Response Content: {content2}");
        
        if (resp2.IsSuccessStatusCode)
        {
            var data2 = JsonSerializer.Deserialize<RouteResponse>(content2, JsonOptions);
            _output.WriteLine($"With Construction Hazard: Distance={data2!.Distance}m (Change: {data2.Distance - data1.Distance}m), Safety={data2.SafetyScore}");
        }
        else
        {
            _output.WriteLine($"API Error: {resp2.StatusCode}");
        }
    }
}
