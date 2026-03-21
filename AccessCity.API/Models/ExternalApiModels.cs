using System.Text.Json.Serialization;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Models.External
{
    public class OverpassResponse
    {
        [JsonPropertyName("elements")]
        public List<OverpassElement> Elements { get; set; } = new();
    }

    public class OverpassElement
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty; // "node" or "way"
        /// <summary>Present when the Overpass query uses <c>out meta</c> — last update time of the OSM object.</summary>
        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }
        [JsonPropertyName("tags")]
        public Dictionary<string, string> Tags { get; set; } = new();
        [JsonPropertyName("lat")]
        public double Lat { get; set; }
        [JsonPropertyName("lon")]
        public double Lon { get; set; }
        /// <summary>Present for ways when using "out center".</summary>
        [JsonPropertyName("center")]
        public OverpassLatLon? Center { get; set; }
    }

    public class OverpassLatLon
    {
        [JsonPropertyName("lat")]
        public double Lat { get; set; }
        [JsonPropertyName("lon")]
        public double Lon { get; set; }
        
        [JsonPropertyName("center")]
        public OverpassCenter? Center { get; set; }
    }

    public class OverpassCenter
    {
        [JsonPropertyName("lat")]
        public double Lat { get; set; }
        [JsonPropertyName("lon")]
        public double Lon { get; set; }
    }

    public class StreetCrimeRecord
    {
        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("location_type")]
        public string LocationType { get; set; } = string.Empty;

        [JsonPropertyName("location")]
        public CrimeLocation Location { get; set; } = new();

        [JsonPropertyName("month")]
        public string Month { get; set; } = string.Empty;
    }

    public class CrimeLocation
    {
        [JsonPropertyName("latitude")]
        public string Latitude { get; set; } = string.Empty;

        [JsonPropertyName("longitude")]
        public string Longitude { get; set; } = string.Empty;
    }

    public class PlacesSearchRequest
    {
        [JsonPropertyName("includedTypes")]
        public List<string> IncludedTypes { get; set; } = new();
        [JsonPropertyName("maxResultCount")]
        public int MaxResultCount { get; set; } = 10;
        [JsonPropertyName("locationRestriction")]
        public LocationRestriction LocationRestriction { get; set; } = new();
    }

    public class LocationRestriction
    {
        [JsonPropertyName("circle")]
        public CircleRestriction Circle { get; set; } = new();
    }

    public class CircleRestriction
    {
        [JsonPropertyName("center")]
        public LatLng Center { get; set; } = new();
        [JsonPropertyName("radius")]
        public double Radius { get; set; } = 500.0;
    }

    public class LatLng
    {
        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }
        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }
    }

    public class PlacesSearchResponse
    {
        [JsonPropertyName("places")]
        public List<Place> Places { get; set; } = new();
    }

    public class Place
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        [JsonPropertyName("displayName")]
        public DisplayText DisplayName { get; set; } = new();
        [JsonPropertyName("types")]
        public List<string> Types { get; set; } = new();
        [JsonPropertyName("location")]
        public LatLng Location { get; set; } = new();
        [JsonPropertyName("regularOpeningHours")]
        public RegularHours RegularOpeningHours { get; set; } = new();
    }

    public class DisplayText
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    public class RegularHours
    {
        [JsonPropertyName("openNow")]
        public bool OpenNow { get; set; }
    }

    public class WeatherResponse
    {
        public List<WeatherCondition> Weather { get; set; } = new();
        public WeatherMain Main { get; set; } = new();
        public WeatherWind Wind { get; set; } = new();
    }

    public class WeatherCondition
    {
        public int Id { get; set; }
        public string Main { get; set; } = string.Empty; // e.g., "Rain", "Snow", "Clear"
    }

    public class WeatherMain
    {
        public double Temp { get; set; }
        public int Humidity { get; set; }
    }

    public class WeatherWind
    {
        public double Speed { get; set; }
    }
}
