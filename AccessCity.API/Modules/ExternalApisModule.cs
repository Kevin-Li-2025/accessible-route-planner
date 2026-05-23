using AccessCity.API.Services;
using AccessCity.API.Services.External;

namespace AccessCity.API.Modules;

public static class ExternalApisModule
{
    public static IServiceCollection AddExternalApisModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IExternalDependencyGuard, ExternalDependencyGuard>();

        var osrmTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:Osrm:TimeoutSeconds", 3));
        var overpassTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:Overpass:TimeoutSeconds", 5));
        var policeTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:UkPolice:TimeoutSeconds", 2));
        var placesTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:GooglePlaces:TimeoutSeconds", 6));
        var weatherTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:OpenWeather:TimeoutSeconds", 5));
        var environmentalTimeout = TimeSpan.FromSeconds(configuration.GetValue("ExternalApis:Environmental:TimeoutSeconds", 3));
        var nominatimTimeout = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("ExternalApis:Nominatim:TimeoutSeconds", 2)));

        // Tail-sensitive dependencies keep bounded timeouts and avoid retry amplification on hot paths.
        services.AddHttpClient<IOsrmClient, OsrmClient>(client =>
        {
            client.Timeout = osrmTimeout;
        });

        services.AddHttpClient<IOpenStreetMapClient, OverpassApiClient>()
            .ConfigureHttpClient(c => c.Timeout = overpassTimeout);

        services.AddHttpClient("Nominatim", c =>
        {
            c.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
            c.DefaultRequestHeaders.Add("User-Agent", "AccessCity-App/1.0");
            c.Timeout = nominatimTimeout;
        });

        services.AddHttpClient<IUkPoliceDataClient, UkPoliceDataClient>(client =>
        {
            client.Timeout = policeTimeout;
        });

        services.AddHttpClient<ISafeHavenPlacesClient, GooglePlacesClient>(client =>
        {
            client.Timeout = placesTimeout;
        })
        .AddStandardResilienceHandler();

        services.AddHttpClient<ILiveHazardClient, OpenWeatherClient>(client =>
        {
            client.Timeout = weatherTimeout;
        })
        .AddStandardResilienceHandler();

        services.AddHttpClient<IEnvironmentalDataClient, EnvironmentalDataClient>()
            .ConfigureHttpClient(c => c.Timeout = environmentalTimeout);

        return services;
    }
}
