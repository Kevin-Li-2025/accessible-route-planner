using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Security;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AccessCity.API.Controllers;

[Authorize]
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin/osm")]
[EnableRateLimiting(AccessCityRateLimitPolicies.Write)]
public class AdminOsmController : ControllerBase
{
    private readonly IOsmImportService _osmImportService;
    private readonly IOsmImportJobService _osmImportJobs;
    private readonly IRouteGraphProfileService _routeGraphProfiler;

    public AdminOsmController(
        IOsmImportService osmImportService,
        IOsmImportJobService osmImportJobs,
        IRouteGraphProfileService routeGraphProfiler)
    {
        _osmImportService = osmImportService;
        _osmImportJobs = osmImportJobs;
        _routeGraphProfiler = routeGraphProfiler;
    }

    /// <summary>
    /// Runs an OSM import synchronously. Kept for local maintenance and integration tests.
    /// Use POST /import-jobs for production-scale imports.
    /// </summary>
    [HttpPost("import")]
    [DisableRequestTimeout]
    [ProducesResponseType(typeof(OsmImportResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<OsmImportResult>> Import(CancellationToken cancellationToken)
    {
        var result = await _osmImportService.ImportConfiguredAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Queues an OSM import job for a background worker. With Kafka enabled, one worker in the
    /// configured consumer group handles the job across all API replicas.
    /// </summary>
    [HttpPost("import-jobs")]
    [DisableRequestTimeout]
    [ProducesResponseType(typeof(OsmImportJobResponse), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<OsmImportJobResponse>> QueueImport(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _osmImportJobs.QueueConfiguredImportAsync(cancellationToken);
            return Accepted(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

    }

    [HttpGet("import-jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(OsmImportJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OsmImportJobResponse>> GetImportJob(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _osmImportJobs.GetImportJobAsync(jobId, cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>
    /// Profiles packed route graph artifacts against configured or supplied routes.
    /// Use this after a real city OSM import to measure shard reuse, Redis payload size, and hot-load time.
    /// </summary>
    [HttpPost("route-graph/profile")]
    [DisableRequestTimeout]
    [ProducesResponseType(typeof(RouteGraphProfileResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<RouteGraphProfileResponse>> ProfileRouteGraph(
        [FromBody] RouteGraphProfileRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _routeGraphProfiler.ProfileAsync(request, cancellationToken);
        return Ok(result);
    }
}
