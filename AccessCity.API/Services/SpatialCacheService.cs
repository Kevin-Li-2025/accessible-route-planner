using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Index.Quadtree;
using Microsoft.Extensions.Caching.Hybrid;
using AccessCity.API.Models;

namespace AccessCity.API.Services
{
    /// <summary>
    /// Industrial-level Spatial Caching Service.
    /// Combines an in-memory R-Tree index for O(log N) spatial queries
    /// with .NET 9 HybridCache for robust L1/L2 persistence.
    /// </summary>
    public interface ISpatialCacheService
    {
        Task<IReadOnlyList<HazardReport>> GetHazardsInBoundsAsync(Envelope bounds);
        Task UpdateHazardCacheAsync(HazardReport hazard);
        Task BulkUpdateHazardsAsync(IEnumerable<HazardReport> hazards);
    }

    public class SpatialCacheService : ISpatialCacheService
    {
        private readonly HybridCache _hybridCache;
        private readonly ILogger<SpatialCacheService> _logger;
        private Quadtree<HazardReport> _spatialIndex = new();
        private readonly ReaderWriterLockSlim _indexLock = new();
        private const string CacheKeyPrefix = "spatial:hazard:";

        public SpatialCacheService(HybridCache hybridCache, ILogger<SpatialCacheService> logger)
        {
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
                    _logger.LogWarning("Spatial query took {Elapsed}ms for bounds {Bounds}. Optimization required.", stopwatch.ElapsedMilliseconds, bounds);
                }

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying spatial index.");
                return new List<HazardReport>();
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }

        public async Task UpdateHazardCacheAsync(HazardReport hazard)
        {
            if (hazard == null) return;

            // 1. Persistent Storage (L2) - Done outside the lock to minimize contention
            string key = $"{CacheKeyPrefix}{hazard.Id}";
            await _hybridCache.SetAsync(key, hazard);

            // 2. In-Memory Index (L1) - High-concurrency write
            _indexLock.EnterWriteLock();
            try
            {
                _spatialIndex.Insert(hazard.Location.EnvelopeInternal, hazard);
                _logger.LogInformation("Successfully indexed hazard {Id} at {Location}", hazard.Id, hazard.Location);
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }

        public async Task BulkUpdateHazardsAsync(IEnumerable<HazardReport> hazards)
        {
            if (hazards == null || !hazards.Any()) return;

            var hazardList = hazards.ToList();

            // 1. Bulk Set in L2
            foreach (var hazard in hazardList)
            {
                await _hybridCache.SetAsync($"{CacheKeyPrefix}{hazard.Id}", hazard);
            }

            // 2. Optimized Index Rebuild/Update
            _indexLock.EnterWriteLock();
            try
            {
                foreach (var hazard in hazardList)
                {
                    _spatialIndex.Insert(hazard.Location.EnvelopeInternal, hazard);
                }
                _logger.LogInformation("Bulk indexed {Count} hazards.", hazardList.Count);
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }
    }
}
