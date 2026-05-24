using AccessCity.API.Models;

namespace AccessCity.API.Services;

/// <summary>
/// Precomputed 2D grid of hazard risk values covering the metropolitan area.
/// Built by sampling the <see cref="IHazardSpatialIndex"/> at regular intervals.
///
/// This makes the A* inner loop's per-edge risk evaluation O(1) — a single
/// array lookup instead of a linear scan through all nearby hazards.
///
/// The grid is rebuilt every time the hazard spatial index refreshes (every 30s).
/// Grid cell size is configurable but defaults to 100m (~0.0009 degrees).
/// </summary>
public interface IHazardRiskGrid
{
    /// <summary>
    /// Returns the precomputed risk value [0.0, 1.0] at the given coordinate.
    /// Returns 0.0 if the coordinate is outside the grid bounds.
    /// This is an O(1) array lookup.
    /// </summary>
    double GetRisk(double latitude, double longitude);

    /// <summary>
    /// Returns true if the grid has been built at least once.
    /// </summary>
    bool IsReady { get; }
}

public sealed class HazardRiskGrid : IHazardRiskGrid
{
    /// <summary>Cell size in degrees (~100m at mid-latitudes).</summary>
    private const double DefaultCellSizeDegrees = 0.0009;

    /// <summary>Decay constant for distance-weighted hazard risk blending (metres).</summary>
    private const double DecayLambda = 150.0;

    private volatile GridSnapshot _snapshot = GridSnapshot.Empty;

    /// <inheritdoc/>
    public bool IsReady => _snapshot.IsReady;

    /// <inheritdoc/>
    public double GetRisk(double latitude, double longitude)
    {
        var snapshot = _snapshot;
        if (!snapshot.IsReady) return 0.0;

        int row = (int)((latitude - snapshot.MinLat) / snapshot.CellSize);
        int col = (int)((longitude - snapshot.MinLon) / snapshot.CellSize);

        if (row < 0 || row >= snapshot.Rows || col < 0 || col >= snapshot.Cols)
            return 0.0;

        return snapshot.Grid[row, col];
    }

    /// <summary>
    /// Rebuilds the risk grid from the current hazard spatial index.
    /// Called after the R-Tree is refreshed. Typically completes in < 10ms
    /// for a 100x100 grid (10km × 10km city with 100m cells).
    /// </summary>
    public void Rebuild(IHazardSpatialIndex index, double cellSizeDegrees = DefaultCellSizeDegrees)
    {
        var allHazards = index.GetAllActive();
        if (allHazards.Count == 0)
        {
            _snapshot = new GridSnapshot(
                new double[0, 0], 0, 0, 0, 0, cellSizeDegrees, true);
            return;
        }

        // Compute bounding box of all hazards, with padding
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (var h in allHazards)
        {
            if (h.Location is null) continue;
            minLat = Math.Min(minLat, h.Location.Y);
            maxLat = Math.Max(maxLat, h.Location.Y);
            minLon = Math.Min(minLon, h.Location.X);
            maxLon = Math.Max(maxLon, h.Location.X);
        }

        // Add buffer around the hazard area
        var bufferDegrees = cellSizeDegrees * 10;
        minLat -= bufferDegrees;
        maxLat += bufferDegrees;
        minLon -= bufferDegrees;
        maxLon += bufferDegrees;

        int rows = Math.Clamp((int)Math.Ceiling((maxLat - minLat) / cellSizeDegrees), 1, 2000);
        int cols = Math.Clamp((int)Math.Ceiling((maxLon - minLon) / cellSizeDegrees), 1, 2000);

        var grid = new double[rows, cols];

        // For each grid cell, query the R-Tree and compute the blended risk
        for (int r = 0; r < rows; r++)
        {
            double cellLat = minLat + (r + 0.5) * cellSizeDegrees;
            for (int c = 0; c < cols; c++)
            {
                double cellLon = minLon + (c + 0.5) * cellSizeDegrees;
                var nearby = index.QueryNearby(cellLat, cellLon, 300);
                grid[r, c] = ComputeCellRisk(cellLat, cellLon, nearby);
            }
        }

        _snapshot = new GridSnapshot(grid, rows, cols, minLat, minLon, cellSizeDegrees, true);
    }

    private static double ComputeCellRisk(
        double lat, double lon,
        IReadOnlyList<HazardReport> nearbyHazards)
    {
        if (nearbyHazards.Count == 0) return 0.0;

        double riskSum = 0;
        foreach (var hazard in nearbyHazards)
        {
            if (hazard.Location is null) continue;
            double dist = RiskScoringService.HaversineDistance(
                lat, lon, hazard.Location.Y, hazard.Location.X);
            double severity = HazardSeverityLookup.GetSeverity(hazard.Type);
            riskSum += severity * Math.Exp(-dist / DecayLambda);
        }

        return Math.Clamp(riskSum, 0.0, 1.0);
    }

    private sealed record GridSnapshot(
        double[,] Grid,
        int Rows,
        int Cols,
        double MinLat,
        double MinLon,
        double CellSize,
        bool IsReady)
    {
        public static readonly GridSnapshot Empty =
            new(new double[0, 0], 0, 0, 0, 0, DefaultCellSizeDegrees, false);
    }
}

/// <summary>
/// Centralized hazard severity lookup. Extracted to avoid duplicating the severity
/// map across RiskScoringService and HazardRiskGrid.
/// </summary>
public static class HazardSeverityLookup
{
    private const double DefaultSeverity = 0.5;

    private static readonly Dictionary<string, double> SeverityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["broken_sidewalk"] = 0.7,
        ["missing_ramp"] = 0.9,
        ["obstruction"] = 0.6,
        ["poor_lighting"] = 0.5,
        ["steep_slope"] = 0.6,
        ["flooding"] = 0.8,
        ["construction"] = 0.7,
        ["no_sidewalk"] = 0.85,
        ["narrow_path"] = 0.6,
        ["uneven_surface"] = 0.55,
        ["temporary_closure"] = 0.9,
        ["pothole"] = 0.5,
        ["signal_missing"] = 0.7,
    };

    public static double GetSeverity(string? hazardType)
    {
        if (string.IsNullOrWhiteSpace(hazardType)) return DefaultSeverity;
        return SeverityMap.GetValueOrDefault(hazardType, DefaultSeverity);
    }
}
