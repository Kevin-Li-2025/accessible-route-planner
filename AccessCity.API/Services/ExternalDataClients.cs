using System.Net.Http.Json;
using System.Text.Json;
using AccessCity.API.Exceptions;
using AccessCity.API.Models.External;
using Microsoft.Extensions.Configuration;

namespace AccessCity.API.Services.External
{
    /// <summary>
    /// Service for fetching precise street accessibility and physical hazard data
    /// (e.g. stairs, tactile paving, cobblestone paths, lighting).
    /// </summary>
    public interface IOpenStreetMapClient
    {
        /// <summary>Fetches real OSM features that represent hazards/barriers: barriers, steps, poor surface.</summary>
        Task<List<OverpassElement>?> GetHazardLikeDataAsync(double minLat, double minLng, double maxLat, double maxLng);
    }

    public class OverpassApiClient : IOpenStreetMapClient
    {
        private const string DefaultOverpassEndpoint = "https://overpass-api.de/api/interpreter";
        private readonly HttpClient _httpClient;
        private readonly ILogger<OverpassApiClient> _logger;
        private readonly string _overpassEndpoint;

        public OverpassApiClient(HttpClient httpClient, ILogger<OverpassApiClient> logger, IConfiguration configuration)
        {
            _httpClient = httpClient;
            _logger = logger;
            _overpassEndpoint = configuration["Overpass:Endpoint"] ?? DefaultOverpassEndpoint;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AccessCity-UniversityProject/1.0");
        }

        public async Task<List<OverpassElement>?> GetInfrastructureDataAsync(double minLat, double minLng, double maxLat, double maxLng)
        {
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
                _overpassEndpoint,
                new StringContent(overpassQuery));

            if (!response.IsSuccessStatusCode) return null;

            var data = await response.Content.ReadFromJsonAsync<OverpassResponse>();
            return data?.Elements;
        }

        public async Task<List<OverpassElement>?> GetHazardLikeDataAsync(double minLat, double minLng, double maxLat, double maxLng)
        {
            var bbox = $"[{minLat:F4},{minLng:F4},{maxLat:F4},{maxLng:F4}]";
            var overpassQuery = $@"
                [out:json][timeout:30];
                (
                  node[""barrier""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""highway""=""steps""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""highway""=""path""][""surface""~""unpaved|gravel|cobblestone|mud|dirt""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""highway""=""footway""][""surface""~""unpaved|gravel|cobblestone|mud|dirt""]({minLat},{minLng},{maxLat},{maxLng});
                );
                out center;
            ";

            try
            {
                var response = await _httpClient.PostAsync(
                    _overpassEndpoint,
                    new StringContent(overpassQuery));

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    _logger.LogError(
                        "Overpass API returned non-success. Endpoint: {Endpoint}, Bbox: {Bbox}, StatusCode: {StatusCode}, ResponseBody: {ResponseBody}",
                        _overpassEndpoint, bbox, (int)response.StatusCode, body.Length > 500 ? body[..500] + "..." : body);
                    throw new OverpassServiceException(
                        $"Overpass returned {(int)response.StatusCode} ({response.StatusCode}). Bbox: {bbox}.");
                }

                var data = await response.Content.ReadFromJsonAsync<OverpassResponse>().ConfigureAwait(false);
                return data?.Elements;
            }
            catch (OverpassServiceException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Overpass request failed. Endpoint: {Endpoint}, Bbox: {Bbox}, ExceptionType: {ExceptionType}, Message: {Message}",
                    _overpassEndpoint, bbox, ex.GetType().Name, ex.Message);
                throw new OverpassServiceException(
                    $"Overpass request failed: {ex.GetType().Name} - {ex.Message}. Bbox: {bbox}.", ex);
            }
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
