using System.Diagnostics;
var queries = GetInt(args, "--queries", 1_000_000);
var batchSize = Math.Max(1, GetInt(args, "--batch-size", 256));
var gridCells = Math.Max(1024, GetInt(args, "--grid-cells", 262_144));
var random = new Random(42);
var points = new (double Lat, double Lon)[queries];
for (var i = 0; i < points.Length; i++)
{
    points[i] = (
        52.38 + random.NextDouble() * 0.22,
        -2.02 + random.NextDouble() * 0.30);
}

var grid = new double[gridCells];
for (var i = 0; i < grid.Length; i++)
{
    grid[i] = (i * 1103515245u + 12345u) % 1000 / 1000.0;
}

var warmup = 0.0;
for (var i = 0; i < Math.Min(queries, 10_000); i++)
{
    var a = points[i];
    var b = points[(i + 97) % points.Length];
    warmup += EquirectangularDistance(a.Lat, a.Lon, b.Lat, b.Lon);
}

var samples = new double[(int)Math.Ceiling(queries / (double)batchSize)];
var gridSamples = new double[(int)Math.Ceiling(queries / (double)batchSize)];
var checksum = 0.0;
var total = Stopwatch.StartNew();
var sampleIndex = 0;
for (var batchStart = 0; batchStart < queries; batchStart += batchSize)
{
    var batchEnd = Math.Min(queries, batchStart + batchSize);
    var started = Stopwatch.GetTimestamp();
    for (var i = batchStart; i < batchEnd; i++)
    {
        var a = points[i];
        var b = points[(i + 97) % points.Length];
        checksum += EquirectangularDistance(a.Lat, a.Lon, b.Lat, b.Lon);
    }

    samples[sampleIndex++] = (Stopwatch.GetTimestamp() - started) * 1_000_000.0
                             / Stopwatch.Frequency
                             / Math.Max(1, batchEnd - batchStart);
}
total.Stop();

var gridChecksum = 0.0;
var gridTotal = Stopwatch.StartNew();
sampleIndex = 0;
for (var batchStart = 0; batchStart < queries; batchStart += batchSize)
{
    var batchEnd = Math.Min(queries, batchStart + batchSize);
    var started = Stopwatch.GetTimestamp();
    for (var i = batchStart; i < batchEnd; i++)
    {
        var p = points[i];
        gridChecksum += DenseGridLookup(grid, p.Lat, p.Lon);
    }

    gridSamples[sampleIndex++] = (Stopwatch.GetTimestamp() - started) * 1_000_000.0
                                 / Stopwatch.Frequency
                                 / Math.Max(1, batchEnd - batchStart);
}
gridTotal.Stop();
Array.Sort(samples);
Array.Sort(gridSamples);

Console.WriteLine($$"""
{
  "kernel": "dotnet-nativeaot-equirectangular-distance",
  "queries": {{queries}},
  "batchSize": {{batchSize}},
  "gridCells": {{gridCells}},
  "throughputOpsPerSecond": {{Math.Round(queries / total.Elapsed.TotalSeconds, 2)}},
  "p50Microseconds": {{Percentile(samples, 0.50)}},
  "p95Microseconds": {{Percentile(samples, 0.95)}},
  "p99Microseconds": {{Percentile(samples, 0.99)}},
  "denseGridLookup": {
    "throughputOpsPerSecond": {{Math.Round(queries / gridTotal.Elapsed.TotalSeconds, 2)}},
    "p50Microseconds": {{Percentile(gridSamples, 0.50)}},
    "p95Microseconds": {{Percentile(gridSamples, 0.95)}},
    "p99Microseconds": {{Percentile(gridSamples, 0.99)}},
    "checksum": {{gridChecksum}}
  },
  "checksum": {{checksum}},
  "warmup": {{warmup}}
}
""");

static int GetInt(string[] args, string name, int fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[i + 1], out var value))
        {
            return value;
        }
    }

    return fallback;
}

static double Percentile(double[] sortedValues, double percentile)
{
    if (sortedValues.Length == 0) return 0;
    var index = Math.Clamp((int)Math.Ceiling(sortedValues.Length * percentile) - 1, 0, sortedValues.Length - 1);
    return Math.Round(sortedValues[index], 4);
}

static double EquirectangularDistance(double lat1, double lon1, double lat2, double lon2)
{
    const double earthRadiusMetres = 6_371_000.0;
    const double degToRad = Math.PI / 180.0;
    var lat1Rad = lat1 * degToRad;
    var lat2Rad = lat2 * degToRad;
    var x = (lon2 - lon1) * degToRad * Math.Cos((lat1Rad + lat2Rad) * 0.5);
    var y = (lat2 - lat1) * degToRad;
    return Math.Sqrt(x * x + y * y) * earthRadiusMetres;
}

static double DenseGridLookup(double[] grid, double lat, double lon)
{
    const double minLat = 52.38;
    const double minLon = -2.02;
    const double invCell = 1.0 / 0.0009;
    var x = (int)((lon - minLon) * invCell);
    var y = (int)((lat - minLat) * invCell);
    var index = (uint)(x * 73856093 ^ y * 19349663) % (uint)grid.Length;
    return grid[index];
}
