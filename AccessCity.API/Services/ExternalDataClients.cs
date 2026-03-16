using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Models.External;

namespace AccessCity.API.Services.External
{
    /// <summary>
    /// Service for fetching precise street accessibility and physical hazard data
    /// (e.g. stairs, tactile paving, cobblestone paths, lighting).
    /// </summary>
    public interface IOpenStreetMapClient
    {
        Task<List<OverpassElement>?> GetInfrastructureDataAsync(double minLat, double minLng, double maxLat, double maxLng);
    }

    public class OverpassApiClient : IOpenStreetMapClient
    {
        private readonly HttpClient _httpClient;

        public OverpassApiClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
            // Overpass API asks for a User-Agent header politely identifying the application
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AccessCity-UniversityProject/1.0");
        }

        public async Task<List<OverpassElement>?> GetInfrastructureDataAsync(double minLat, double minLng, double maxLat, double maxLng)
        {
            // Building a Bounding-Box query for Overpass QL
            // Looking specifically for footways, sidewalks, stairs, and roads indicating wheelchair access
            var overpassQuery = $@"
                [out:json][timeout:25];
                (
                  way[""highway""=""footway""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""highway""=""steps""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""sidewalk""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""lit""=""yes""]({minLat},{minLng},{maxLat},{maxLng});
                );
                out center;
            ";

            var response = await _httpClient.PostAsync(
                "https://overpass-api.de/api/interpreter", 
                new StringContent(overpassQuery));

            if (!response.IsSuccessStatusCode) return null;

            var data = await response.Content.ReadFromJsonAsync<OverpassResponse>();
            return data?.Elements;
        }
    }

    /// <summary>
    /// Service for querying historical crime data in the UK. 
    /// Used by predictive risk scoring engine to inflate risk on notoriously dangerous streets.
    /// </summary>
    public interface IUkPoliceDataClient
    {
        Task<List<StreetCrimeRecord>?> GetRecentStreetCrimesAsync(double latitude, double longitude);
    }

    public class UkPoliceDataClient : IUkPoliceDataClient
    {
        private readonly HttpClient _httpClient;

        public UkPoliceDataClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri("https://data.police.uk/api/");
        }

        public async Task<List<StreetCrimeRecord>?> GetRecentStreetCrimesAsync(double latitude, double longitude)
        {
            try
            {
                // Note: The Police data API is notoriously slow/sometimes down.
                // In production, this should be heavily cached (e.g., IMemoryCache for 24 hours).
                return await _httpClient.GetFromJsonAsync<List<StreetCrimeRecord>>(
                    $"crimes-street/all-crime?lat={latitude}&lng={longitude}");
            }
            catch
            {
                return new List<StreetCrimeRecord>(); // Fail silently for external API downtime
            }
        }
    }

    /// <summary>
    /// Service for finding "Safe Havens" (24/7 convenience stores, police stations, hospitals).
    /// Used for Feature F-3 (Safe Haven Routing).
    /// </summary>
    public interface ISafeHavenPlacesClient
    {
        Task<List<Place>?> GetNearbySafeHavensAsync(double latitude, double longitude, double radiusMetres);
    }

    public class GooglePlacesClient : ISafeHavenPlacesClient
    {
        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;

        public GooglePlacesClient(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _apiKey = config["GooglePlaces:ApiKey"];
            
            _httpClient.DefaultRequestHeaders.Add("X-Goog-Api-Key", _apiKey ?? "");
            _httpClient.DefaultRequestHeaders.Add("X-Goog-FieldMask", "places.id,places.displayName,places.types,places.location,places.regularOpeningHours");
        }

        public async Task<List<Place>?> GetNearbySafeHavensAsync(double latitude, double longitude, double radiusMetres)
        {
            if (string.IsNullOrEmpty(_apiKey)) return new List<Place>();

            var requestBody = new PlacesSearchRequest
            {
                IncludedTypes = new List<string> { "police", "hospital", "convenience_store", "gas_station" },
                MaxResultCount = 20,
                LocationRestriction = new LocationRestriction
                {
                    Circle = new CircleRestriction
                    {
                        Center = new LatLng { Latitude = latitude, Longitude = longitude },
                        Radius = radiusMetres
                    }
                }
            };

            var response = await _httpClient.PostAsJsonAsync("https://places.googleapis.com/v1/places:searchNearby", requestBody);
            
            if (!response.IsSuccessStatusCode) return new List<Place>();

            var result = await response.Content.ReadFromJsonAsync<PlacesSearchResponse>();
            
            // Further filter locally for stores that are "OpenNow" if we need immediate safe havens.
            return result?.Places;
        }
    }

    /// <summary>
    /// Service for live weather data.
    /// E.g., heavy rain or snow increases accessibility risk for wheelchairs or visual impairment.
    /// </summary>
    public interface ILiveHazardClient
    {
        Task<WeatherResponse?> GetCurrentWeatherAsync(double latitude, double longitude);
    }

    public class OpenWeatherClient : ILiveHazardClient
    {
        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;

        public OpenWeatherClient(HttpClient httpClient, IConfiguration config)
        {
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri("https://api.openweathermap.org/data/2.5/");
            _apiKey = config["OpenWeather:ApiKey"];
        }

        public async Task<WeatherResponse?> GetCurrentWeatherAsync(double latitude, double longitude)
        {
            if (string.IsNullOrEmpty(_apiKey)) return null;

            try
            {
                return await _httpClient.GetFromJsonAsync<WeatherResponse>(
                    $"weather?lat={latitude}&lon={longitude}&appid={_apiKey}&units=metric");
            }
            catch
            {
                return null;
            }
        }
    }
}
