using AccessCity.API.Configuration;
using AccessCity.API.Services;

namespace AccessCity.API.Modules;

public static class RoutingModule
{
    public static IServiceCollection AddRoutingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RoutingOptions>(configuration.GetSection(RoutingOptions.SectionName));

        services.AddSingleton<HazardSpatialIndex>();
        services.AddSingleton<IHazardSpatialIndex>(sp => sp.GetRequiredService<HazardSpatialIndex>());
        services.AddSingleton<H3HazardRiskGrid>();
        services.AddSingleton<IHazardRiskGrid>(sp => sp.GetRequiredService<H3HazardRiskGrid>());
        services.AddHostedService<HazardSpatialIndexRefreshBackgroundService>();

        services.AddSingleton<IRouteCoalescingService, RouteCoalescingService>();
        services.AddSingleton<IRouteComputationLimiter, RouteComputationLimiter>();
        services.AddSingleton<RouteJobService>();
        services.AddSingleton<IRouteJobService>(sp => sp.GetRequiredService<RouteJobService>());
        services.AddSingleton<IRouteJobDispatchQueue>(sp => sp.GetRequiredService<RouteJobService>());
        services.AddHostedService<AccessCity.API.Services.Background.RouteJobDispatchBackgroundService>();

        services.AddScoped<IRouteGraphRepository, RouteGraphRepository>();
        services.AddScoped<IRouteGraphProfileService, RouteGraphProfileService>();
        services.AddScoped<IOsmRouteGraphExtractProfileService, OsmRouteGraphExtractProfileService>();
        services.AddSingleton<IRouteGraphArtifactStore, RouteGraphArtifactStore>();
        services.AddScoped<IRouteGraphStatusService, RouteGraphStatusService>();
        services.AddScoped<IRouteCacheService, RouteCacheService>();
        services.AddScoped<IRouteOptionsCacheService, RouteOptionsCacheService>();
        services.AddScoped<IHazardQueryService, HazardQueryService>();
        services.AddScoped<IAccessibilityPlanningService, AccessibilityPlanningService>();

        services.AddScoped<RoutingService>();
        services.AddScoped<IRoutingService>(sp => sp.GetRequiredService<RoutingService>());

        if (configuration.GetValue("Workers:Routing:Enabled", false))
        {
            services.AddHostedService<AccessCity.API.Services.Background.RouteJobBackgroundService>();
        }

        if (configuration.GetValue("Routing:RouteGraphWarmupEnabled", false))
        {
            services.AddHostedService<AccessCity.API.Services.Background.RouteGraphWarmupBackgroundService>();
        }

        services.AddSingleton<RouteGraphReleasePipeline>();

        if (configuration.GetValue("Routing:RouteGraphFileArtifactWarmupEnabled", false))
        {
            services.AddHostedService<AccessCity.API.Services.Background.RouteGraphArtifactManifestWarmupBackgroundService>();
        }

        return services;
    }
}
