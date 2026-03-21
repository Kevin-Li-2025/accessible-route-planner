using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccessCity.API.Controllers;

/// <summary>Reports which external integrations have credentials configured (no live probes — avoids extra outbound calls).</summary>
[AllowAnonymous]
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/integrations")]
public class IntegrationsController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public IntegrationsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet("status")]
    public ActionResult<IntegrationStatusDto> GetStatus()
    {
        var overpass = _configuration["Overpass:Endpoint"];
        return Ok(new IntegrationStatusDto
        {
            OpenWeatherApiKeyConfigured = !string.IsNullOrWhiteSpace(_configuration["OpenWeather:ApiKey"]),
            GooglePlacesApiKeyConfigured = !string.IsNullOrWhiteSpace(_configuration["GooglePlaces:ApiKey"]),
            OverpassEndpoint = string.IsNullOrWhiteSpace(overpass) ? "https://overpass-api.de/api/interpreter (default)" : overpass!,
            NominatimConfigured = true,
            OsrmUsesPublicDemo = true,
            UkPoliceDataPublicApi = true,
            Notes =
                "Weather and crime risk use conservative baselines when APIs fail or keys are missing. " +
                "OSRM routing uses the bundled public demo host unless you replace IOsrmClient with a configurable base URL. " +
                "Safe-path returns 404 when no graph/OSRM route exists."
        });
    }
}

public sealed class IntegrationStatusDto
{
    public bool OpenWeatherApiKeyConfigured { get; init; }
    public bool GooglePlacesApiKeyConfigured { get; init; }
    public string OverpassEndpoint { get; init; } = string.Empty;
    public bool NominatimConfigured { get; init; }
    public bool OsrmUsesPublicDemo { get; init; }
    public bool UkPoliceDataPublicApi { get; init; }
    public string Notes { get; init; } = string.Empty;
}
