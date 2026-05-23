using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AccessCity.API.Models;

namespace AccessCity.API.Services;

public static class RouteRequestFingerprint
{
    public const string AlgorithmVersion = "route-v5-precomputed-edge-cost-v1-risk-v2";

    public static string CanonicalPreferences(IEnumerable<string>? preferences)
    {
        if (preferences is null)
        {
            return "none";
        }

        var values = preferences
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return values.Length == 0 ? "none" : string.Join(",", values);
    }

    public static string HazardContext(IEnumerable<HazardReport>? hazards)
    {
        if (hazards is null)
        {
            return $"{AlgorithmVersion}:haz:none";
        }

        var activeHazards = hazards
            .Where(hazard => hazard.Status is HazardStatus.Reported or HazardStatus.UnderReview)
            .OrderBy(hazard => hazard.Id)
            .Select(hazard => string.Create(
                CultureInfo.InvariantCulture,
                $"{hazard.Id:N}:{hazard.Status}:{hazard.Type}:{hazard.ReportedAt.Ticks}:{hazard.Location.X:F5}:{hazard.Location.Y:F5}"))
            .ToArray();

        if (activeHazards.Length == 0)
        {
            return $"{AlgorithmVersion}:haz:none";
        }

        var payload = string.Join("|", activeHazards);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"{AlgorithmVersion}:haz:{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}";
    }
}
