using AccessCity.API.Models;
using AccessCity.API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Quadtree;

namespace AccessCity.API.Services
{
    /// <summary>
    /// Spatial Caching Service.
    /// Uses an in-memory Quadtree for O(log N) spatial queries
    /// combined with .NET 9 HybridCache for L1/L2 persistence.
    /// </summary>
    public interface ISpatialCacheService
    {
        Task<IReadOnlyList<HazardReport>> GetHazardsInBoundsAsync(Envelope bounds);
        Task UpdateHazardCacheAsync(HazardReport hazard);
        Task BulkUpdateHazardsAsync(IEnumerable<HazardReport> hazards);
    }

    public class SpatialCacheService : ISpatialCacheService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly HybridCache _hybridCache;
        private readonly ILogger<SpatialCacheService> _logger;
        private readonly Dictionary<Guid, HazardReport> _hazards = new();
        private Quadtree<HazardReport> _spatialIndex = new();
        private readonly ReaderWriterLockSlim _indexLock = new();
        private const string CacheKeyPrefix = "spatial:hazard:";

        public SpatialCacheService(
            IServiceScopeFactory scopeFactory,
            HybridCache hybridCache,
            ILogger<SpatialCacheService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _hybridCache = hybridCache ?? throw new ArgumentNullException(nameof(hybridCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyList<HazardReport>> GetHazardsInBoundsAsync(Envelope bounds)
        {
            _indexLock.EnterReadLock();
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var results = (IReadOnlyList<HazardReport>)_spatialIndex.Query(bounds);
                stopwatch.Stop();

                if (stopwatch.ElapsedMilliseconds > 10)
                {
                    _logger.LogWarning("Spatial query execution reached warning threshold: {Elapsed}ms for bounds {Bounds}.", stopwatch.ElapsedMilliseconds, bounds);
                }

                if (results.Count > 0)
                {
                    return results;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query spatial index.");
            }
            finally
            {
                _indexLock.ExitReadLock();
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var databaseResults = await dbContext.Hazards
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM hazard_report
                    WHERE ST_Intersects(
                        geom,
                        ST_MakeEnvelope({bounds.MinX}, {bounds.MinY}, {bounds.MaxX}, {bounds.MaxY}, 4326))
                    """)
                .AsNoTracking()
                .ToListAsync();

            if (databaseResults.Count > 0)
            {
                await BulkUpdateHazardsAsync(databaseResults);
            }

            return databaseResults;
        }

        public async Task UpdateHazardCacheAsync(HazardReport hazard)
        {
            if (hazard == null) return;
            string key = $"{CacheKeyPrefix}{hazard.Id}";
            await _hybridCache.SetAsync(key, hazard);
            _indexLock.EnterWriteLock();
            try
            {
                _hazards[hazard.Id] = hazard;
                RebuildIndex();
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        public async Task BulkUpdateHazardsAsync(IEnumerable<HazardReport> hazards)
        {
            if (hazards == null) return;

            var hazardList = hazards.ToList();
            if (hazardList.Count == 0) return;
            foreach (var hazard in hazardList)
            {
                await _hybridCache.SetAsync($"{CacheKeyPrefix}{hazard.Id}", hazard);
            }
            _indexLock.EnterWriteLock();
            try
            {
                foreach (var hazard in hazardList)
                {
                    _hazards[hazard.Id] = hazard;
                }
                RebuildIndex();
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        private void RebuildIndex()
        {
            _spatialIndex = new Quadtree<HazardReport>();
            foreach (var hazard in _hazards.Values.Where(h => h.Location is not null))
            {
                _spatialIndex.Insert(hazard.Location.EnvelopeInternal, hazard);
            }
        }
    }
}
