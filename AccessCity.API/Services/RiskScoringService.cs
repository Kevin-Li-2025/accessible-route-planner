using NetTopologySuite.Geometries;
using AccessCity.API.Models;
using AccessCity.API.Models.External;
using AccessCity.API.Services.External;
using AccessCity.API.Data;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

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
        private readonly IDistributedCache? _distributedCache;
        private readonly AccessCityMetrics? _metrics;
        private readonly AppDbContext _dbContext;
        private readonly TimeSpan _externalSignalBudget;

        private const string CrimeCacheKeyPrefix = "ukcrime:";
        private const string EnvCacheKeyPrefix = "env:";
        private static readonly TimeSpan CrimeCacheExpiry = TimeSpan.FromHours(24);
        private static readonly TimeSpan EnvCacheExpiry = TimeSpan.FromHours(1);
        private static readonly TimeSpan DefaultExternalSignalBudget = TimeSpan.FromMilliseconds(350);
        private const double DefaultInfrastructureRisk = 0.35;
        private const double DefaultLightingRisk = 0.30;
        private const double DefaultSurveillanceRisk = 0.40;

        private static readonly Dictionary<string, double> HazardSeverity = new(StringComparer.OrdinalIgnoreCase)
        {
            ["pothole"] = 0.6,
            ["broken_pavement"] = 0.5,
            ["missing_curb_ramp"] = 0.7,
            ["obstruction"] = 0.5,
            ["poor_lighting"] = 0.8,
            ["construction"] = 0.7,
            ["flooding"] = 0.9,
            ["missing_tactile"] = 0.6,
            ["steep_gradient"] = 0.5,
            ["narrow_sidewalk"] = 0.4,
            ["uneven_surface"] = 0.5,
            ["missing_handrail"] = 0.6,
            ["traffic_hazard"] = 0.8,
            ["missing_crossing"] = 0.7,
        };

        private const double DefaultSeverity = 0.5;

        // Rebalanced to include lighting and surveillance coverage.
        private const double W_Proximity = 0.35;
        private const double W_Density = 0.20;
        private const double W_Infrastructure = 0.15;
        private const double W_Crime = 0.12;
        private const double W_Lighting = 0.10;
        private const double W_Surveillance = 0.08;

        private const double DecayLambda = 150.0;

        public RiskScoringService(
            AppDbContext dbContext,
            IUkPoliceDataClient? ukPolice = null,
            ILiveHazardClient? weatherClient = null,
            IMemoryCache? cache = null,
            IEnvironmentalDataClient? envClient = null,
            IDistributedCache? distributedCache = null,
            AccessCityMetrics? metrics = null,
            IConfiguration? configuration = null)
        {
            _dbContext = dbContext;
            _ukPolice = ukPolice;
            _weatherClient = weatherClient;
            _cache = cache;
            _envClient = envClient;
            _distributedCache = distributedCache;
            _metrics = metrics;
            var externalBudgetMs = configuration?.GetValue("RiskScoring:ExternalSignalBudgetMilliseconds", (int)DefaultExternalSignalBudget.TotalMilliseconds)
                ?? (int)DefaultExternalSignalBudget.TotalMilliseconds;
            _externalSignalBudget = TimeSpan.FromMilliseconds(Math.Max(50, externalBudgetMs));
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
                    Id = hazard.Id,
                    Type = hazard.Type,
                    DistanceMetres = Math.Round(distMetres, 1),
                    RiskWeight = Math.Round(weight, 4)
                });
            }

            double hazardProximity = Sigmoid(proximitySum, k: 3.0);

            double areaKmSq = Math.PI * Math.Pow(radiusMetres / 1000.0, 2);
            double densityPerKmSq = nearbyHazards.Count / Math.Max(areaKmSq, 0.001);
            double hazardDensity = Math.Min(densityPerKmSq / 50.0, 1.0);

            var infrastructureRiskTask = WithExternalSignalBudgetAsync(
                EstimateInfrastructureRiskAsync(latitude, longitude, radiusMetres),
                DefaultInfrastructureRisk);
            var crimeCountTask = _ukPolice != null && _cache != null
                ? WithExternalSignalBudgetAsync(GetCachedCrimeCountAsync(latitude, longitude), 0)
                : Task.FromResult(0);
            var environmentalRisksTask = WithExternalSignalBudgetAsync(
                GetCachedEnvironmentalRisksAsync(latitude, longitude, radiusMetres),
                (DefaultLightingRisk, DefaultSurveillanceRisk));

            double infrastructureRisk = await infrastructureRiskTask;
            int crimeCount = await crimeCountTask;
            double crimeRisk = crimeCount > 0 ? Sigmoid(crimeCount / 12.0, k: 2.0) : 0.0;
            var (lightingRisk, surveillanceRisk) = await environmentalRisksTask;

            double overall = Clamp01(
                W_Proximity * hazardProximity +
                W_Density * hazardDensity +
                W_Infrastructure * infrastructureRisk +
                W_Crime * crimeRisk +
                W_Lighting * lightingRisk +
                W_Surveillance * surveillanceRisk);

            return new RiskScoreResponse
            {
                OverallRisk = Math.Round(overall, 4),
                HazardProximityRisk = Math.Round(hazardProximity, 4),
                HazardDensityRisk = Math.Round(hazardDensity, 4),
                InfrastructureRisk = Math.Round(infrastructureRisk, 4),
                CrimeRisk = Math.Round(crimeRisk, 4),
                LightingRisk = Math.Round(lightingRisk, 4),
                SurveillanceRisk = Math.Round(surveillanceRisk, 4),
                CrimeCount = crimeCount,
                NearbyHazardCount = nearbyHazards.Count,
                NearbyHazards = nearbyHazards
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
            if (_ukPolice == null) return 0;

            var key = $"{CrimeCacheKeyPrefix}{Math.Round(lat, 3):F3}:{Math.Round(lng, 3):F3}";
            var cachedCrime = await TryGetCachedAsync<int>(key, "crime");
            if (cachedCrime.Hit)
            {
                return cachedCrime.Value;
            }

            var list = await _ukPolice.GetRecentStreetCrimesAsync(lat, lng);
            int count = list?.Count ?? 0;
            await SetCachedAsync(key, count, CrimeCacheExpiry);
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

        private async Task<(double LightingRisk, double SurveillanceRisk)> GetCachedEnvironmentalRisksAsync(
            double lat,
            double lng,
            double radiusMetres)
        {
            if (_envClient == null) return (DefaultLightingRisk, DefaultSurveillanceRisk);

            var lampKey = $"{EnvCacheKeyPrefix}lamp:{Math.Round(lat, 3):F3}:{Math.Round(lng, 3):F3}";
            var camKey = $"{EnvCacheKeyPrefix}cam:{Math.Round(lat, 3):F3}:{Math.Round(lng, 3):F3}";
            var cachedLighting = await TryGetCachedAsync<double>(lampKey, "environment");
            var cachedSurveillance = await TryGetCachedAsync<double>(camKey, "environment");
            if (cachedLighting.Hit && cachedSurveillance.Hit)
            {
                return (cachedLighting.Value, cachedSurveillance.Value);
            }

            var summary = await _envClient.GetNearbyInfrastructureAsync(lat, lng, Math.Min(radiusMetres, 300));
            double risk = Math.Clamp(1.0 - (summary.StreetLampCount / 10.0), 0, 1);
            double camRisk = Math.Clamp(1.0 - (summary.SurveillanceCameraCount / 3.0), 0, 1);
            await SetCachedAsync(lampKey, risk, EnvCacheExpiry);
            await SetCachedAsync(camKey, camRisk, EnvCacheExpiry);

            return (risk, camRisk);
        }

        private async Task<double> GetCachedLightingRiskAsync(double lat, double lng, double radiusMetres)
        {
            var risks = await GetCachedEnvironmentalRisksAsync(lat, lng, radiusMetres);
            return risks.LightingRisk;
        }

        private async Task<double> GetCachedSurveillanceRiskAsync(double lat, double lng, double radiusMetres)
        {
            var risks = await GetCachedEnvironmentalRisksAsync(lat, lng, radiusMetres);
            return risks.SurveillanceRisk;
        }

        private async Task<T> WithExternalSignalBudgetAsync<T>(Task<T> task, T fallback)
        {
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            try
            {
                return await task.WaitAsync(_externalSignalBudget);
            }
            catch (TimeoutException)
            {
                return fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private async Task<(bool Hit, T Value)> TryGetCachedAsync<T>(string key, string cacheName)
        {
            var stopwatch = Stopwatch.StartNew();
            if (_cache != null && _cache.TryGetValue(key, out T? memoryValue) && memoryValue is not null)
            {
                stopwatch.Stop();
                _metrics?.CacheLookup(cacheName, hit: true, stopwatch.Elapsed.TotalMilliseconds);
                return (true, memoryValue);
            }

            if (_distributedCache != null)
            {
                var json = await _distributedCache.GetStringAsync(key);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var distributedValue = JsonSerializer.Deserialize<T>(json);
                    if (distributedValue is not null)
                    {
                        _cache?.Set(key, distributedValue, TimeSpan.FromMinutes(2));
                        stopwatch.Stop();
                        _metrics?.CacheLookup(cacheName, hit: true, stopwatch.Elapsed.TotalMilliseconds);
                        return (true, distributedValue);
                    }
                }
            }

            stopwatch.Stop();
            _metrics?.CacheLookup(cacheName, hit: false, stopwatch.Elapsed.TotalMilliseconds);
            return (false, default!);
        }

        private async Task SetCachedAsync<T>(string key, T value, TimeSpan ttl)
        {
            _cache?.Set(key, value, ttl);
            if (_distributedCache == null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(value);
            await _distributedCache.SetStringAsync(
                key,
                json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });
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
            List<RouteEdge> nearbyEdges;
            if (string.Equals(_dbContext.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
            {
                nearbyEdges = await _dbContext.RouteEdges
                    .FromSqlInterpolated($"""
                        SELECT *
                        FROM route_edges
                        WHERE ST_DWithin(
                            "Geometry",
                            ST_SetSRID(ST_MakePoint({lng}, {lat}), 4326),
                            {radiusMetres / 111_320.0})
                        ORDER BY "Geometry" <-> ST_SetSRID(ST_MakePoint({lng}, {lat}), 4326)
                        LIMIT 50
                        """)
                    .AsNoTracking()
                    .ToListAsync();
            }
            else
            {
                nearbyEdges = await _dbContext.RouteEdges
                    .Where(e => e.Geometry.Distance(queryPoint) < (radiusMetres / 100000.0))
                    .Take(50)
                    .ToListAsync();
            }

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
