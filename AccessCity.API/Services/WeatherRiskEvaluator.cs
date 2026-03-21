using AccessCity.API.Models.External;
using AccessCity.API.Services.External;
using Microsoft.Extensions.Caching.Memory;

namespace AccessCity.API.Services;

/// <summary>
/// Shared OpenWeather → risk [0,1] mapping used by <see cref="PredictiveRiskModel"/> and <see cref="RiskScoringService.PredictRiskAsync"/>.
/// </summary>
public static class WeatherRiskEvaluator
{
    public const string CacheKeyPrefix = "weather:";
    public static readonly TimeSpan DefaultCacheExpiry = TimeSpan.FromMinutes(15);

    public static async Task<double> GetRiskAsync(
        ILiveHazardClient? weatherClient,
        IMemoryCache? cache,
        double lat,
        double lon,
        TimeSpan? cacheExpiry = null)
    {
        if (weatherClient == null) return 0.1;

        string cacheKey = $"{CacheKeyPrefix}{Math.Round(lat, 2):F2}:{Math.Round(lon, 2):F2}";
        var expiry = cacheExpiry ?? DefaultCacheExpiry;

        if (cache?.TryGetValue(cacheKey, out double cached) == true)
            return cached;

        try
        {
            var weather = await weatherClient.GetCurrentWeatherAsync(lat, lon);
            double risk = ComputeRisk(weather);
            cache?.Set(cacheKey, risk, expiry);
            return risk;
        }
        catch
        {
            return 0.1;
        }
    }

    public static double GetCachedRisk(IMemoryCache? cache, double lat, double lon)
    {
        string cacheKey = $"{CacheKeyPrefix}{Math.Round(lat, 2):F2}:{Math.Round(lon, 2):F2}";
        if (cache?.TryGetValue(cacheKey, out double cached) == true)
            return cached;
        return 0.1;
    }

    public static double ComputeRisk(WeatherResponse? weather)
    {
        if (weather == null) return 0.1;

        double risk = 0.0;

        foreach (var condition in weather.Weather)
        {
            risk = Math.Max(risk, condition.Id switch
            {
                >= 200 and < 300 => 0.85,
                >= 300 and < 400 => 0.25,
                >= 500 and < 505 => 0.40,
                >= 505 and < 600 => 0.65,
                >= 600 and < 612 => 0.60,
                >= 612 and < 700 => 0.75,
                >= 700 and < 770 => 0.50,
                771 => 0.70,
                781 => 0.95,
                _ => 0.0
            });
        }

        if (weather.Wind.Speed > 15) risk = Math.Max(risk, 0.55);
        else if (weather.Wind.Speed > 10) risk = Math.Max(risk, 0.30);

        if (weather.Main.Temp < 0) risk = Math.Max(risk, 0.45);
        if (weather.Main.Temp < -5) risk = Math.Max(risk, 0.65);

        return Math.Clamp(risk, 0, 1);
    }
}
