using AccessCity.API.Models.DTOs;
using AccessCity.API.Models.External;
using AccessCity.API.Services.External;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccessCity.API.Controllers;

/// <summary>Feature F-3: nearby safe havens (police, hospital, convenience, fuel) via Google Places when <c>GooglePlaces:ApiKey</c> is set.</summary>
[AllowAnonymous]
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/safe-haven")]
public class SafeHavenController : ControllerBase
{
    private readonly ISafeHavenPlacesClient _places;
    private readonly IConfiguration _configuration;

    public SafeHavenController(ISafeHavenPlacesClient places, IConfiguration configuration)
    {
        _places = places;
        _configuration = configuration;
    }

    /// <summary>Search for nearby safe-haven places around a coordinate.</summary>
    [HttpGet("nearby")]
    [ProducesResponseType(typeof(SafeHavenNearbyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SafeHavenNearbyResponse>> GetNearby(
        [FromQuery] double lat,
        [FromQuery] double lng,
        [FromQuery] double radius = 500,
        CancellationToken cancellationToken = default)
    {
        if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
            return BadRequest(new { error = "Invalid coordinates." });

        radius = Math.Clamp(radius, 50, 50_000);

        var configured = !string.IsNullOrWhiteSpace(_configuration["GooglePlaces:ApiKey"]);
        var raw = await _places.GetNearbySafeHavensAsync(lat, lng, radius);
        cancellationToken.ThrowIfCancellationRequested();

        var places = (raw ?? new List<Place>())
            .Select(p => new SafeHavenPlaceDto
            {
                Id = p.Id ?? string.Empty,
                Name = p.DisplayName?.Text ?? string.Empty,
                Types = p.Types ?? new List<string>(),
                Latitude = p.Location?.Latitude ?? 0,
                Longitude = p.Location?.Longitude ?? 0,
                OpenNow = p.RegularOpeningHours?.OpenNow
            })
            .Where(p => !string.IsNullOrEmpty(p.Id))
            .ToList();

        return Ok(new SafeHavenNearbyResponse
        {
            Places = places,
            GooglePlacesConfigured = configured,
            RadiusMetres = radius
        });
    }
}

public sealed class SafeHavenNearbyResponse
{
    public IReadOnlyList<SafeHavenPlaceDto> Places { get; init; } = Array.Empty<SafeHavenPlaceDto>();
    public bool GooglePlacesConfigured { get; init; }
    public double RadiusMetres { get; init; }
}
