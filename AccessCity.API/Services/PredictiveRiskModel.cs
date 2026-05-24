using AccessCity.API.Models;
using AccessCity.API.Services.External;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace AccessCity.API.Services
{
    public interface IPredictiveRiskModel
    {
        Task<PredictiveRiskResult> EvaluateSegmentRiskAsync(
            double lat,
            double lon,
            IEnumerable<HazardReport> hazards,
            double radiusMetres = 200);

        double QuickPredictiveRisk(
            double lat,
            double lon,
            IEnumerable<HazardReport> hazards,
            double radiusMetres = 200);
    }

    /// <summary>
    /// Deterministic multi-factor risk model for route safety scoring.
    /// 
    /// Combines multiple real-time signals into a unified risk prediction:
    ///   • Time-of-day risk (circadian safety patterns)
    ///   • Live weather conditions (rain/snow/ice/fog amplify risk)
    ///   • UK Police street crime data (cached, no DB)
    ///   • Reported accessibility hazards (proximity + density)
    ///   • Infrastructure quality heuristics
    /// 
    /// Each factor is weighted with fixed, reviewable coefficients derived
    /// from urban pedestrian safety literature and production calibration.
    /// LLM outputs must not feed this model or change edge costs at runtime.
    /// </summary>
    public class PredictiveRiskModel : IPredictiveRiskModel
    {
        private readonly IRiskScoringService _baseRisk;
        private readonly IHazardRiskGrid _hazardRiskGrid;
        private readonly ILiveHazardClient? _weatherClient;
        private readonly IMemoryCache? _cache;
        private readonly TimeSpan _externalSignalBudget;
        private readonly bool _realtimeExternalSignalsEnabled;

        // ──── Fixed model weights (logistic regression coefficients) ────
        // Derived from urban pedestrian safety research:
        //   Ewing & Dumbaugh 2009, Loukaitou-Sideris 2006, WHO pedestrian safety reports
        private const double W_Hazard = 0.35;
        private const double W_TimeOfDay = 0.10;
        private const double W_Weather = 0.10;
        private const double W_Crime = 0.12;
        private const double W_Infra = 0.12;
        private const double W_Lighting = 0.12;
        private const double W_Surveillance = 0.09;
        private const double DefaultInfrastructureRisk = 0.35;
        private static readonly TimeSpan DefaultExternalSignalBudget = TimeSpan.FromMilliseconds(350);

        public PredictiveRiskModel(
            IRiskScoringService baseRisk,
            IHazardRiskGrid hazardRiskGrid,
            ILiveHazardClient? weatherClient = null,
            IMemoryCache? cache = null,
            IConfiguration? configuration = null)
        {
            _baseRisk = baseRisk;
            _hazardRiskGrid = hazardRiskGrid;
            _weatherClient = weatherClient;
            _cache = cache;
            var budgetMs = configuration?.GetValue("RiskScoring:ExternalSignalBudgetMilliseconds", (int)DefaultExternalSignalBudget.TotalMilliseconds)
                ?? (int)DefaultExternalSignalBudget.TotalMilliseconds;
            _externalSignalBudget = TimeSpan.FromMilliseconds(Math.Max(50, budgetMs));
            _realtimeExternalSignalsEnabled = configuration?.GetValue("RiskScoring:RealtimeExternalSignalsEnabled", true) ?? true;
        }

        /// <summary>
        /// Compute a deterministic multi-factor risk score for a route segment.
        /// Returns a score in [0, 1] where 1 = maximum risk.
        /// </summary>
        public async Task<PredictiveRiskResult> EvaluateSegmentRiskAsync(
            double lat, double lon,
            IEnumerable<HazardReport> hazards,
            double radiusMetres = 200)
        {
            // Factor 1: Base hazard risk (proximity + density)
            double hazardRisk = _baseRisk.QuickRisk(lat, lon, hazards, radiusMetres);

            // Factor 2: Time-of-day risk
            double timeRisk = ComputeTimeOfDayRisk(DateTime.UtcNow);

            // Factor 3: Weather risk
            double weatherRisk = _realtimeExternalSignalsEnabled
                ? await WithExternalSignalBudgetAsync(
                    WeatherRiskEvaluator.GetRiskAsync(_weatherClient, _cache, lat, lon),
                    0.1)
                : WeatherRiskEvaluator.GetCachedRisk(_cache, lat, lon);

            // Factor 4: Crime risk — uses cached UK Police street crime data
            double crimeRisk = _baseRisk.QuickCrimeRisk(lat, lon);

            // Factor 5: Infrastructure quality — uses real PostGIS route edge data
            double infraRisk = await EstimateInfrastructureRiskWithBudgetAsync(lat, lon, radiusMetres);
            double lightingRisk = _baseRisk.QuickLightingCoverage(lat, lon);
            double surveillanceRisk = _baseRisk.QuickSurveillanceCoverage(lat, lon);

            // ──── Logistic Regression Combination ────
            // z = w₁·x₁ + w₂·x₂ + ... + wₙ·xₙ
            double z = W_Hazard * hazardRisk +
                       W_TimeOfDay * timeRisk +
                       W_Weather * weatherRisk +
                       W_Crime * crimeRisk +
                       W_Infra * infraRisk +
                       W_Lighting * lightingRisk +
                       W_Surveillance * surveillanceRisk;

            // Apply sigmoid activation for final prediction
            // Adjusted midpoint to 0.6 to allow for more 'Safe' (high score) headroom
            double overallRisk = Sigmoid(z, k: 5.0, midpoint: 0.60);

            return new PredictiveRiskResult
            {
                OverallRisk = Math.Round(overallRisk, 4),
                HazardRisk = Math.Round(hazardRisk, 4),
                TimeOfDayRisk = Math.Round(timeRisk, 4),
                WeatherRisk = Math.Round(weatherRisk, 4),
                CrimeRisk = Math.Round(crimeRisk, 4),
                InfrastructureRisk = Math.Round(infraRisk, 4),
                LightingRisk = Math.Round(lightingRisk, 4),
                SurveillanceRisk = Math.Round(surveillanceRisk, 4),
                RiskFactors = GenerateRiskFactors(hazardRisk, timeRisk, weatherRisk, crimeRisk, infraRisk)
            };
        }

        /// <summary>
        /// Fast synchronous version for per-edge scoring during A* search.
        /// Skips the async weather call and uses cached weather data.
        /// </summary>
        public double QuickPredictiveRisk(
            double lat, double lon,
            IEnumerable<HazardReport> hazards,
            double radiusMetres = 200)
        {
            // O(1) grid lookup when the precomputed risk grid is available;
            // falls back to the original O(N) QuickRisk linear scan otherwise.
            double hazardRisk = _hazardRiskGrid.IsReady
                ? _hazardRiskGrid.GetRisk(lat, lon)
                : _baseRisk.QuickRisk(lat, lon, hazards, radiusMetres);
            double timeRisk = ComputeTimeOfDayRisk(DateTime.UtcNow);
            double weatherRisk = WeatherRiskEvaluator.GetCachedRisk(_cache, lat, lon);
            double crimeRisk = _baseRisk.QuickCrimeRisk(lat, lon);
            double infraRisk = _baseRisk.QuickInfrastructureRisk(lat, lon, radiusMetres);
            double lightingRisk = _baseRisk.QuickLightingCoverage(lat, lon);
            double surveillanceRisk = _baseRisk.QuickSurveillanceCoverage(lat, lon);

            double z = W_Hazard * hazardRisk +
                       W_TimeOfDay * timeRisk +
                       W_Weather * weatherRisk +
                       W_Crime * crimeRisk +
                       W_Infra * infraRisk +
                       W_Lighting * lightingRisk +
                       W_Surveillance * surveillanceRisk;

            return Math.Clamp(Sigmoid(z, k: 5.0, midpoint: 0.60), 0, 1);
        }

        private async Task<double> EstimateInfrastructureRiskWithBudgetAsync(
            double lat,
            double lon,
            double radiusMetres)
        {
            return await WithExternalSignalBudgetAsync(
                token => _baseRisk.EstimateInfrastructureRiskAsync(lat, lon, radiusMetres, token),
                DefaultInfrastructureRisk);
        }

        private async Task<T> WithExternalSignalBudgetAsync<T>(Task<T> task, T fallback)
        {
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

        private async Task<T> WithExternalSignalBudgetAsync<T>(
            Func<CancellationToken, Task<T>> factory,
            T fallback)
        {
            using var budgetCts = new CancellationTokenSource(_externalSignalBudget);

            try
            {
                return await factory(budgetCts.Token);
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested)
            {
                return fallback;
            }
            catch
            {
                return fallback;
            }
        }

        // ──────── Factor Computations ────────

        /// <summary>
        /// Time-of-day risk using a circadian safety curve.
        /// Pedestrian incidents peak between 18:00-06:00 (darkness hours).
        /// Based on: NHTSA pedestrian safety statistics, WHO Global Status Report.
        /// </summary>
        private static double ComputeTimeOfDayRisk(DateTime utcNow)
        {
            // Convert to approximate UK local time (UTC+0 or UTC+1 for BST)
            int hour = utcNow.Hour;

            // Piecewise circadian risk curve
            return hour switch
            {
                < 2 => 0.80,  // Late night — peak risk
                >= 2 and < 4 => 0.75,  // Deep night — very high
                >= 4 and < 6 => 0.50,  // Pre-dawn — high
                >= 6 and < 8 => 0.15,  // Early morning — moderate visibility
                >= 8 and < 10 => 0.05,  // Morning — low risk
                >= 10 and < 16 => 0.02,  // Daytime — minimal risk
                >= 16 and < 18 => 0.10,  // Late afternoon — increasing
                >= 18 and < 20 => 0.30,  // Early evening — dusk
                >= 20 and < 22 => 0.55,  // Night — elevated
                _ => 0.80   // >= 22 — late night peak risk
            };
        }

        /// <summary>
        /// Weather-based risk from live OpenWeatherMap data.
        /// Adverse conditions (rain, snow, ice, fog) increase pedestrian risk.
        /// Based on: Brodsky & Hakkert 1988, Eisenberg 2004 weather-crash studies.
        /// </summary>
        // Crime and infrastructure risk are now delegated to RiskScoringService
        // which uses real cached UK Police data and PostGIS infrastructure queries.

        /// <summary>
        /// Generate human-readable risk factor explanations for the API response.
        /// </summary>
        private static List<string> GenerateRiskFactors(
            double hazardRisk, double timeRisk, double weatherRisk,
            double crimeRisk, double infraRisk)
        {
            var factors = new List<string>();

            if (timeRisk > 0.4)
                factors.Add("⚠️ Elevated night-time risk — reduced visibility and fewer pedestrians.");
            if (weatherRisk > 0.3)
                factors.Add("🌧️ Adverse weather conditions increase slip and visibility risk.");
            if (hazardRisk > 0.3)
                factors.Add("🚧 Reported hazards detected near this route segment.");
            if (crimeRisk > 0.2)
                factors.Add("🔒 Historical crime data suggests elevated caution in this area.");
            if (infraRisk > 0.3)
                factors.Add("🏗️ Infrastructure quality may be reduced (poor lighting or pavement).");

            if (factors.Count == 0)
                factors.Add("✅ No significant risk factors detected for this segment.");

            return factors;
        }

        // ──── Math helpers ────

        private static double Sigmoid(double x, double k = 1.0, double midpoint = 0.5)
        {
            if (double.IsNaN(x)) return 0.5;
            return 1.0 / (1.0 + Math.Exp(-k * (x - midpoint)));
        }
    }
}
