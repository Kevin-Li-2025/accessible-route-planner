namespace AccessCity.API.Models.DTOs;

/// <summary>Nearby safe-haven POI for clients (Google Places–backed when configured).</summary>
public sealed class SafeHavenPlaceDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Types { get; init; } = Array.Empty<string>();
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public bool? OpenNow { get; init; }
}
