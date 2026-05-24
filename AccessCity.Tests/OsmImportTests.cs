using System.Net.Http.Json;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace AccessCity.Tests;

public class OsmImportTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;

    public OsmImportTests(AccessCityApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ImportJobsEndpoint_Queues_Background_Osm_Import()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/admin/osm/import-jobs", content: null);
        response.EnsureSuccessStatusCode();

        var job = await response.Content.ReadFromJsonAsync<AccessCity.API.Models.DTOs.OsmImportJobResponse>();
        Assert.NotNull(job);
        Assert.Equal("queued", job!.Status);
        Assert.NotEqual(Guid.Empty, job.JobId);

        var statusResponse = await client.GetAsync($"/api/v1/admin/osm/import-jobs/{job.JobId}");
        statusResponse.EnsureSuccessStatusCode();

        var status = await statusResponse.Content.ReadFromJsonAsync<AccessCity.API.Models.DTOs.OsmImportJobResponse>();
        Assert.NotNull(status);
        Assert.Equal(job.JobId, status!.JobId);
        Assert.Contains(status.Status, new[] { "queued", "running", "completed" });
    }

    [Fact]
    public async Task ImportEndpoint_Persists_RouteGraph_Infrastructure_And_Run_Audit()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/api/v1/admin/osm/import", content: null);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OsmImportResult>();
        Assert.NotNull(result);
        Assert.True(result!.RouteNodesInserted > 0);
        Assert.True(result.RouteEdgesInserted > 0);
        Assert.True(result.InfrastructureAssetsInserted > 0);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True(await dbContext.RouteNodes.CountAsync() > 0);
        Assert.True(await dbContext.RouteEdges.CountAsync() > 0);
        Assert.True(await dbContext.InfrastructureAssets.CountAsync() > 0);

        var accessibleToilet = await dbContext.InfrastructureAssets
            .Where(asset => asset.SourceRecordId == "node:1004")
            .FirstAsync();
        var accessibilityProfile = AccessibilityProfileMapper.Parse(accessibleToilet.AccessibilityProfile);
        Assert.Equal(AccessibilityProfileMapper.SchemaVersion, accessibilityProfile.SchemaVersion);
        Assert.Equal("osm", accessibilityProfile.SourceSystem);
        Assert.Equal("node:1004", accessibilityProfile.SourceRecordId);
        Assert.NotNull(accessibilityProfile.LastVerifiedAtUtc);
        Assert.True(accessibilityProfile.Confidence >= 0.75);
        Assert.Equal("verified", accessibilityProfile.VerificationStatus);
        Assert.Single(accessibilityProfile.Entrances);
        Assert.Single(accessibilityProfile.Restrooms);
        Assert.Single(accessibilityProfile.Photos);
        Assert.True(accessibilityProfile.Entrances[0].StepFree);
        Assert.Equal(0.9, accessibilityProfile.Entrances[0].DoorWidthMetres.GetValueOrDefault(), precision: 1);
        Assert.True(accessibilityProfile.Restrooms[0].WheelchairAccessible);
        Assert.True(accessibilityProfile.Restrooms[0].HasGrabBars);
        Assert.DoesNotContain("toilets_wheelchair_access", accessibilityProfile.MissingFields);

        var accessibleEdge = await dbContext.RouteEdges
            .Where(edge => edge.SourceWayId == 2001)
            .FirstAsync();

        Assert.Equal("asphalt", accessibleEdge.SurfaceType);
        Assert.Equal("good", accessibleEdge.Smoothness);
        Assert.Equal(1.8, accessibleEdge.WidthMetres.GetValueOrDefault(), precision: 1);
        Assert.Equal(0, accessibleEdge.KerbHeight);
        Assert.True(accessibleEdge.HasTactilePaving);
        Assert.NotNull(accessibleEdge.Access);
        Assert.Contains("wheelchair=yes", accessibleEdge.Access);
        Assert.Equal(1, accessibleEdge.AccessibilityCostVersion);
        Assert.Equal(0, accessibleEdge.WheelchairAccessibilityPenaltySeconds, precision: 6);
        Assert.True(accessibleEdge.AccessibilityDataQuality > 0.9);

        var incompleteEdge = await dbContext.RouteEdges
            .Where(edge => edge.SourceWayId == 2002)
            .FirstAsync();

        Assert.True(incompleteEdge.WheelchairAccessibilityPenaltySeconds > incompleteEdge.StandardAccessibilityPenaltySeconds);
        Assert.True(incompleteEdge.AccessibilityDataQuality < accessibleEdge.AccessibilityDataQuality);

        var latestRun = await dbContext.FeedIngestionRuns
            .OrderByDescending(run => run.Id)
            .FirstAsync();

        Assert.Equal("completed", latestRun.Status);
        Assert.True(latestRun.RecordsInserted > 0);
    }

    [Fact]
    public async Task ImportService_Rejects_Missing_Osm_File()
    {
        using var scope = _factory.Services.CreateScope();
        var importService = scope.ServiceProvider.GetRequiredService<IOsmImportService>();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            importService.ImportAsync("/tmp/accesscity-missing-route-graph.osm"));
    }

    [Fact]
    public async Task OfflineRouteGraphExtractProfile_Builds_Packed_Alt_Artifact()
    {
        using var scope = _factory.Services.CreateScope();
        var profileService = scope.ServiceProvider.GetRequiredService<IOsmRouteGraphExtractProfileService>();

        var result = await profileService.ProfileAsync(
            _factory.OsmFixturePath,
            new RouteGraphProfileRequest
            {
                HotReadsPerRoute = 1,
                Routes =
                {
                    new RouteGraphProfileRouteRequest
                    {
                        Name = "fixture-core",
                        StartLat = 52.4862,
                        StartLng = -1.8904,
                        EndLat = 52.4862,
                        EndLng = -1.8894
                    }
                }
            });

        Assert.Equal("osm-extract-offline", result.SourceType);
        Assert.True(result.SourceRecordsSeen > 0);
        Assert.True(result.SourceNodeCount > 0);
        Assert.True(result.SourceEdgeCount > 0);
        Assert.True(result.SourceShardCount > 0);
        var route = Assert.Single(result.Routes);
        Assert.True(route.NodeCount > 0);
        Assert.True(route.EdgeCount > 0);
        Assert.True(route.ArtifactBytes > 0);
        Assert.True(route.RedisPayloadBytes > 0);
        Assert.True(route.HotLoadMilliseconds >= 0);
        Assert.True(route.HasAltPreprocessing);
    }

    [Fact]
    public async Task RouteGraphStatus_Reports_Coverage_After_Import()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var response = await client.GetAsync("/api/v1/routing/route-graph/status");
        response.EnsureSuccessStatusCode();

        var status = await response.Content.ReadFromJsonAsync<RouteGraphCoverageStatus>();
        Assert.NotNull(status);
        Assert.True(status!.HasCoverage);
        Assert.True(status.RouteNodeCount > 0);
        Assert.True(status.RouteEdgeCount > 0);
        Assert.Contains("osm:", status.Version, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpatialEndpoints_Return_Imported_Osm_Poi_And_Overlay_Data()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);

        var poiResponse = await client.GetAsync("/api/v1/spatial/poi?lat=52.48625&lng=-1.8899&radius=250");
        poiResponse.EnsureSuccessStatusCode();
        var poiContent = await poiResponse.Content.ReadAsStringAsync();
        Assert.Contains("amenity", poiContent);
        Assert.Contains("accessibilityProfile", poiContent);
        Assert.Contains("restrooms", poiContent);
        Assert.Contains("lastVerifiedAtUtc", poiContent);

        var overlayResponse = await client.GetAsync("/api/v1/spatial/map-overlay?layerName=infrastructure");
        overlayResponse.EnsureSuccessStatusCode();
        var overlayContent = await overlayResponse.Content.ReadAsStringAsync();
        Assert.Contains("FeatureCollection", overlayContent);
        Assert.Contains("infrastructure", overlayContent);
        Assert.Contains("accessibilityProfile", overlayContent);
    }
}
