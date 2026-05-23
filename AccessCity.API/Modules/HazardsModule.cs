using AccessCity.API.Configuration;
using AccessCity.API.Services;

namespace AccessCity.API.Modules;

public static class HazardsModule
{
    public static IServiceCollection AddHazardsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiEnrichmentOptions>(configuration.GetSection(AiEnrichmentOptions.SectionName));

        services.AddSingleton<ISpatialCacheService, SpatialCacheService>();

        services.AddScoped<IRealHazardDataService, RealHazardDataService>();
        services.AddScoped<IHazardReportService, HazardReportService>();
        services.AddScoped<IAiAssistService, AiAssistService>();
        services.AddScoped<IDashboardQueryService, DashboardQueryService>();

        return services;
    }
}
