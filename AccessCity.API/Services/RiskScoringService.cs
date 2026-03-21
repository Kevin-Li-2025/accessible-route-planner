using NetTopologySuite.Geometries;
using AccessCity.API.Models;
using AccessCity.API.Models.External;
using AccessCity.API.Services.External;
using AccessCity.API.Data;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
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
        private readonly ILiveHazardClient? _weatherClient;
        private readonly IEnvironmentalDataClient? _envClient;
        private readonly IMemoryCache? _cache;
        private readonly AppDbContext _dbContext;

        private const string CrimeCacheKeyPrefix = "ukcrime:";
        private const string EnvCacheKeyPrefix = "env:";
        private static readonly TimeSpan CrimeCacheExpiry = TimeSpan.FromHours(24);
        private static readonly TimeSpan EnvCacheExpiry = TimeSpan.FromHours(1);

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

        // Rebalanced to include lighting and surveillance coverage.
        private const double W_Proximity       = 0.35;
        private const double W_Density         = 0.20;
        private const double W_Infrastructure  = 0.15;
        private const double W_Crime           = 0.12;
        private const double W_Lighting        = 0.10;
        private const double W_Surveillance    = 0.08;

        private const double DecayLambda = 150.0;

        public RiskScoringService(
            AppDbContext dbContext,
            IUkPoliceDataClient? ukPolice = null,
            ILiveHazardClient? weatherClient = null,
            IMemoryCache? cache = null,
            IEnvironmentalDataClient? envClient = null)
        {
            _dbContext = dbContext;
            _ukPolice = ukPolice;
            _weatherClient = weatherClient;
            _cache = cache;
            _envClient = envClient;
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

            double infrastructureRisk = await EstimateInfrastructureRiskAsync(latitude, longitude, radiusMetres);

            int crimeCount = 0;
            double crimeRisk = 0.0;
            if (_ukPolice != null && _cache != null)
            {
                crimeCount = await GetCachedCrimeCountAsync(latitude, longitude);
                crimeRisk = Sigmoid(crimeCount / 12.0, k: 2.0);
            }

            double lightingRisk = await GetCachedLightingRiskAsync(latitude, longitude, radiusMetres);
            double surveillanceRisk = await GetCachedSurveillanceRiskAsync(latitude, longitude, radiusMetres);

            double overall = Clamp01(
                W_Proximity      * hazardProximity +
                W_Density        * hazardDensity   +
                W_Infrastructure * infrastructureRisk +
                W_Crime          * crimeRisk +
                W_Lighting       * lightingRisk +
                W_Surveillance   * surveillanceRisk);

            return new RiskScoreResponse
            {
                OverallRisk         = Math.Round(overall,           4),
                HazardProximityRisk = Math.Round(hazardProximity,  4),
                HazardDensityRisk   = Math.Round(hazardDensity,    4),
                InfrastructureRisk = Math.Round(infrastructureRisk,4),
                CrimeRisk           = Math.Round(crimeRisk,        4),
                LightingRisk        = Math.Round(lightingRisk,     4),
                SurveillanceRisk    = Math.Round(surveillanceRisk,  4),
                CrimeCount          = crimeCount,
                NearbyHazardCount   = nearbyHazards.Count,
                NearbyHazards       = nearbyHazards
                    .OrderByDescending(h => h.RiskWeight)
                    .Take(20)
                    .ToList()
            };
        }

        public async Task<PredictiveRiskResult> PredictRiskAsync(
            double latitude, 
            double longitude, 
            double radiusMetres, 
            IEnumerable<HazardReport> hazards)
        {
            var baseRisk = await EvaluateRiskAsync(latitude, longitude, radiusMetres, hazards);
            
            // 1. Time-of-day Factor
            var now = DateTime.UtcNow.TimeOfDay;
            double timeFactor = 0.2; // default
            if (now.Hours >= 22 || now.Hours < 5) timeFactor = 0.8; // Night risk
            else if (now.Hours >= 6 && now.Hours < 9) timeFactor = 0.4; // Morning rush

            // 2. Weather — same OpenWeather mapping as PredictiveRiskModel (shared cache key).
            double weatherFactor = await WeatherRiskEvaluator.GetRiskAsync(_weatherClient, _cache, latitude, longitude);

            var factors = new List<string>();
            if (baseRisk.HazardDensityRisk > 0.6) factors.Add("High density of reported hazards in this sector.");
            if (baseRisk.CrimeRisk > 0.5) factors.Add("Historical crime data indicates elevated risk.");
            if (timeFactor > 0.7) factors.Add("Increased risk due to late-night hours.");
            if (weatherFactor > 0.35) factors.Add("Weather conditions may increase slip or visibility risk.");
            if (baseRisk.InfrastructureRisk > 0.6) factors.Add("Infrastructure indicators suggest poor accessibility.");

            if (factors.Count == 0) factors.Add("Generally safe area with no major risk indicators.");

            double overallAiRisk = Clamp01(
                baseRisk.OverallRisk * 0.6 + 
                timeFactor * 0.2 + 
                weatherFactor * 0.2);

            return new PredictiveRiskResult
            {
                OverallRisk = Math.Round(overallAiRisk, 3),
                HazardRisk = baseRisk.HazardProximityRisk,
                TimeOfDayRisk = timeFactor,
                WeatherRisk = weatherFactor,
                CrimeRisk = baseRisk.CrimeRisk,
                InfrastructureRisk = baseRisk.InfrastructureRisk,
                RiskFactors = factors
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
        /// Lightweight crime risk score that ONLY checks the cache.
        /// Used by the routing engine for sub-millisecond per-edge cost evaluation.
        /// </summary>
        public double QuickCrimeRisk(double lat, double lng)
        {
            if (_cache == null) return 0.0;

            var key = $"{CrimeCacheKeyPrefix}{Math.Round(lat, 3):F3}:{Math.Round(lng, 3):F3}";
            if (_cache.TryGetValue(key, out int crimeCount))
            {
                return Sigmoid(crimeCount / 12.0, k: 2.0);
            }

            return 0.15; // Baseline risk when data is unknown (safer than 0, more cautious than 0.5)
        }

        /// <summary>
        /// Lightweight sync infrastructure risk score.
        /// Queries PostGIS route edges within ~100m of the point for lighting,
        /// stairs, and kerb height. Result is consistent with per-edge routing.
        /// Falls back to 0.25 baseline when no PostGIS data is available.
        /// </summary>
        public double QuickInfrastructureRisk(double lat, double lng)
        {
            if (_cache != null)
            {
                var cacheKey = $"infra:{Math.Round(lat, 4):F4}:{Math.Round(lng, 4):F4}";
                if (_cache.TryGetValue(cacheKey, out double cached)) return cached;

                try
                {
                    double risk = EstimateInfrastructureRiskAsync(lat, lng, 150)
                        .GetAwaiter().GetResult();
                    _cache.Set(cacheKey, risk, TimeSpan.FromMinutes(10));
                    return risk;
                }
                catch
                {
                    return 0.25;
                }
            }

            // No cache — just return baseline instead of random
            return 0.25;
        }

        /// <summary>Lighting coverage risk [0,1]. 0 = well-lit, 1 = no lamps.</summary>
        public double QuickLightingCoverage(double lat, double lng)
        {
            if (_cache == null) return 0.30;
            var key = $"{EnvCacheKeyPrefix}lamp:{lat.ToString("F3", CultureInfo.InvariantCulture)}:{lng.ToString("F3", CultureInfo.InvariantCulture)}";
            return _cache.TryGetValue(key, out double cached) ? cached : 0.30;
        }

        /// <summary>Surveillance coverage risk [0,1]. 0 = high CCTV, 1 = none.</summary>
        public double QuickSurveillanceCoverage(double lat, double lng)
        {
            if (_cache == null) return 0.40;
            var key = $"{EnvCacheKeyPrefix}cam:{lat.ToString("F3", CultureInfo.InvariantCulture)}:{lng.ToString("F3", CultureInfo.InvariantCulture)}";
            return _cache.TryGetValue(key, out double cached) ? cached : 0.40;
        }

        private async Task<double> GetCachedLightingRiskAsync(double lat, double lng, double radiusMetres)
        {
            if (_envClient == null || _cache == null) return 0.30;
            var key = $"{EnvCacheKeyPrefix}lamp:{Math.Round(lat, 3):F3}:{Math.Round(lng, 3):F3}";
            if (_cache.TryGetValue(key, out double cached)) return cached;

            var summary = await _envClient.GetNearbyInfrastructureAsync(lat, lng, Math.Min(radiusMetres, 300));
            // 10+ lamps within radius = well-lit, 0 = dark.
            double risk = Math.Clamp(1.0 - (summary.StreetLampCount / 10.0), 0, 1);
            _cache.Set(key, risk, EnvCacheExpiry);

            // Also cache surveillance while we have it.
            var camKey = $"{EnvCacheKeyPrefix}cam:{Math.Round(lat, 3):F3}:{Math.Round(lng, 3):F3}";
            double camRisk = Math.Clamp(1.0 - (summary.SurveillanceCameraCount / 3.0), 0, 1);
            _cache.Set(camKey, camRisk, EnvCacheExpiry);

            return risk;
        }

        private async Task<double> GetCachedSurveillanceRiskAsync(double lat, double lng, double radiusMetres)
        {
            if (_envClient == null || _cache == null) return 0.40;
            var key = $"{EnvCacheKeyPrefix}cam:{Math.Round(lat, 3):F3}:{Math.Round(lng, 3):F3}";
            if (_cache.TryGetValue(key, out double cached)) return cached;

            // Fetch and cache both at once.
            await GetCachedLightingRiskAsync(lat, lng, radiusMetres);
            return _cache.TryGetValue(key, out cached) ? cached : 0.40;
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
        /// Real infrastructure risk estimator based on OSM tag data.
        /// Queries PostGIS for nearby edges and evaluates lighting, stairs, and kerb height.
        /// </summary>
        private static readonly GeometryFactory Wgs84 = new(new PrecisionModel(), 4326);

        private async Task<double> EstimateInfrastructureRiskAsync(double lat, double lng, double radiusMetres)
        {
            var queryPoint = Wgs84.CreatePoint(new Coordinate(lng, lat));
            var center = _dbContext.RouteNodes.OrderBy(n => n.Location.Distance(queryPoint)).FirstOrDefault()?.Location;
            if (center == null) return 0.5; // Baseline if no data

            var nearbyEdges = await _dbContext.RouteEdges
                .Where(e => e.Geometry.Distance(queryPoint) < (radiusMetres / 100000.0)) // Rough degree approx for radius
                .Take(50)
                .ToListAsync();

            if (nearbyEdges.Count == 0) return 0.35;

            double lightingAvg = nearbyEdges.Average(e => e.LightingQuality);
            double stairDensity = nearbyEdges.Count(e => e.HasStairs) / (double)nearbyEdges.Count;
            double kerbHeightAvg = nearbyEdges.Average(e => e.KerbHeight);

            // Invert lighting (high quality = low risk)
            double lightingRisk = 1.0 - lightingAvg;
            
            // Stairs and high kerbs increase risk
            double mobilityRisk = (stairDensity * 0.7) + (Math.Min(kerbHeightAvg * 10.0, 1.0) * 0.3);

            return Clamp01(lightingRisk * 0.4 + mobilityRisk * 0.6);
        }
    }
}
