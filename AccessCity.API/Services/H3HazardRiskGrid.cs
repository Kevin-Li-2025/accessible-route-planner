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
/// ulong H3 index → float risk dictionary. Only hexagonal cells near active hazards are populated.
///
/// Memory characteristics:
///   - City scale (100 hazards):    ~5,000 H3 cells
///   - Metropolitan (1,000 hazards): ~50,000 H3 cells
///   - National (10,000 hazards):   ~500,000 H3 cells
///   - Dense 2D array (national):   60,000,000 cells ≈ 480MB ← eliminated
///
/// H3 resolution 9 has ~174m edge length, closely matching the previous 100m grid cell size.
/// Lookups are O(1) dictionary hash lookups — same asymptotic cost as the 2D array index.
/// City-scale snapshots also build a dense accelerator grid so route hot-path lookups avoid
/// per-query H3 coordinate conversion allocations. Sparse H3 remains the fallback for very
/// large geographic extents.
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
    private const double DenseCellSizeDegrees = 0.0009;
    private const int MinDenseAcceleratorHazards = 1_000;
    private const int MaxDenseAcceleratorCells = 2_000_000;

    private static readonly double DegreesToRadians = Math.PI / 180.0;

    private volatile H3GridSnapshot _snapshot = H3GridSnapshot.Empty;

    /// <inheritdoc/>
    public bool IsReady => _snapshot.IsReady;

    /// <inheritdoc/>
    public double GetRisk(double latitude, double longitude)
    {
        var snapshot = _snapshot;
        if (!snapshot.IsReady) return 0.0;

        if (snapshot.DenseGrid is not null)
        {
            var row = (int)((latitude - snapshot.DenseMinLat) / snapshot.DenseCellSize);
            var col = (int)((longitude - snapshot.DenseMinLon) / snapshot.DenseCellSize);
            if (row >= 0 && row < snapshot.DenseRows && col >= 0 && col < snapshot.DenseCols)
            {
                return snapshot.DenseGrid[row, col];
            }
        }

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
                new Dictionary<ulong, float>(),
                null,
                0,
                0,
                0,
                0,
                DenseCellSizeDegrees,
                true);
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
        var newCells = new Dictionary<ulong, float>(cellsToCompute.Count);

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
                newCells[cell] = (float)risk;
            }
        }

        var dense = TryBuildDenseAccelerator(spatialIndex, allHazards);

        _snapshot = new H3GridSnapshot(
            newCells,
            dense.Grid,
            dense.Rows,
            dense.Cols,
            dense.MinLat,
            dense.MinLon,
            dense.CellSize,
            true);
    }

    private static DenseAccelerator TryBuildDenseAccelerator(
        IHazardSpatialIndex spatialIndex,
        IReadOnlyList<HazardReport> allHazards)
    {
        if (allHazards.Count < MinDenseAcceleratorHazards)
        {
            return DenseAccelerator.Empty;
        }

        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (var hazard in allHazards)
        {
            if (hazard.Location is null) continue;
            minLat = Math.Min(minLat, hazard.Location.Y);
            maxLat = Math.Max(maxLat, hazard.Location.Y);
            minLon = Math.Min(minLon, hazard.Location.X);
            maxLon = Math.Max(maxLon, hazard.Location.X);
        }

        if (minLat == double.MaxValue)
        {
            return DenseAccelerator.Empty;
        }

        var bufferDegrees = DenseCellSizeDegrees * 10;
        minLat -= bufferDegrees;
        maxLat += bufferDegrees;
        minLon -= bufferDegrees;
        maxLon += bufferDegrees;

        var rows = Math.Max(1, (int)Math.Ceiling((maxLat - minLat) / DenseCellSizeDegrees));
        var cols = Math.Max(1, (int)Math.Ceiling((maxLon - minLon) / DenseCellSizeDegrees));
        if ((long)rows * cols > MaxDenseAcceleratorCells)
        {
            return DenseAccelerator.Empty;
        }

        var grid = new float[rows, cols];
        for (var row = 0; row < rows; row++)
        {
            var cellLat = minLat + (row + 0.5) * DenseCellSizeDegrees;
            for (var col = 0; col < cols; col++)
            {
                var cellLon = minLon + (col + 0.5) * DenseCellSizeDegrees;
                var risk = ComputeCellRisk(cellLat, cellLon, spatialIndex.QueryNearby(cellLat, cellLon, 300));
                if (risk > 0.001)
                {
                    grid[row, col] = (float)risk;
                }
            }
        }

        return new DenseAccelerator(grid, rows, cols, minLat, minLon, DenseCellSizeDegrees);
    }

    private static double ComputeCellRisk(
        double lat, double lon,
        IReadOnlyList<HazardReport> nearbyHazards)
    {
        if (nearbyHazards.Count == 0) return 0.0;

        double riskSum = 0;
        var contributingHazards = 0;
        foreach (var hazard in nearbyHazards)
        {
            if (hazard.Location is null) continue;
            double dist = RiskScoringService.EquirectangularDistance(
                lat, lon, hazard.Location.Y, hazard.Location.X);
            double severity = HazardSeverityLookup.GetSeverity(hazard.Type);
            riskSum += severity * Math.Exp(-dist / DecayLambda);
            contributingHazards++;
        }

        return RiskScoringService.NormalizeHazardRisk(riskSum, contributingHazards);
    }

    private sealed record H3GridSnapshot(
        IReadOnlyDictionary<ulong, float> Cells,
        float[,]? DenseGrid,
        int DenseRows,
        int DenseCols,
        double DenseMinLat,
        double DenseMinLon,
        double DenseCellSize,
        bool IsReady)
    {
        public static readonly H3GridSnapshot Empty =
            new(new Dictionary<ulong, float>(), null, 0, 0, 0, 0, DenseCellSizeDegrees, false);
    }

    private sealed record DenseAccelerator(
        float[,]? Grid,
        int Rows,
        int Cols,
        double MinLat,
        double MinLon,
        double CellSize)
    {
        public static readonly DenseAccelerator Empty = new(null, 0, 0, 0, 0, DenseCellSizeDegrees);
    }
}
