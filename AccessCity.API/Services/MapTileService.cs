using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.VectorTiles;
using NetTopologySuite.IO.VectorTiles.Mapbox;
using AccessCity.API.Models;

namespace AccessCity.API.Services
{
    public interface IMapTileService
    {
        Task<byte[]> GetVectorTileAsync(int z, int x, int y);
    }

    public class MapTileService : IMapTileService
    {
        private readonly ISpatialCacheService _spatialCache;
        private readonly ILogger<MapTileService> _logger;

        public MapTileService(ISpatialCacheService spatialCache, ILogger<MapTileService> logger)
        {
            _spatialCache = spatialCache;
            _logger = logger;
        }

        public async Task<byte[]> GetVectorTileAsync(int z, int x, int y)
        {
            // 1. Calculate tile boundary (Web Mercator)
            var envelope = TileToEnvelope(z, x, y);

            // 2. Query spatial index for features in this tile
            var hazards = await _spatialCache.GetHazardsInBoundsAsync(envelope);

            if (hazards.Count == 0) return Array.Empty<byte>();

            // 3. Convert to NTS Features
            var features = new List<Feature>();
            foreach (var h in hazards)
            {
                var attributes = new AttributesTable
                {
                    { "id", h.Id.ToString() },
                    { "type", h.Type },
                    { "status", h.Status.ToString() }
                };
                features.Add(new Feature(h.Location, attributes));
            }

            // 4. Generate MVT
            try
            {
                var vectorTile = new VectorTile();
                var layer = new Layer { Name = "hazards" };
                
                foreach (var f in features)
                {
                    layer.Features.Add(f);
                }
                
                vectorTile.Layers.Add(layer);

                // Serialize using Mapbox Protocol (Static Writer)
                using var ms = new MemoryStream();
                // MapboxTileWriter.Write is static. Usage: Write(vectorTile, stream, zoom)
                // Using the specific zoom for clipping/simplification logic
                MapboxTileWriter.Write(vectorTile, ms, (uint)z);
                
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate MVT for {Z}/{X}/{Y}", z, x, y);
                return Array.Empty<byte>();
            }
        }

        private Envelope TileToEnvelope(int z, int x, int y)
        {
            double n = Math.Pow(2.0, z);
            double lonMin = x / n * 360.0 - 180.0;
            double lonMax = (x + 1) / n * 360.0 - 180.0;
            
            double latMinRad = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (y + 1) / n)));
            double latMaxRad = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n)));
            
            double latMin = latMinRad * 180.0 / Math.PI;
            double latMax = latMaxRad * 180.0 / Math.PI;

            return new Envelope(lonMin, lonMax, latMin, latMax);
        }
    }
}
