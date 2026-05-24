using AccessCity.API.Models;
using H3;
using H3.Algorithms;
using H3.Model;

namespace AccessCity.API.Services;

/// <summary>
/// Sparse H3-based risk grid that replaces the dense 2D array implementation.
///
/// Instead of allocating a fixed double[rows, cols] array that covers a bounding box
/// (which overflows at national/continental scale), this grid uses an
/// H3Index → double dictionary. Only hexagonal cells near active hazards are populated.
///
/// Memory characteristics:
///   - City scale (100 hazards):    ~5,000 H3 cells ≈ 40KB
///   - Metropolitan (1,000 hazards): ~50,000 H3 cells ≈ 400KB
///   - National (10,000 hazards):   ~500,000 H3 cells ≈ 4MB
///   - Dense 2D array (national):   60,000,000 cells ≈ 480MB ← eliminated
///
/// H3 resolution 9 has ~174m edge length, closely matching the previous 100m grid cell size.
/// Lookups are O(1) dictionary hash lookups — same asymptotic cost as the 2D array index.
/// </summary>
public sealed class H3HazardRiskGrid : IHazardRiskGrid
{
    /// <summary>
    /// H3 resolution 9: ~174m edge length, ~0.105 km² area.
    /// Closely matches the previous 100m grid cell size for risk evaluation granularity.
    /// </summary>
    private const int H3Resolution = 9;

    /// <summary>
    /// Number of hexagonal rings around each hazard to populate.
    /// k=3 covers ~500m radius — matching the previous 300m query radius
    /// with buffer for blending accuracy.
    /// </summary>
    private const int DiskRingK = 3;

    /// <summary>Decay constant for distance-weighted hazard risk blending (metres).</summary>
    private const double DecayLambda = 150.0;

    private static readonly double DegreesToRadians = Math.PI / 180.0;

    private volatile H3GridSnapshot _snapshot = H3GridSnapshot.Empty;

    /// <inheritdoc/>
    public bool IsReady => _snapshot.IsReady;

    /// <inheritdoc/>
    public double GetRisk(double latitude, double longitude)
    {
        var snapshot = _snapshot;
        if (!snapshot.IsReady) return 0.0;

        var latLng = new LatLng(latitude * DegreesToRadians, longitude * DegreesToRadians);
        var index = H3Index.FromLatLng(latLng, H3Resolution);

        return snapshot.Cells.TryGetValue((ulong)index, out var risk) ? risk : 0.0;
    }

    /// <summary>
    /// Rebuilds the sparse H3 risk grid from the current hazard spatial index.
    /// Called after the R-Tree is refreshed. Only populates cells near hazards,
    /// keeping memory proportional to hazard count rather than geographic area.
    /// </summary>
    public void Rebuild(IHazardSpatialIndex spatialIndex)
    {
        var allHazards = spatialIndex.GetAllActive();
        if (allHazards.Count == 0)
        {
            _snapshot = new H3GridSnapshot(
                new Dictionary<ulong, double>(), true);
            return;
        }

        // Collect all H3 cells that need risk values.
        // Using a HashSet to deduplicate cells in overlapping disk rings.
        var cellsToCompute = new HashSet<ulong>();

        foreach (var hazard in allHazards)
        {
            if (hazard.Location is null) continue;

            var latLng = new LatLng(
                hazard.Location.Y * DegreesToRadians,
                hazard.Location.X * DegreesToRadians);
            var center = H3Index.FromLatLng(latLng, H3Resolution);

            // GridDiskDistances returns (index, distance) tuples for k-ring
            foreach (var ringCell in Rings.GridDiskDistances(center, DiskRingK))
            {
                cellsToCompute.Add((ulong)ringCell.Index);
            }
        }

        // Compute risk for each unique H3 cell
        var newCells = new Dictionary<ulong, double>(cellsToCompute.Count);

        foreach (var cell in cellsToCompute)
        {
            var cellIndex = new H3Index(cell);
            var cellCenter = cellIndex.ToLatLng();
            double cellLatDeg = cellCenter.Latitude / DegreesToRadians;
            double cellLonDeg = cellCenter.Longitude / DegreesToRadians;

            var nearby = spatialIndex.QueryNearby(cellLatDeg, cellLonDeg, 300);
            double risk = ComputeCellRisk(cellLatDeg, cellLonDeg, nearby);
            if (risk > 0.001) // Skip negligible risk cells to save memory
            {
                newCells[cell] = risk;
            }
        }

        _snapshot = new H3GridSnapshot(newCells, true);
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
            double dist = RiskScoringService.EquirectangularDistance(
                lat, lon, hazard.Location.Y, hazard.Location.X);
            double severity = HazardSeverityLookup.GetSeverity(hazard.Type);
            riskSum += severity * Math.Exp(-dist / DecayLambda);
        }

        return Math.Clamp(riskSum, 0.0, 1.0);
    }

    private sealed record H3GridSnapshot(
        IReadOnlyDictionary<ulong, double> Cells,
        bool IsReady)
    {
        public static readonly H3GridSnapshot Empty =
            new(new Dictionary<ulong, double>(), false);
    }
}
