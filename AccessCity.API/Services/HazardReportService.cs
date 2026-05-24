using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services;

public interface IHazardReportService
{
    Task<List<HazardReport>> GetHazardsAsync(
        double? minLat,
        double? minLng,
        double? maxLat,
        double? maxLng,
        HazardStatus? status,
        CancellationToken cancellationToken);

    Task<HazardReport> CreateAsync(CreateHazardRequest request, CancellationToken cancellationToken);

    Task<HazardReport?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<HazardReport?> UpdateStatusAsync(Guid id, HazardStatus status, CancellationToken cancellationToken);
}

public sealed class HazardReportService : IHazardReportService
{
    private readonly HazardDbContext _dbContext;
    private readonly ISpatialCacheService _spatialCache;
    private readonly IRealHazardDataService _realHazardData;
    private readonly IHazardSpatialIndex _hazardSpatialIndex;

    public HazardReportService(
        HazardDbContext dbContext,
        ISpatialCacheService spatialCache,
        IRealHazardDataService realHazardData,
        IHazardSpatialIndex hazardSpatialIndex)
    {
        _dbContext = dbContext;
        _spatialCache = spatialCache;
        _realHazardData = realHazardData;
        _hazardSpatialIndex = hazardSpatialIndex;
    }

    public async Task<List<HazardReport>> GetHazardsAsync(
        double? minLat,
        double? minLng,
        double? maxLat,
        double? maxLng,
        HazardStatus? status,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return await _realHazardData.GetActiveHazardsAsync(minLat, minLng, maxLat, maxLng, status);
    }

    public async Task<HazardReport> CreateAsync(CreateHazardRequest request, CancellationToken cancellationToken)
    {
        var report = new HazardReport
        {
            Id = Guid.NewGuid(),
            Location = new Point(request.Location) { SRID = 4326 },
            Type = request.Type.Trim(),
            Description = request.Description.Trim(),
            PhotoUrl = request.PhotoUrl ?? string.Empty,
            ReportedAt = DateTime.UtcNow,
            Status = HazardStatus.Reported,
            Source = string.IsNullOrWhiteSpace(request.Source) ? "user" : request.Source
        };

        if (report.Type.Length > 50) report.Type = report.Type[..50];
        if (report.Description.Length > 500) report.Description = report.Description[..500];
        report.Location.SRID = 4326;

        _dbContext.Hazards.Add(report);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _hazardSpatialIndex.MarkStale();
        await _spatialCache.UpdateHazardCacheAsync(report);

        return report;
    }

    public async Task<HazardReport?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var hazard = await _dbContext.Hazards.AsNoTracking().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (hazard is not null)
        {
            return hazard;
        }

        var merged = await _realHazardData.GetActiveHazardsAsync(null, null, null, null, null);
        return merged.FirstOrDefault(h => h.Id == id);
    }

    public async Task<HazardReport?> UpdateStatusAsync(
        Guid id,
        HazardStatus status,
        CancellationToken cancellationToken)
    {
        var hazard = await _dbContext.Hazards.SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (hazard is null)
        {
            return null;
        }

        hazard.Status = status;
        await _dbContext.SaveChangesAsync(cancellationToken);
        _hazardSpatialIndex.MarkStale();
        await _spatialCache.UpdateHazardCacheAsync(hazard);

        return hazard;
    }
}
