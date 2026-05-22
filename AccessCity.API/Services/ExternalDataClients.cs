using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AccessCity.API.Exceptions;
using AccessCity.API.Models.External;
using AccessCity.API.Services;
using Microsoft.Extensions.Configuration;
using System.Threading;

namespace AccessCity.API.Services.External
{
    /// <summary>
    /// Service for fetching precise street accessibility and physical hazard data
    /// (e.g. stairs, tactile paving, cobblestone paths, lighting).
    /// </summary>
    public interface IOpenStreetMapClient
    {
        /// <summary>Fetches real OSM features that represent hazards/barriers: barriers, steps, poor surface.</summary>
        Task<List<OverpassElement>?> GetHazardLikeDataAsync(
            double minLat,
            double minLng,
            double maxLat,
            double maxLng,
            CancellationToken cancellationToken = default);
    }

    public class OverpassApiClient : IOpenStreetMapClient
    {
        private const string DefaultOverpassEndpoint = "https://overpass-api.de/api/interpreter";

        /// <summary>
        /// Huge Overpass JSON can take tens of seconds to download/parse and spikes /hazards tail latency.
        /// Fail fast so the hazard merge path can degrade to DB-only.
        /// </summary>
        private const int MaxHazardJsonBytes = 8 * 1024 * 1024;

        private readonly HttpClient _httpClient;
        private readonly IExternalDependencyGuard _guard;
        private readonly ILogger<OverpassApiClient> _logger;
        private readonly string _overpassEndpoint;

        public OverpassApiClient(
            HttpClient httpClient,
            IExternalDependencyGuard guard,
            ILogger<OverpassApiClient> logger,
            IConfiguration configuration)
        {
            _httpClient = httpClient;
            _guard = guard;
            _logger = logger;
            _overpassEndpoint = configuration["Overpass:Endpoint"] ?? DefaultOverpassEndpoint;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AccessCity-UniversityProject/1.0");
        }

        public async Task<List<OverpassElement>?> GetInfrastructureDataAsync(double minLat, double minLng, double maxLat, double maxLng)
        {
            var overpassQuery = $@"
                [out:json][timeout:4];
                (
                  way[""highway""=""footway""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""highway""=""steps""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""sidewalk""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""lit""=""yes""]({minLat},{minLng},{maxLat},{maxLng});
                );
                out center;
            ";

            return await _guard.ExecuteAsync<List<OverpassElement>?>(
                "Overpass",
                async guardedToken =>
                {
                    var response = await _httpClient.PostAsync(
                        _overpassEndpoint,
                        new StringContent(overpassQuery),
                        guardedToken);

                    if (!response.IsSuccessStatusCode) return null;

                    var data = await response.Content.ReadFromJsonAsync<OverpassResponse>(guardedToken);
                    return data?.Elements;
                },
                () => new List<OverpassElement>());
        }

        public async Task<List<OverpassElement>?> GetHazardLikeDataAsync(
            double minLat,
            double minLng,
            double maxLat,
            double maxLng,
            CancellationToken cancellationToken = default)
        {
            var bbox = $"[{minLat:F4},{minLng:F4},{maxLat:F4},{maxLng:F4}]";
            // Server-side cap must stay below HttpClient.Timeout so we fail fast instead of tail latency in minutes.
            var overpassQuery = $@"
                [out:json][timeout:4];
                (
                  node[""barrier""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""highway""=""steps""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""highway""=""path""][""surface""~""unpaved|gravel|cobblestone|mud|dirt""]({minLat},{minLng},{maxLat},{maxLng});
                  way[""highway""=""footway""][""surface""~""unpaved|gravel|cobblestone|mud|dirt""]({minLat},{minLng},{maxLat},{maxLng});
                );
                out center meta;
            ";

            return await _guard.ExecuteAsync<List<OverpassElement>?>(
                "Overpass",
                guardedToken => GetHazardLikeDataCoreAsync(overpassQuery, bbox, guardedToken),
                () => new List<OverpassElement>(),
                cancellationToken);
        }

        private async Task<List<OverpassElement>?> GetHazardLikeDataCoreAsync(
            string overpassQuery,
            string bbox,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _httpClient.PostAsync(
                    _overpassEndpoint,
                    new StringContent(overpassQuery),
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogError(
                        "Overpass API returned non-success. Endpoint: {Endpoint}, Bbox: {Bbox}, StatusCode: {StatusCode}, ResponseBody: {ResponseBody}",
                        _overpassEndpoint, bbox, (int)response.StatusCode, errBody.Length > 500 ? errBody[..500] + "..." : errBody);
                    throw new OverpassServiceException(
                        $"Overpass returned {(int)response.StatusCode} ({response.StatusCode}). Bbox: {bbox}.");
                }

                var body = await ReadResponseBodyWithByteCapAsync(
                        response.Content,
                        MaxHazardJsonBytes,
                        cancellationToken)
                    .ConfigureAwait(false);

                OverpassResponse? data;
                try
                {
                    data = JsonSerializer.Deserialize<OverpassResponse>(body);
                }
                catch (JsonException jx)
                {
                    throw new OverpassServiceException(
                        $"Overpass returned unparsable JSON (length {body.Length}). Bbox: {bbox}.", jx);
                }

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

        private static async Task<string> ReadResponseBodyWithByteCapAsync(
            HttpContent content,
            int maxBytes,
            CancellationToken cancellationToken)
        {
            var len = content.Headers.ContentLength;
            if (len.HasValue && len.Value > maxBytes)
            {
                throw new OverpassServiceException(
                    $"Overpass response too large (Content-Length {len.Value} bytes, cap {maxBytes}).");
            }

            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var initialCap = len is long l && l > 0 && l <= maxBytes ? (int)l : 65536;
            using var ms = new MemoryStream(initialCap);
            var buffer = new byte[8192];
            var total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
                if (total > maxBytes)
                {
                    throw new OverpassServiceException(
                        $"Overpass response exceeded {maxBytes} bytes while reading (truncation guard).");
                }

                ms.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(ms.ToArray());
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
        private readonly IExternalDependencyGuard _guard;

        public UkPoliceDataClient(HttpClient httpClient, IExternalDependencyGuard guard)
        {
            _httpClient = httpClient;
            _guard = guard;
            _httpClient.BaseAddress = new Uri("https://data.police.uk/api/");
        }

        public async Task<List<StreetCrimeRecord>?> GetRecentStreetCrimesAsync(double latitude, double longitude)
        {
            return await _guard.ExecuteAsync<List<StreetCrimeRecord>?>(
                "UkPolice",
                guardedToken => _httpClient.GetFromJsonAsync<List<StreetCrimeRecord>>(
                    $"crimes-street/all-crime?lat={latitude}&lng={longitude}",
                    guardedToken),
                () => new List<StreetCrimeRecord>());
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

    /// <summary>
    /// Queries OSM Overpass API for street lamps and CCTV cameras near a point.
    /// Used by the risk scoring engine to quantify lighting and surveillance coverage.
    /// </summary>
    public interface IEnvironmentalDataClient
    {
        Task<EnvironmentalSummary> GetNearbyInfrastructureAsync(double lat, double lng, double radiusMetres);
    }

    public class EnvironmentalSummary
    {
        public int StreetLampCount { get; set; }
        public int SurveillanceCameraCount { get; set; }
    }

    public class EnvironmentalDataClient : IEnvironmentalDataClient
    {
        private readonly HttpClient _httpClient;
        private readonly IExternalDependencyGuard _guard;
        private readonly ILogger<EnvironmentalDataClient> _logger;
        private readonly string _endpoint;

        public EnvironmentalDataClient(
            HttpClient httpClient,
            IExternalDependencyGuard guard,
            ILogger<EnvironmentalDataClient> logger,
            IConfiguration config)
        {
            _httpClient = httpClient;
            _guard = guard;
            _logger = logger;
            _endpoint = config["Overpass:Endpoint"] ?? "https://overpass-api.de/api/interpreter";
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AccessCity-UniversityProject/1.0");
        }

        public async Task<EnvironmentalSummary> GetNearbyInfrastructureAsync(double lat, double lng, double radiusMetres)
        {
            return await _guard.ExecuteAsync(
                "Environmental",
                guardedToken => QueryNearbyInfrastructureAsync(lat, lng, radiusMetres, guardedToken),
                () => new EnvironmentalSummary());
        }

        private async Task<EnvironmentalSummary> QueryNearbyInfrastructureAsync(
            double lat,
            double lng,
            double radiusMetres,
            CancellationToken cancellationToken)
        {
            var query = $@"
                [out:json][timeout:3];
                node[""highway""=""street_lamp""](around:{radiusMetres},{lat},{lng});
                out count;
                node[""man_made""=""surveillance""](around:{radiusMetres},{lat},{lng});
                out count;
            ";

            try
            {
                var response = await _httpClient.PostAsync(_endpoint, new StringContent(query), cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return new EnvironmentalSummary();

                var data = await response.Content.ReadFromJsonAsync<OverpassCountResponse>(cancellationToken);
                var lamps = data?.Elements?.ElementAtOrDefault(0)?.Tags?.Total ?? 0;
                var cameras = data?.Elements?.ElementAtOrDefault(1)?.Tags?.Total ?? 0;

                return new EnvironmentalSummary
                {
                    StreetLampCount = lamps,
                    SurveillanceCameraCount = cameras
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Overpass environmental query failed for ({Lat},{Lng})", lat, lng);
                return new EnvironmentalSummary();
            }
        }
    }

    // Minimal model for Overpass "out count" responses.
    internal class OverpassCountResponse
    {
        [JsonPropertyName("elements")]
        public List<OverpassCountElement>? Elements { get; set; }
    }

    internal class OverpassCountElement
    {
        [JsonPropertyName("tags")]
        public OverpassCountTags? Tags { get; set; }
    }

    internal class OverpassCountTags
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("nodes")]
        public int Nodes { get; set; }
    }
}
