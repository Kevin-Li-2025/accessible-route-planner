using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Asp.Versioning;
using AccessCity.API.Common;
using AccessCity.API.Hubs;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Services;

namespace AccessCity.API.Controllers;

/// <summary>
/// CRUD operations for hazard reports with real-time SignalR broadcasting.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public class HazardsController : ControllerBase
{
    private const long MaxHazardPhotoBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> AllowedPhotoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    private readonly IHazardReportService _hazards;
    private readonly IHubContext<HazardAlertHub> _alertHub;
    private readonly IWebHostEnvironment _environment;

    public HazardsController(
        IHazardReportService hazards,
        IHubContext<HazardAlertHub> alertHub,
        IWebHostEnvironment environment)
    {
        _hazards = hazards;
        _alertHub = alertHub;
        _environment = environment;
    }

    /// <summary>
    /// Lists hazard reports, optionally filtered by bounding box and status.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<HazardReport>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<HazardReport>>> GetHazards(
        [FromQuery] double? minLat,
        [FromQuery] double? minLng,
        [FromQuery] double? maxLat,
        [FromQuery] double? maxLng,
        [FromQuery] HazardStatus? status,
        CancellationToken cancellationToken = default)
    {
        var hazards = await _hazards.GetHazardsAsync(minLat, minLng, maxLat, maxLng, status, cancellationToken);
        return Ok(hazards);
    }

    /// <summary>
    /// Lists persisted hazard reports with bounded keyset pagination for interactive clients.
    /// </summary>
    [HttpGet("page")]
    [ProducesResponseType(typeof(HazardPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HazardPageResponse>> GetHazardsPage(
        [FromQuery] double? minLat,
        [FromQuery] double? minLng,
        [FromQuery] double? maxLat,
        [FromQuery] double? maxLng,
        [FromQuery] HazardStatus? status,
        [FromQuery] int? limit,
        [FromQuery] string? cursor,
        [FromQuery] string? query,
        CancellationToken cancellationToken = default)
    {
        var page = await _hazards.GetHazardsPageAsync(
            minLat,
            minLng,
            maxLat,
            maxLng,
            status,
            limit,
            cursor,
            query,
            cancellationToken);

        return Ok(page);
    }

    /// <summary>
    /// Creates a new hazard report and broadcasts a real-time alert via SignalR.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(HazardReport), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<HazardReport>> ReportHazard(
        [FromBody] CreateHazardRequest request,
        CancellationToken cancellationToken = default)
    {
        var report = await _hazards.CreateAsync(request, cancellationToken);

        // Broadcast real-time alert to connected clients
        await _alertHub.Clients.All.SendAsync("HazardReported", new RouteAlert(
            report.Type, report.Description,
            report.Location.Y, report.Location.X, report.ReportedAt), cancellationToken);

        return CreatedAtAction(nameof(GetHazardById), new { id = report.Id }, report);
    }

    /// <summary>
    /// Retrieves a single hazard report by ID, falling back to OSM-backed synthetic hazards.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(HazardReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HazardReport>> GetHazardById(Guid id, CancellationToken cancellationToken = default)
    {
        var hazard = await _hazards.GetByIdAsync(id, cancellationToken);
        return hazard is null ? NotFound() : Ok(hazard);
    }

    /// <summary>
    /// Updates the status of an existing hazard report.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateHazardStatus(Guid id, [FromBody] HazardStatus status, CancellationToken cancellationToken = default)
    {
        var hazard = await _hazards.UpdateStatusAsync(id, status, cancellationToken);
        if (hazard is null)
            return NotFound();

        return NoContent();
    }

    /// <summary>
    /// Attaches an uploaded image to a persisted hazard report.
    /// </summary>
    [HttpPost("{id:guid}/photo")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxHazardPhotoBytes)]
    [ProducesResponseType(typeof(HazardPhotoUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HazardPhotoUploadResponse>> UploadHazardPhoto(
        Guid id,
        [FromForm] IFormFile file,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ApiError("Photo file is required."));
        }

        if (file.Length > MaxHazardPhotoBytes)
        {
            return BadRequest(new ApiError("Photo file must be 8 MB or smaller."));
        }

        if (!AllowedPhotoContentTypes.Contains(file.ContentType))
        {
            return BadRequest(new ApiError("Photo must be JPEG, PNG, or WebP."));
        }

        var extension = file.ContentType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
            ? ".png"
            : file.ContentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase)
                ? ".webp"
                : ".jpg";

        var uploadRoot = ResolvePhotoUploadRoot();
        Directory.CreateDirectory(uploadRoot);

        var fileName = $"{id:N}-{Guid.NewGuid():N}{extension}";
        var filePath = Path.Combine(uploadRoot, fileName);
        await using (var stream = System.IO.File.Create(filePath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var photoUrl = $"/api/v1/hazards/photos/{Uri.EscapeDataString(fileName)}";
        var hazard = await _hazards.UpdatePhotoAsync(id, photoUrl, cancellationToken);
        if (hazard is null)
        {
            System.IO.File.Delete(filePath);
            return NotFound();
        }

        return Ok(new HazardPhotoUploadResponse(id, photoUrl, file.Length, file.ContentType));
    }

    /// <summary>
    /// Serves hazard images uploaded through the API.
    /// </summary>
    [HttpGet("photos/{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetHazardPhoto(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            return NotFound();
        }

        var filePath = Path.Combine(ResolvePhotoUploadRoot(), safeFileName);
        if (!System.IO.File.Exists(filePath))
        {
            return NotFound();
        }

        return PhysicalFile(filePath, ResolvePhotoContentType(filePath));
    }

    private string ResolvePhotoUploadRoot()
    {
        var root = string.IsNullOrWhiteSpace(_environment.ContentRootPath)
            ? AppContext.BaseDirectory
            : _environment.ContentRootPath;
        return Path.Combine(root, "uploads", "hazard-photos");
    }

    private static string ResolvePhotoContentType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
}
