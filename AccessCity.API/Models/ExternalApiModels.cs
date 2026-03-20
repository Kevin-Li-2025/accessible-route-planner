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
        public List<string> IncludedTypes { get; set; } = new();
        public int MaxResultCount { get; set; } = 10;
        public LocationRestriction LocationRestriction { get; set; } = new();
    }

    public class LocationRestriction
    {
        public CircleRestriction Circle { get; set; } = new();
    }

    public class CircleRestriction
    {
        public LatLng Center { get; set; } = new();
        public double Radius { get; set; } = 500.0;
    }

    public class LatLng
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public class PlacesSearchResponse
    {
        public List<Place> Places { get; set; } = new();
    }

    public class Place
    {
        public string Id { get; set; } = string.Empty;
        public DisplayText DisplayName { get; set; } = new();
        public List<string> Types { get; set; } = new();
        public LatLng Location { get; set; } = new();
        public RegularHours RegularOpeningHours { get; set; } = new();
    }

    public class DisplayText { public string Text { get; set; } = string.Empty; }
    
    public class RegularHours { public bool OpenNow { get; set; } }

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
