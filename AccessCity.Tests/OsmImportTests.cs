using System.Net.Http.Json;
using AccessCity.API.Data;
using AccessCity.API.Models;
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

        var latestRun = await dbContext.FeedIngestionRuns
            .OrderByDescending(run => run.Id)
            .FirstAsync();

        Assert.Equal("completed", latestRun.Status);
        Assert.True(latestRun.RecordsInserted > 0);
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

        var overlayResponse = await client.GetAsync("/api/v1/spatial/map-overlay?layerName=infrastructure");
        overlayResponse.EnsureSuccessStatusCode();
        var overlayContent = await overlayResponse.Content.ReadAsStringAsync();
        Assert.Contains("FeatureCollection", overlayContent);
        Assert.Contains("infrastructure", overlayContent);
    }
}
