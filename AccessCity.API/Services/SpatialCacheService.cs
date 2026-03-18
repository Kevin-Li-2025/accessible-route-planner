using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Index.Quadtree;
using Microsoft.Extensions.Caching.Hybrid;
using AccessCity.API.Models;

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
        private readonly HybridCache _hybridCache;
        private readonly ILogger<SpatialCacheService> _logger;
        private readonly Quadtree<HazardReport> _spatialIndex = new();
        private readonly ReaderWriterLockSlim _indexLock = new();
        private const string CacheKeyPrefix = "spatial:hazard:";

        public SpatialCacheService(HybridCache hybridCache, ILogger<SpatialCacheService> logger)
        {
            _hybridCache = hybridCache ?? throw new ArgumentNullException(nameof(hybridCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task<IReadOnlyList<HazardReport>> GetHazardsInBoundsAsync(Envelope bounds)
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

                return Task.FromResult(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query spatial index.");
                return Task.FromResult<IReadOnlyList<HazardReport>>(new List<HazardReport>());
            }
            finally
            {
                _indexLock.ExitReadLock();
            }
        }

        public async Task UpdateHazardCacheAsync(HazardReport hazard)
        {
            if (hazard == null) return;
            string key = $"{CacheKeyPrefix}{hazard.Id}";
            await _hybridCache.SetAsync(key, hazard);
            _indexLock.EnterWriteLock();
            try
            {
                _spatialIndex.Insert(hazard.Location.EnvelopeInternal, hazard);
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
                    _spatialIndex.Insert(hazard.Location.EnvelopeInternal, hazard);
                }
            }
            finally
            {
                _indexLock.ExitWriteLock();
            }
        }
    }
}
