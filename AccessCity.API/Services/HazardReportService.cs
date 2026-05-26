using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Models.DTOs;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using System.Globalization;
using System.Text;

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

    Task<List<HazardReport>> GetPersistedHazardsAsync(
        double? minLat,
        double? minLng,
        double? maxLat,
        double? maxLng,
        HazardStatus? status,
        int limit,
        CancellationToken cancellationToken);

    Task<HazardPageResponse> GetHazardsPageAsync(
        double? minLat,
        double? minLng,
        double? maxLat,
        double? maxLng,
        HazardStatus? status,
        int? limit,
        string? cursor,
        string? query,
        CancellationToken cancellationToken);

    Task<HazardReport> CreateAsync(CreateHazardRequest request, CancellationToken cancellationToken);

    Task<HazardReport?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<HazardReport?> UpdateStatusAsync(Guid id, HazardStatus status, CancellationToken cancellationToken);

    Task<HazardReport?> UpdatePhotoAsync(Guid id, string photoUrl, CancellationToken cancellationToken);
}

public sealed class HazardReportService : IHazardReportService
{
    private const int DefaultPageLimit = 25;
    private const int MaxPageLimit = 100;

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
        return await _realHazardData.GetActiveHazardsAsync(minLat, minLng, maxLat, maxLng, status, cancellationToken);
    }

    public async Task<List<HazardReport>> GetPersistedHazardsAsync(
        double? minLat,
        double? minLng,
        double? maxLat,
        double? maxLng,
        HazardStatus? status,
        int limit,
        CancellationToken cancellationToken)
    {
        var cappedLimit = Math.Clamp(limit, 1, MaxPageLimit);
        return await QueryPersistedHazardsPageAsync(
                minLat,
                minLng,
                maxLat,
                maxLng,
                status,
                cursorReportedBefore: null,
                normalizedSearchQuery: null,
                cappedLimit,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<HazardPageResponse> GetHazardsPageAsync(
        double? minLat,
        double? minLng,
        double? maxLat,
        double? maxLng,
        HazardStatus? status,
        int? limit,
        string? cursor,
        string? query,
        CancellationToken cancellationToken)
    {
        var cappedLimit = Math.Clamp(limit ?? DefaultPageLimit, 1, MaxPageLimit);
        var cursorReportedBefore = DecodeCursor(cursor);
        var normalizedSearchQuery = NormalizeSearchQuery(query);

        var dbRows = await QueryPersistedHazardsPageAsync(
                minLat,
                minLng,
                maxLat,
                maxLng,
                status,
                cursorReportedBefore,
                normalizedSearchQuery,
                cappedLimit,
                cancellationToken)
            .ConfigureAwait(false);

        if (dbRows.Count > cappedLimit)
        {
            return ToPageResponse(dbRows, cappedLimit);
        }

        var mergedRows = await _realHazardData
            .GetActiveHazardsAsync(minLat, minLng, maxLat, maxLng, status, cancellationToken)
            .ConfigureAwait(false);

        var rows = mergedRows
            .Where(h => h.Location is not null)
            .Where(h => !cursorReportedBefore.HasValue || h.ReportedAt < cursorReportedBefore.Value)
            .Where(h => MatchesSearch(h, normalizedSearchQuery))
            .OrderByDescending(h => h.ReportedAt)
            .DistinctBy(h => h.Id)
            .Take(cappedLimit + 1)
            .ToList();

        return ToPageResponse(rows, cappedLimit);
    }

    private async Task<List<HazardReport>> QueryPersistedHazardsPageAsync(
        double? minLat,
        double? minLng,
        double? maxLat,
        double? maxLng,
        HazardStatus? status,
        DateTime? cursorReportedBefore,
        string? normalizedSearchQuery,
        int cappedLimit,
        CancellationToken cancellationToken)
    {
        IQueryable<HazardReport> query = _dbContext.Hazards.AsNoTracking();

        if (minLat.HasValue || minLng.HasValue || maxLat.HasValue || maxLng.HasValue)
        {
            var envelope = CreateValidatedEnvelope(minLat, minLng, maxLat, maxLng);
            query = query.Where(h => h.Location.Intersects(envelope));
        }

        if (status.HasValue)
        {
            query = query.Where(h => h.Status == status.Value);
        }

        if (cursorReportedBefore.HasValue)
        {
            query = query.Where(h => h.ReportedAt < cursorReportedBefore.Value);
        }

        if (!string.IsNullOrWhiteSpace(normalizedSearchQuery))
        {
            query = query.Where(h =>
                h.Type.ToLower().Contains(normalizedSearchQuery)
                || h.Description.ToLower().Contains(normalizedSearchQuery)
                || (!string.IsNullOrEmpty(h.Source) && h.Source.ToLower().Contains(normalizedSearchQuery)));
        }

        return await query
            .OrderByDescending(h => h.ReportedAt)
            .Take(cappedLimit + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static HazardPageResponse ToPageResponse(List<HazardReport> rows, int cappedLimit)
    {
        var hasMore = rows.Count > cappedLimit;
        var items = hasMore
            ? rows.Take(cappedLimit).ToList()
            : rows;

        var nextCursor = hasMore && items.Count > 0
            ? EncodeCursor(items[^1].ReportedAt)
            : null;

        return new HazardPageResponse(items, nextCursor, cappedLimit, hasMore);
    }

    private static string? NormalizeSearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var normalized = query.Trim();
        if (normalized.Length > 80)
        {
            normalized = normalized[..80];
        }

        return normalized.ToLowerInvariant();
    }

    private static bool MatchesSearch(HazardReport hazard, string? normalizedSearchQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedSearchQuery))
        {
            return true;
        }

        return ContainsSearchTerm(hazard.Type, normalizedSearchQuery)
               || ContainsSearchTerm(hazard.Description, normalizedSearchQuery)
               || ContainsSearchTerm(hazard.Source, normalizedSearchQuery);
    }

    private static bool ContainsSearchTerm(string? value, string normalizedSearchQuery) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(normalizedSearchQuery, StringComparison.OrdinalIgnoreCase);

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

        var reportedHazards = await _realHazardData
            .GetActiveHazardsAsync(null, null, null, null, HazardStatus.Reported, cancellationToken)
            .ConfigureAwait(false);
        hazard = reportedHazards.FirstOrDefault(h => h.Id == id);
        if (hazard is not null)
        {
            return hazard;
        }

        var allHazards = await _realHazardData
            .GetActiveHazardsAsync(null, null, null, null, null, cancellationToken)
            .ConfigureAwait(false);
        return allHazards.FirstOrDefault(h => h.Id == id);
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

    public async Task<HazardReport?> UpdatePhotoAsync(
        Guid id,
        string photoUrl,
        CancellationToken cancellationToken)
    {
        var hazard = await _dbContext.Hazards.SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (hazard is null)
        {
            return null;
        }

        hazard.PhotoUrl = photoUrl.Length > 2048 ? photoUrl[..2048] : photoUrl;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _spatialCache.UpdateHazardCacheAsync(hazard);

        return hazard;
    }

    private static Polygon CreateValidatedEnvelope(
        double? minLat,
        double? minLng,
        double? maxLat,
        double? maxLng)
    {
        if (!minLat.HasValue || !minLng.HasValue || !maxLat.HasValue || !maxLng.HasValue)
        {
            throw new ArgumentException("minLat, minLng, maxLat and maxLng must be provided together.");
        }

        if (minLat.Value < -90 || minLat.Value > 90 || maxLat.Value < -90 || maxLat.Value > 90)
            throw new ArgumentException("Latitude values must be between -90 and 90.");
        if (minLng.Value < -180 || minLng.Value > 180 || maxLng.Value < -180 || maxLng.Value > 180)
            throw new ArgumentException("Longitude values must be between -180 and 180.");
        if (minLat.Value > maxLat.Value)
            throw new ArgumentException("minLat must be less than or equal to maxLat.");
        if (minLng.Value > maxLng.Value)
            throw new ArgumentException("minLng must be less than or equal to maxLng.");

        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        return factory.CreatePolygon(new[]
        {
            new Coordinate(minLng.Value, minLat.Value),
            new Coordinate(maxLng.Value, minLat.Value),
            new Coordinate(maxLng.Value, maxLat.Value),
            new Coordinate(minLng.Value, maxLat.Value),
            new Coordinate(minLng.Value, minLat.Value)
        });
    }

    private static string EncodeCursor(DateTime reportedAt)
    {
        var value = reportedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static DateTime? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(padded));

            if (DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }
        catch (FormatException)
        {
        }

        throw new ArgumentException("Invalid hazard page cursor.");
    }
}
