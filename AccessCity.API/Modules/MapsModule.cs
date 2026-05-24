using AccessCity.API.Services;

namespace AccessCity.API.Modules;

public static class MapsModule
{
    public static IServiceCollection AddMapsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IBloomFilterService, BloomFilterService>();

        services.AddScoped<IMapTileService, MapTileService>();
        services.AddScoped<ISpatialQueryService, SpatialQueryService>();
        services.AddScoped<IAccessibilityVerificationService, AccessibilityVerificationService>();
        services.AddScoped<IOfflineMapBundleService, OfflineMapBundleService>();

        if (configuration.GetValue("Workers:TileWarming:Enabled", true))
        {
            services.AddHostedService<AccessCity.API.Services.Background.TileWarmingBackgroundService>();
        }

        return services;
    }
}
