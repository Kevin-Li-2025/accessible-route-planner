using AccessCity.API.Configuration;
using AccessCity.API.Services;

namespace AccessCity.API.Modules;

public static class RoutingModule
{
    public static IServiceCollection AddRoutingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RoutingOptions>(configuration.GetSection(RoutingOptions.SectionName));

        services.AddSingleton<IRouteCoalescingService, RouteCoalescingService>();
        services.AddSingleton<IRouteComputationLimiter, RouteComputationLimiter>();
        services.AddSingleton<IRouteJobService, RouteJobService>();

        services.AddScoped<IRouteGraphRepository, RouteGraphRepository>();
        services.AddScoped<IRouteCacheService, RouteCacheService>();
        services.AddScoped<IHazardQueryService, HazardQueryService>();

        services.AddScoped<RoutingService>();
        services.AddScoped<IRoutingService>(sp => sp.GetRequiredService<RoutingService>());

        if (configuration.GetValue("Workers:Routing:Enabled", false))
        {
            services.AddHostedService<AccessCity.API.Services.Background.RouteJobBackgroundService>();
        }

        return services;
    }
}
