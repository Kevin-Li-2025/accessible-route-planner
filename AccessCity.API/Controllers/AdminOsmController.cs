using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Messaging;
using AccessCity.API.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AccessCity.API.Controllers;

[Authorize]
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin/osm")]
public class AdminOsmController : ControllerBase
{
    private readonly IOsmImportService _osmImportService;
    private readonly IMessageBus _messageBus;
    private readonly IOptions<OsmImportOptions> _osmOptions;
    private readonly AppDbContext _dbContext;

    public AdminOsmController(
        IOsmImportService osmImportService,
        IMessageBus messageBus,
        IOptions<OsmImportOptions> osmOptions,
        AppDbContext dbContext)
    {
        _osmImportService = osmImportService;
        _messageBus = messageBus;
        _osmOptions = osmOptions;
        _dbContext = dbContext;
    }

    /// <summary>
    /// Runs an OSM import synchronously. Kept for local maintenance and integration tests.
    /// Use POST /import-jobs for production-scale imports.
    /// </summary>
    [HttpPost("import")]
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
    [ProducesResponseType(typeof(OsmImportJobResponse), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<OsmImportJobResponse>> QueueImport(CancellationToken cancellationToken)
    {
        var filePath = _osmOptions.Value.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return BadRequest("OsmImport:FilePath is not configured.");
        }

        var jobId = Guid.NewGuid();
        var queuedAt = DateTime.UtcNow;
        _dbContext.OsmImportJobs.Add(new OsmImportJob
        {
            Id = jobId,
            Status = "queued",
            FilePath = filePath,
            CityName = "configured",
            QueuedAtUtc = queuedAt
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _messageBus.PublishAsync(
            new OsmImportStartedEvent(jobId, filePath, "configured", queuedAt),
            cancellationToken);

        return Accepted(new OsmImportJobResponse(jobId, "queued", filePath, queuedAt));
    }

    [HttpGet("import-jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(OsmImportJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OsmImportJobResponse>> GetImportJob(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _dbContext.OsmImportJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            return NotFound();
        }

        return Ok(new OsmImportJobResponse(
            job.Id,
            job.Status,
            job.FilePath,
            job.QueuedAtUtc,
            job.StartedAtUtc,
            job.FinishedAtUtc,
            job.Attempts,
            job.FeedIngestionRunId,
            job.ErrorSummary));
    }
}
