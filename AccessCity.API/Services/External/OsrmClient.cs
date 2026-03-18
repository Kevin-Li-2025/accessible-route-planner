using System.Net.Http.Json;
using AccessCity.API.Models.External;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services.External
{
    /// <summary>
    /// Client for the OSRM (Open Source Routing Machine) public API.
    /// Provides real road-following routes for pedestrian navigation.
    /// </summary>
    public interface IOsrmClient
    {
        /// <summary>
        /// Get a walking route between two points, optionally via waypoints.
        /// </summary>
        Task<OsrmRouteResult?> GetRouteAsync(
            Coordinate start,
            Coordinate end,
            List<Coordinate>? waypoints = null);

        /// <summary>
        /// Get up to 3 alternative routes between two points.
        /// </summary>
        Task<List<OsrmRouteResult>?> GetAlternativeRoutesAsync(
            Coordinate start,
            Coordinate end);
    }

    /// <summary>
    /// Parsed route result with NTS coordinates.
    /// </summary>
    public class OsrmRouteResult
    {
        public List<Coordinate> Coordinates { get; set; } = new();
        public double DistanceMetres { get; set; }
        public double DurationSeconds { get; set; }
        public List<OsrmStepResult> Steps { get; set; } = new();
    }

    public class OsrmStepResult
    {
        public List<Coordinate> Geometry { get; set; } = new();
        public double Distance { get; set; }
        public double Duration { get; set; }
        public string StreetName { get; set; } = string.Empty;
        public string ManeuverType { get; set; } = string.Empty;
        public string? ManeuverModifier { get; set; }
    }

    public class OsrmClient : IOsrmClient
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "http://router.project-osrm.org/route/v1/foot/";

        public OsrmClient(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AccessCity-UniversityProject/1.0");
        }

        public async Task<OsrmRouteResult?> GetRouteAsync(
            Coordinate start,
            Coordinate end,
            List<Coordinate>? waypoints = null)
        {
            var coords = new List<Coordinate> { start };
            if (waypoints != null)
                coords.AddRange(waypoints);
            coords.Add(end);

            string coordString = string.Join(";",
                coords.Select(c => $"{c.X:F6},{c.Y:F6}"));

            string url = $"{BaseUrl}{coordString}?overview=full&geometries=geojson&steps=true";

            try
            {
                var response = await _httpClient.GetFromJsonAsync<OsrmRouteResponse>(url);

                if (response == null || response.Code != "Ok" || response.Routes.Count == 0)
                    return null;

                return ParseRoute(response.Routes[0]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OSRM ERROR] {ex.Message}");
                if (ex.InnerException != null) Console.WriteLine($"[OSRM ERROR INNER] {ex.InnerException.Message}");
                return null; // Fail gracefully — caller uses fallback
            }
        }

        public async Task<List<OsrmRouteResult>?> GetAlternativeRoutesAsync(
            Coordinate start,
            Coordinate end)
        {
            string coordString = $"{start.X:F6},{start.Y:F6};{end.X:F6},{end.Y:F6}";
            string url = $"{BaseUrl}{coordString}?overview=full&geometries=geojson&steps=true&alternatives=3";

            try
            {
                var response = await _httpClient.GetFromJsonAsync<OsrmRouteResponse>(url);

                if (response == null || response.Code != "Ok" || response.Routes.Count == 0)
                    return null;

                return response.Routes.Select(ParseRoute).ToList();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static OsrmRouteResult ParseRoute(OsrmRoute route)
        {
            var result = new OsrmRouteResult
            {
                DistanceMetres = route.Distance,
                DurationSeconds = route.Duration,
                Coordinates = route.Geometry.Coordinates
                    .Select(c => new Coordinate(c[0], c[1]))
                    .ToList()
            };

            foreach (var leg in route.Legs)
            {
                foreach (var step in leg.Steps)
                {
                    result.Steps.Add(new OsrmStepResult
                    {
                        Geometry = step.Geometry.Coordinates
                            .Select(c => new Coordinate(c[0], c[1]))
                            .ToList(),
                        Distance = step.Distance,
                        Duration = step.Duration,
                        StreetName = step.Name,
                        ManeuverType = step.Maneuver.Type,
                        ManeuverModifier = step.Maneuver.Modifier
                    });
                }
            }

            return result;
        }
    }
}
