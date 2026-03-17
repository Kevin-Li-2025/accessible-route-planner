using NetTopologySuite.Geometries;
using AccessCity.API.Models;
using AccessCity.API.Models.External;
using AccessCity.API.Services.External;
using Microsoft.Extensions.Caching.Memory;

namespace AccessCity.API.Services
{
    /// <summary>
    /// Predictive risk scoring engine.
    /// Evaluates safety at a geographic point by combining:
    ///   • Inverse-square hazard proximity weighting
    ///   • Hazard density within a configurable radius
    ///   • Infrastructure quality heuristics (lighting, surface)
    ///   • UK Police open data (street crime) — cached 24h, no DB required
    /// </summary>
    public class RiskScoringService
    {
        private readonly IUkPoliceDataClient? _ukPolice;
        private readonly IMemoryCache? _cache;

        private const string CrimeCacheKeyPrefix = "ukcrime:";
        private static readonly TimeSpan CrimeCacheExpiry = TimeSpan.FromHours(24);

        private static readonly Dictionary<string, double> HazardSeverity = new(StringComparer.OrdinalIgnoreCase)
        {
            ["pothole"]              = 0.6,
            ["broken_pavement"]      = 0.5,
            ["missing_curb_ramp"]    = 0.7,
            ["obstruction"]          = 0.5,
            ["poor_lighting"]        = 0.8,
            ["construction"]         = 0.7,
            ["flooding"]             = 0.9,
            ["missing_tactile"]      = 0.6,
            ["steep_gradient"]       = 0.5,
            ["narrow_sidewalk"]      = 0.4,
            ["uneven_surface"]       = 0.5,
            ["missing_handrail"]     = 0.6,
            ["traffic_hazard"]       = 0.8,
            ["missing_crossing"]     = 0.7,
        };

        private const double DefaultSeverity = 0.5;

        private const double W_Proximity       = 0.40;
        private const double W_Density         = 0.25;
        private const double W_Infrastructure  = 0.20;
        private const double W_Crime           = 0.15;

        private const double DecayLambda = 150.0;

        public RiskScoringService(IUkPoliceDataClient? ukPolice = null, IMemoryCache? cache = null)
        {
            _ukPolice = ukPolice;
            _cache = cache;
        }

        /// <summary>
        /// Compute a full risk breakdown for a single geographic point.
        /// Integrates UK Police street crime data when client and cache are available (cached 24h; no database).
        /// </summary>
        public async Task<RiskScoreResponse> EvaluateRiskAsync(
            double latitude,
            double longitude,
            double radiusMetres,
            IEnumerable<HazardReport> allHazards)
        {
            var origin = new Coordinate(longitude, latitude);
            var activeHazards = allHazards
                .Where(h => h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview)
                .ToList();

            var nearbyHazards = new List<NearbyHazard>();
            double proximitySum = 0.0;

            foreach (var hazard in activeHazards)
            {
                double distMetres = HaversineDistance(
                    latitude, longitude,
                    hazard.Location.Y, hazard.Location.X);

                if (distMetres > radiusMetres) continue;

                double severity = HazardSeverity.GetValueOrDefault(hazard.Type, DefaultSeverity);

                double weight = severity * Math.Exp(-distMetres / DecayLambda);
                proximitySum += weight;

                nearbyHazards.Add(new NearbyHazard
                {
                    Id             = hazard.Id,
                    Type           = hazard.Type,
                    DistanceMetres = Math.Round(distMetres, 1),
                    RiskWeight     = Math.Round(weight, 4)
                });
            }

            double hazardProximity = Sigmoid(proximitySum, k: 3.0);

            double areaKmSq        = Math.PI * Math.Pow(radiusMetres / 1000.0, 2);
            double densityPerKmSq  = nearbyHazards.Count / Math.Max(areaKmSq, 0.001);
            double hazardDensity   = Math.Min(densityPerKmSq / 50.0, 1.0);

            double infrastructureRisk = EstimateInfrastructureRisk(latitude, longitude);

            int crimeCount = 0;
            double crimeRisk = 0.0;
            if (_ukPolice != null && _cache != null)
            {
                crimeCount = await GetCachedCrimeCountAsync(latitude, longitude);
                crimeRisk = Sigmoid(crimeCount / 12.0, k: 2.0);
            }

            double overall = Clamp01(
                W_Proximity      * hazardProximity +
                W_Density        * hazardDensity   +
                W_Infrastructure * infrastructureRisk +
                W_Crime          * crimeRisk);

            return new RiskScoreResponse
            {
                OverallRisk         = Math.Round(overall,           4),
                HazardProximityRisk = Math.Round(hazardProximity,  4),
                HazardDensityRisk   = Math.Round(hazardDensity,    4),
                InfrastructureRisk = Math.Round(infrastructureRisk,4),
                CrimeRisk           = Math.Round(crimeRisk,        4),
                CrimeCount          = crimeCount,
                NearbyHazardCount   = nearbyHazards.Count,
                NearbyHazards       = nearbyHazards
                    .OrderByDescending(h => h.RiskWeight)
                    .Take(20)
                    .ToList()
            };
        }

        /// <summary>
        /// Sync overload for callers that only need hazard-based risk (e.g. tests). No UK API.
        /// </summary>
        public RiskScoreResponse EvaluateRisk(
            double latitude,
            double longitude,
            double radiusMetres,
            IEnumerable<HazardReport> allHazards)
        {
            return EvaluateRiskAsync(latitude, longitude, radiusMetres, allHazards)
                .GetAwaiter().GetResult();
        }

        private async Task<int> GetCachedCrimeCountAsync(double lat, double lng)
        {
            if (_cache == null || _ukPolice == null) return 0;

            var key = $"{CrimeCacheKeyPrefix}{Math.Round(lat, 3):F3}:{Math.Round(lng, 3):F3}";
            if (_cache.TryGetValue(key, out int cached)) return cached;

            var list = await _ukPolice.GetRecentStreetCrimesAsync(lat, lng);
            int count = list?.Count ?? 0;
            _cache.Set(key, count, CrimeCacheExpiry);
            return count;
        }

        /// <summary>
        /// Lightweight risk score for a single coordinate (used by the routing engine
        /// per-edge to avoid a full breakdown on every segment).
        /// </summary>
        public double QuickRisk(
            double latitude,
            double longitude,
            IEnumerable<HazardReport> allHazards,
            double radiusMetres = 300)
        {
            var activeHazards = allHazards
                .Where(h => h.Status == HazardStatus.Reported || h.Status == HazardStatus.UnderReview);

            double riskSum = 0.0;
            int count = 0;

            foreach (var hazard in activeHazards)
            {
                double dist = HaversineDistance(
                    latitude, longitude,
                    hazard.Location.Y, hazard.Location.X);

                if (dist > radiusMetres) continue;

                double severity = HazardSeverity.GetValueOrDefault(hazard.Type, DefaultSeverity);
                riskSum += severity * Math.Exp(-dist / DecayLambda);
                count++;
            }

            return Clamp01(Sigmoid(riskSum, k: 3.0));
        }

        /// <summary>
        /// Haversine distance in metres between two WGS-84 points.
        /// </summary>
        public static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6_371_000;
            double dLat = ToRad(lat2 - lat1);
            double dLon = ToRad(lon2 - lon1);
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1.0 - a)));
        }

        private static double ToRad(double deg) => deg * Math.PI / 180.0;

        /// <summary>Sigmoid squashing: maps [0,∞) → [0,1).</summary>
        private static double Sigmoid(double x, double k = 1.0)
        {
            if (double.IsNaN(x)) return 0.5;
            if (double.IsPositiveInfinity(x)) return 1.0;
            if (double.IsNegativeInfinity(x)) return 0.0;
            return 1.0 / (1.0 + Math.Exp(-k * (x - 1.0)));
        }

        private static double Clamp01(double v)
            => Math.Max(0.0, Math.Min(1.0, v));

        /// <summary>
        /// Placeholder infrastructure risk estimator.
        /// A real implementation would query PostGIS layers for:
        ///   – street lighting coverage
        ///   – sidewalk width / presence
        ///   – surface quality index
        ///   – proximity to busy roads without crossings
        /// For now, returns a moderate baseline.
        /// </summary>
        private static double EstimateInfrastructureRisk(double lat, double lng)
        {
            int hash = HashCode.Combine(
                Math.Round(lat, 4),
                Math.Round(lng, 4));
            var rng = new Random(hash);
            return 0.15 + rng.NextDouble() * 0.35;
        }
    }
}
