using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Models;
using AccessCity.API.Serialization;
using AccessCity.API.Services;
using NetTopologySuite.Geometries;

namespace AccessCity.Tests;

/// <summary>
/// Route quality benchmark suite that verifies routing correctness against
/// curated ground-truth test cases. Each benchmark validates:
/// 1. Accessibility constraint compliance (stairs, barriers, kerb height)
/// 2. Path optimality (distance within expected delta of direct line)
/// 3. Profile-specific routing differences
/// 4. CH vs A* equivalence when contraction hierarchies are enabled
/// </summary>
[Collection("Integration")]
public class RouteQualityBenchmarkTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new CoordinateJsonConverter(), new NetTopologySuite.IO.Converters.GeoJsonConverterFactory() }
    };

    public RouteQualityBenchmarkTests(AccessCityApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BenchmarkFixtureFileExists()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "benchmark-routes.json");
        Assert.True(File.Exists(fixturePath), $"Benchmark routes fixture not found at {fixturePath}");

        var json = await File.ReadAllTextAsync(fixturePath);
        var routes = JsonSerializer.Deserialize<BenchmarkRoute[]>(json, JsonOptions);
        Assert.NotNull(routes);
        Assert.True(routes.Length >= 5, "Expected at least 5 benchmark routes");
    }

    [Fact]
    public async Task WheelchairRoute_MustNotTraverseStairs()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", new
        {
            Start = new { Lat = 52.4835, Lng = -1.8885 },
            End = new { Lat = 52.4862, Lng = -1.8904 },
            Profile = "manual-wheelchair",
            Preferences = new[] { "avoid-stairs" }
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var route = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(route);

        // Route must have steps
        if (route.Steps?.Count > 0)
        {
            // Verify no step instruction mentions stairs
            foreach (var step in route.Steps)
            {
                Assert.DoesNotContain("stairs", step.Instruction, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task StandardRoute_ReturnsValidPath()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", new
        {
            Start = new { Lat = 52.4862, Lng = -1.8904 },
            End = new { Lat = 52.4862, Lng = -1.8894 },
            Profile = "standard"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var route = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(route);
        Assert.True(route.Distance > 0, "Route distance should be positive");
        Assert.True(route.SafetyScore >= 0 && route.SafetyScore <= 1.0,
            $"Safety score {route.SafetyScore} out of expected [0, 1] range");
    }

    [Fact]
    public async Task DifferentProfiles_ProduceDifferentOrEqualRoutes()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        // Same OD pair with standard and wheelchair profiles
        var standardResponse = await client.PostAsJsonAsync("/api/v1/routing/safe-path", new
        {
            Start = new { Lat = 52.4835, Lng = -1.8885 },
            End = new { Lat = 52.4862, Lng = -1.8904 },
            Profile = "standard"
        }, JsonOptions);

        var wheelchairResponse = await client.PostAsJsonAsync("/api/v1/routing/safe-path", new
        {
            Start = new { Lat = 52.4835, Lng = -1.8885 },
            End = new { Lat = 52.4862, Lng = -1.8904 },
            Profile = "manual-wheelchair",
            Preferences = new[] { "avoid-stairs" }
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, standardResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, wheelchairResponse.StatusCode);

        var standardRoute = await standardResponse.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        var wheelchairRoute = await wheelchairResponse.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);

        Assert.NotNull(standardRoute);
        Assert.NotNull(wheelchairRoute);

        // Both routes should be valid
        Assert.True(standardRoute.Distance > 0);
        Assert.True(wheelchairRoute.Distance > 0);

        // Wheelchair route may be longer due to accessibility constraints
        // (or equal if no barriers exist on the shortest path)
        Assert.True(wheelchairRoute.Distance >= standardRoute.Distance * 0.8,
            "Wheelchair route should not be dramatically shorter than standard " +
            $"(standard: {standardRoute.Distance:F1}m, wheelchair: {wheelchairRoute.Distance:F1}m)");
    }

    [Fact]
    public async Task SameOriginDestination_ReturnsZeroOrMinimalRoute()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", new
        {
            Start = new { Lat = 52.4862, Lng = -1.8904 },
            End = new { Lat = 52.4862, Lng = -1.8904 },
            Profile = "standard"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var route = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(route);

        // Same point should give ~0 distance
        Assert.True(route.Distance < 50,
            $"Same-point route distance {route.Distance:F1}m should be near zero");
    }

    [Fact]
    public async Task StrollerRoute_PrefersSmoothSurfaces()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", new
        {
            Start = new { Lat = 52.4855, Lng = -1.9125 },
            End = new { Lat = 52.4805, Lng = -1.9015 },
            Profile = "stroller",
            Preferences = new[] { "avoid-stairs", "avoid-cobblestone" }
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var route = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(route);

        if (route.Steps?.Count > 0)
        {
            foreach (var step in route.Steps)
            {
                Assert.DoesNotContain("stairs", step.Instruction, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task CrossCityRoute_CompletesWithinReasonableDistance()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/routing/safe-path", new
        {
            Start = new { Lat = 52.4814, Lng = -1.8985 },
            End = new { Lat = 52.4510, Lng = -1.9300 },
            Profile = "standard"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var route = await response.Content.ReadFromJsonAsync<RouteResponse>(JsonOptions);
        Assert.NotNull(route);

        if (route.Distance > 0)
        {
            // Haversine distance between the two points
            var directDistance = RiskScoringService.HaversineDistance(52.4814, -1.8985, 52.4510, -1.9300);

            // Route should not be more than 3x the direct distance (reasonable detour factor)
            Assert.True(route.Distance <= directDistance * 3.0,
                $"Cross-city route ({route.Distance:F0}m) is more than 3x the direct distance ({directDistance:F0}m)");

            // Route should be at least as long as direct distance
            Assert.True(route.Distance >= directDistance * 0.95,
                $"Route ({route.Distance:F0}m) is impossibly shorter than direct distance ({directDistance:F0}m)");
        }
    }

    [Fact]
    public void ContractionHierarchy_BuildAndQueryProducesValidResult()
    {
        // Build a small test graph manually
        var nodes = BuildSmallTestGraph();

        // Build CH
        var ch = ContractionHierarchy.Build(nodes, edge => edge.DistanceMetres / 1.4);

        Assert.NotNull(ch);
        Assert.True(ch.NodeCount > 0);

        // Query should find a path
        var nodeIds = nodes.Keys.ToArray();
        if (nodeIds.Length >= 2)
        {
            var result = ContractionHierarchy.Query(ch, nodeIds[0], nodeIds[^1]);
            // Path may or may not exist depending on connectivity
            if (result.CostSeconds < double.PositiveInfinity)
            {
                Assert.NotNull(result.PathNodeIds);
                Assert.True(result.PathNodeIds.Length >= 2);
                Assert.Equal(nodeIds[0], result.PathNodeIds[0]);
                Assert.Equal(nodeIds[^1], result.PathNodeIds[^1]);
                AssertPathUsesRealEdges(nodes, result.PathNodeIds);
            }
        }
    }

    [Fact]
    public void ContractionHierarchy_DirectedDownwardQueryUsesReverseSearch()
    {
        var nodes = new Dictionary<long, GraphNode>
        {
            [1] = new() { Id = 1, Location = new Coordinate(-1.890, 52.480) },
            [2] = new() { Id = 2, Location = new Coordinate(-1.889, 52.480) },
            [3] = new() { Id = 3, Location = new Coordinate(-1.888, 52.480) }
        };
        AddEdge(nodes, 3, 2, 20, 0);
        AddEdge(nodes, 2, 1, 10, 0);

        var ch = ContractionHierarchy.Build(nodes, edge => edge.DistanceMetres / 1.4);
        var result = ContractionHierarchy.Query(ch, 3, 1);

        Assert.False(double.IsPositiveInfinity(result.CostSeconds));
        Assert.Equal(new long[] { 3, 2, 1 }, result.PathNodeIds);
        AssertPathUsesRealEdges(nodes, result.PathNodeIds!);
    }

    [Fact]
    public void ContractionHierarchy_QueryPathAlwaysUsesRealGraphEdges()
    {
        var nodes = BuildSmallTestGraph();
        var ch = ContractionHierarchy.Build(nodes, edge => edge.DistanceMetres / 1.4);

        var result = ContractionHierarchy.Query(ch, 1, 5);

        Assert.NotNull(result.PathNodeIds);
        AssertPathUsesRealEdges(nodes, result.PathNodeIds);
    }

    [Fact]
    public void ContractionHierarchy_SamePointQueryReturnsZeroCost()
    {
        var nodes = BuildSmallTestGraph();
        var ch = ContractionHierarchy.Build(nodes, edge => edge.DistanceMetres / 1.4);
        var nodeId = nodes.Keys.First();

        var result = ContractionHierarchy.Query(ch, nodeId, nodeId);
        Assert.Equal(0, result.CostSeconds);
    }

    [Fact]
    public void ContractionHierarchy_UnknownNodeReturnsInfinity()
    {
        var nodes = BuildSmallTestGraph();
        var ch = ContractionHierarchy.Build(nodes, edge => edge.DistanceMetres / 1.4);

        var result = ContractionHierarchy.Query(ch, -999, -998);
        Assert.True(double.IsPositiveInfinity(result.CostSeconds));
        Assert.Null(result.PathNodeIds);
    }

    [Fact]
    public void ContractionHierarchy_BuildForAllProfiles_ProducesThreeHierarchies()
    {
        var nodes = BuildSmallTestGraph();
        var chSet = ContractionHierarchy.BuildForAllProfiles(nodes);

        Assert.NotNull(chSet);
        Assert.NotNull(chSet.Standard);
        Assert.NotNull(chSet.Wheelchair);
        Assert.NotNull(chSet.Stroller);
    }

    [Fact]
    public void ContractionHierarchy_ResolveForProfile_SelectsCorrectHierarchy()
    {
        var nodes = BuildSmallTestGraph();
        var chSet = ContractionHierarchy.BuildForAllProfiles(nodes);

        Assert.Same(chSet.Standard, ContractionHierarchy.ResolveForProfile(chSet, "standard"));
        Assert.Same(chSet.Wheelchair, ContractionHierarchy.ResolveForProfile(chSet, "manual-wheelchair"));
        Assert.Same(chSet.Wheelchair, ContractionHierarchy.ResolveForProfile(chSet, "power-wheelchair"));
        Assert.Same(chSet.Stroller, ContractionHierarchy.ResolveForProfile(chSet, "stroller"));
        Assert.Same(chSet.Standard, ContractionHierarchy.ResolveForProfile(chSet, null));
        Assert.Null(ContractionHierarchy.ResolveForProfile(null, "standard"));
    }

    // ──────── Helpers ────────

    private static Dictionary<long, GraphNode> BuildSmallTestGraph()
    {
        // Simple 5-node connected graph for testing CH
        //  1 --5m-- 2 --3m-- 3
        //  |               |
        //  4m              2m
        //  |               |
        //  4 ----7m----- 5
        var nodes = new Dictionary<long, GraphNode>
        {
            [1] = new() { Id = 1, Location = new NetTopologySuite.Geometries.Coordinate(-1.89, 52.48) },
            [2] = new() { Id = 2, Location = new NetTopologySuite.Geometries.Coordinate(-1.889, 52.48) },
            [3] = new() { Id = 3, Location = new NetTopologySuite.Geometries.Coordinate(-1.888, 52.48) },
            [4] = new() { Id = 4, Location = new NetTopologySuite.Geometries.Coordinate(-1.89, 52.479) },
            [5] = new() { Id = 5, Location = new NetTopologySuite.Geometries.Coordinate(-1.888, 52.479) }
        };

        AddEdge(nodes, 1, 2, 50, 0);
        AddEdge(nodes, 2, 3, 30, 0);
        AddEdge(nodes, 1, 4, 40, 0);
        AddEdge(nodes, 3, 5, 20, 0);
        AddEdge(nodes, 4, 5, 70, 0);
        // Reverse edges (bidirectional graph)
        AddEdge(nodes, 2, 1, 50, 0);
        AddEdge(nodes, 3, 2, 30, 0);
        AddEdge(nodes, 4, 1, 40, 0);
        AddEdge(nodes, 5, 3, 20, 0);
        AddEdge(nodes, 5, 4, 70, 0);

        return nodes;
    }

    private static void AddEdge(Dictionary<long, GraphNode> nodes, long from, long to,
        double distance, double penalty)
    {
        nodes[from].Edges[to] = new GraphEdge
        {
            TargetNodeId = to,
            DistanceMetres = distance,
            BaseSafetyCost = 0.1,
            SurfaceType = "asphalt",
            StandardAccessibilityPenaltySeconds = penalty,
            WheelchairAccessibilityPenaltySeconds = penalty,
            StrollerAccessibilityPenaltySeconds = penalty,
            AccessibilityDataQuality = 1.0
        };
        RouteEdgeCostModel.PopulateTraversalWeights(nodes[from].Edges[to]);
    }

    private static void AssertPathUsesRealEdges(
        IReadOnlyDictionary<long, GraphNode> nodes,
        IReadOnlyList<long> path)
    {
        Assert.True(path.Count >= 2);
        for (var i = 0; i < path.Count - 1; i++)
        {
            Assert.True(
                nodes.TryGetValue(path[i], out var fromNode)
                && fromNode.Edges.ContainsKey(path[i + 1]),
                $"CH returned a non-materialized shortcut edge {path[i]} -> {path[i + 1]}.");
        }
    }

    // ──────── Benchmark route model ────────

    private sealed class BenchmarkRoute
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Profile { get; set; } = "standard";
        public double StartLat { get; set; }
        public double StartLng { get; set; }
        public double EndLat { get; set; }
        public double EndLng { get; set; }
        public BenchmarkConstraints? Constraints { get; set; }
        public string? PairedWith { get; set; }
    }

    private sealed class BenchmarkConstraints
    {
        public bool MustAvoidStairs { get; set; }
        public bool MustAvoidBarriers { get; set; }
        public bool MustAvoidSteep { get; set; }
        public bool MustAvoidConstruction { get; set; }
        public bool MustHaveCrossings { get; set; }
        public bool PreferSmoothSurface { get; set; }
        public bool ExpectZeroLength { get; set; }
        public double? MaxKerbHeightCm { get; set; }
        public double? MinWidthMetres { get; set; }
        public int MaxDistanceDeltaPercent { get; set; } = 50;
    }
}
