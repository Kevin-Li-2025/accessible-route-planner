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
        private readonly Quadtree<HazardReport> _spatialIndex = new();
        private readonly SemaphoreSlim _lock = new(1, 1);
        private const string CacheKeyPrefix = "spatial:hazard:";

        public SpatialCacheService(HybridCache hybridCache)
        {
            _hybridCache = hybridCache;
        }

        public async Task<IReadOnlyList<HazardReport>> GetHazardsInBoundsAsync(Envelope bounds)
        {
            await _lock.WaitAsync();
            try
            {
                // O(log N) spatial query on the dynamic Quadtree
                return (IReadOnlyList<HazardReport>)_spatialIndex.Query(bounds);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task UpdateHazardCacheAsync(HazardReport hazard)
        {
            await _lock.WaitAsync();
            try
            {
                // 1. Update L1/L2 persistent cache
                string key = $"{CacheKeyPrefix}{hazard.Id}";
                await _hybridCache.SetAsync(key, hazard);

                // 2. Update Dynamic Spatial Index
                _spatialIndex.Insert(hazard.Location.EnvelopeInternal, hazard);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task BulkUpdateHazardsAsync(IEnumerable<HazardReport> hazards)
        {
            await _lock.WaitAsync();
            try
            {
                foreach (var hazard in hazards)
                {
                    string key = $"{CacheKeyPrefix}{hazard.Id}";
                    await _hybridCache.SetAsync(key, hazard);
                    _spatialIndex.Insert(hazard.Location.EnvelopeInternal, hazard);
                }
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
