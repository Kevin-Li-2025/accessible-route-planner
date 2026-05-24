using System.Text.Json;
using AccessCity.API.Configuration;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Extensions;

public static class RouteGraphProfileCommandExtensions
{
    public static async Task RunRouteGraphProfileCommandAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("RouteGraphProfile");
        var osmOptions = services.GetRequiredService<IOptions<OsmImportOptions>>().Value;
        var routingOptions = services.GetRequiredService<IOptions<RoutingOptions>>().Value;

        var request = new RouteGraphProfileRequest
        {
            HotReadsPerRoute = 1,
            Routes = routingOptions.RouteGraphWarmupRoutes.Select(route => new RouteGraphProfileRouteRequest
            {
                Name = route.Name,
                StartLat = route.StartLat,
                StartLng = route.StartLng,
                EndLat = route.EndLat,
                EndLng = route.EndLng
            }).ToList()
        };

        RouteGraphProfileResponse result;
        if (routingOptions.RouteGraphProfileUseOsmExtract && !string.IsNullOrWhiteSpace(osmOptions.FilePath))
        {
            logger.LogInformation("Profiling route graph directly from configured OSM extract: {FilePath}", osmOptions.FilePath);
            result = await services.GetRequiredService<IOsmRouteGraphExtractProfileService>()
                .ProfileAsync(osmOptions.FilePath, request, CancellationToken.None);
        }
        else
        {
            var routeGraphStatus = services.GetRequiredService<IRouteGraphStatusService>();
            var status = await routeGraphStatus.GetStatusAsync(CancellationToken.None);
            if ((osmOptions.ReplaceExisting || !status.HasCoverage) && !string.IsNullOrWhiteSpace(osmOptions.FilePath))
            {
                logger.LogInformation(
                    "Importing configured OSM extract before profiling (replaceExisting={ReplaceExisting}, hasCoverage={HasCoverage}).",
                    osmOptions.ReplaceExisting,
                    status.HasCoverage);
                await services.GetRequiredService<IOsmImportService>().ImportConfiguredAsync(CancellationToken.None);
            }

            result = await services.GetRequiredService<IRouteGraphProfileService>()
                .ProfileAsync(request, CancellationToken.None);
        }

        var json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

        if (!string.IsNullOrWhiteSpace(routingOptions.RouteGraphProfileOutputPath))
        {
            var outputPath = Path.GetFullPath(routingOptions.RouteGraphProfileOutputPath);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            await File.WriteAllTextAsync(outputPath, json);
            logger.LogInformation("Wrote route graph profile report to {OutputPath}", outputPath);
        }

        Console.WriteLine(json);

        if (routingOptions.RouteGraphProfileFailOnQualityGate && !result.QualityGatePassed)
        {
            throw new InvalidOperationException(
                "Route graph profile quality gate failed: " + string.Join("; ", result.QualityGateWarnings));
        }
    }
}
