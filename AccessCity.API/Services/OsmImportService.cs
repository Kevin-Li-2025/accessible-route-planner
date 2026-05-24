using System.Text.Json;
using System.Globalization;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Npgsql;
using NpgsqlTypes;
using OsmSharp;
using OsmSharp.Complete;
using OsmSharp.Streams;
using OsmSharp.Streams.Complete;
using OsmSharp.Tags;

namespace AccessCity.API.Services;

public interface IOsmImportService
{
    Task<OsmImportResult> ImportConfiguredAsync(CancellationToken cancellationToken = default);
    Task<OsmImportResult> ImportAsync(string filePathConfig, CancellationToken cancellationToken = default);
}

public sealed class OsmImportService : IOsmImportService
{
    private static readonly HashSet<string> WalkableHighways = new(StringComparer.OrdinalIgnoreCase)
    {
        "footway", "path", "pedestrian", "living_street", "service", "residential",
        "unclassified", "tertiary", "secondary", "primary", "track", "steps", "crossing"
    };

    private static readonly HashSet<string> ExcludedHighways = new(StringComparer.OrdinalIgnoreCase)
    {
        "motorway", "motorway_link", "trunk", "trunk_link", "proposed", "construction"
    };

    private readonly AppDbContext _dbContext;
    private readonly IOptions<OsmImportOptions> _options;
    private readonly IRouteGraphStatusService _routeGraphStatus;
    private readonly ILogger<OsmImportService> _logger;
    private readonly GeometryFactory _geometryFactory = new(new PrecisionModel(), 4326);

    public OsmImportService(
        AppDbContext dbContext,
        IOptions<OsmImportOptions> options,
        IRouteGraphStatusService routeGraphStatus,
        ILogger<OsmImportService> logger)
    {
        _dbContext = dbContext;
        _options = options;
        _routeGraphStatus = routeGraphStatus;
        _logger = logger;
    }

    public async Task<OsmImportResult> ImportConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var filePathConfig = _options.Value.FilePath;
        if (string.IsNullOrWhiteSpace(filePathConfig))
        {
            throw new InvalidOperationException("OsmImport:FilePath is not configured.");
        }

        return await ImportAsync(filePathConfig, cancellationToken);
    }

    public async Task<OsmImportResult> ImportAsync(string filePathConfig, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePathConfig))
        {
            throw new InvalidOperationException("OsmImport:FilePath is not configured.");
        }

        var configuredFilePaths = filePathConfig.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filePaths = ResolveExistingFiles(configuredFilePaths);
        if (filePaths.Count == 0)
        {
            throw new FileNotFoundException(
                $"No OSM import files were found. Configured paths: {string.Join(';', configuredFilePaths)}");
        }

        var combinedCounters = new ImportCounters();
        var startedAt = DateTime.UtcNow;

        var executionStrategy = _dbContext.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            if (_options.Value.ReplaceExisting)
            {
                await _dbContext.RouteEdges.ExecuteDeleteAsync(cancellationToken);
                await _dbContext.RouteNodes.ExecuteDeleteAsync(cancellationToken);
                await _dbContext.InfrastructureAssets
                    .Where(asset => asset.SourceSystem == "osm")
                    .ExecuteDeleteAsync(cancellationToken);
            }

            var globalSeenNodes = new HashSet<long>();
            foreach (var filePath in filePaths)
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var fileCounters = await ImportFileAsync(_dbContext, filePath, globalSeenNodes, cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                // Help GC recover memory between large files
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                combinedCounters.RecordsSeen += fileCounters.RecordsSeen;
                combinedCounters.RouteNodesInserted += fileCounters.RouteNodesInserted;
                combinedCounters.RouteEdgesInserted += fileCounters.RouteEdgesInserted;
                combinedCounters.InfrastructureAssetsInserted += fileCounters.InfrastructureAssetsInserted;
            }

            var finishedAt = DateTime.UtcNow;
            var importedRouteGraph = combinedCounters.RouteNodesInserted > 0 && combinedCounters.RouteEdgesInserted > 0;
            var run = new FeedIngestionRun
            {
                SourceType = "osm",
                SourceName = string.Join(';', filePaths),
                Status = importedRouteGraph ? "completed" : "failed",
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                RecordsSeen = combinedCounters.RecordsSeen,
                RecordsInserted = combinedCounters.RouteNodesInserted + combinedCounters.RouteEdgesInserted + combinedCounters.InfrastructureAssetsInserted,
                ErrorSummary = importedRouteGraph
                    ? null
                    : "OSM import completed without route graph coverage. At least one route node and route edge is required.",
                Metadata = JsonSerializer.SerializeToDocument(new
                {
                    filePaths = filePaths,
                    routeNodesInserted = combinedCounters.RouteNodesInserted,
                    routeEdgesInserted = combinedCounters.RouteEdgesInserted,
                    infrastructureAssetsInserted = combinedCounters.InfrastructureAssetsInserted
                })
            };

            _dbContext.FeedIngestionRuns.Add(run);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _routeGraphStatus.InvalidateLocalCache();

            if (!importedRouteGraph)
            {
                throw new InvalidOperationException(run.ErrorSummary);
            }

            return new OsmImportResult
            {
                RunId = run.Id,
                SourceName = run.SourceName,
                Status = run.Status,
                RecordsSeen = combinedCounters.RecordsSeen,
                RouteNodesInserted = combinedCounters.RouteNodesInserted,
                RouteEdgesInserted = combinedCounters.RouteEdgesInserted,
                InfrastructureAssetsInserted = combinedCounters.InfrastructureAssetsInserted,
                Duration = finishedAt - startedAt
            };
        });
    }

    private List<string> ResolveExistingFiles(IEnumerable<string> configuredFilePaths)
    {
        var existing = new List<string>();
        foreach (var filePath in configuredFilePaths)
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("OSM import file not found: {FullPath}", fullPath);
                continue;
            }

            existing.Add(fullPath);
        }

        return existing;
    }

    private async Task<ImportCounters> ImportFileAsync(
        AppDbContext dbContext,
        string filePath,
        HashSet<long> seenRouteNodeIds,
        CancellationToken cancellationToken)
    {
        var nodeCache = new Dictionary<long, (double Lon, double Lat)>();
        var seenAssetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchSize = GetFlushBatchSize(dbContext);
        var pendingRouteNodes = new List<RouteNode>(Math.Min(batchSize, 4096));
        var pendingRouteEdges = new List<RouteEdge>(Math.Min(batchSize, 4096));
        var pendingAssets = new List<InfrastructureAsset>(Math.Min(batchSize, 4096));
        var counters = new ImportCounters();
        var previousAutoDetectChanges = dbContext.ChangeTracker.AutoDetectChangesEnabled;
        dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            _logger.LogInformation("Importing file (Pass 1 - Nodes): {FilePath}", filePath);

            // Pass 1: Nodes
            using (var stream = File.OpenRead(filePath))
            {
                var source = CreateSource(stream, filePath);
                foreach (var osmGeo in source)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    counters.RecordsSeen++;

                    if (osmGeo is Node node && node.Id.HasValue && node.Longitude.HasValue && node.Latitude.HasValue)
                    {
                        nodeCache[node.Id.Value] = (node.Longitude.Value, node.Latitude.Value);

                        if (TryCreatePointInfrastructureAsset(node, out var pointAsset) &&
                            pointAsset is not null &&
                            seenAssetKeys.Add(pointAsset.SourceRecordId!))
                        {
                            pendingAssets.Add(pointAsset);
                            counters.InfrastructureAssetsInserted++;
                        }
                    }

                    if (pendingAssets.Count >= batchSize)
                    {
                        await FlushAsync(dbContext, pendingRouteNodes, pendingRouteEdges, pendingAssets, cancellationToken);
                    }
                }
                await FlushAsync(dbContext, pendingRouteNodes, pendingRouteEdges, pendingAssets, cancellationToken);
            }

            _logger.LogInformation("Importing file (Pass 2 - Ways): {FilePath}", filePath);

            // Pass 2: Ways
            using (var stream = File.OpenRead(filePath))
            {
                var source = CreateSource(stream, filePath);
                foreach (var osmGeo in source)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (osmGeo is Way way)
                    {
                        if (IsWalkable(way))
                        {
                            AddWalkableWayManual(way, nodeCache, seenRouteNodeIds, pendingRouteNodes, pendingRouteEdges, counters);
                        }

                        if (TryCreateWayInfrastructureAssetManual(way, nodeCache, out var wayAsset) &&
                            wayAsset is not null &&
                            seenAssetKeys.Add(wayAsset.SourceRecordId!))
                        {
                            pendingAssets.Add(wayAsset);
                            counters.InfrastructureAssetsInserted++;
                        }
                    }

                    if (pendingRouteNodes.Count >= batchSize ||
                        pendingRouteEdges.Count >= batchSize ||
                        pendingAssets.Count >= batchSize)
                    {
                        await FlushAsync(dbContext, pendingRouteNodes, pendingRouteEdges, pendingAssets, cancellationToken);
                    }
                }
                await FlushAsync(dbContext, pendingRouteNodes, pendingRouteEdges, pendingAssets, cancellationToken);
            }
        }
        finally
        {
            dbContext.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetectChanges;
        }

        nodeCache.Clear();
        return counters;
    }

    private void AddWalkableWayManual(
        Way way,
        Dictionary<long, (double Lon, double Lat)> nodeCache,
        HashSet<long> seenRouteNodeIds,
        List<RouteNode> pendingRouteNodes,
        List<RouteEdge> pendingRouteEdges,
        ImportCounters counters)
    {
        if (way.Nodes == null || way.Nodes.Length < 2) return;

        var validNodes = new List<(long Id, double Lon, double Lat)>();
        foreach (var nodeId in way.Nodes)
        {
            if (nodeCache.TryGetValue(nodeId, out var coords))
            {
                validNodes.Add((nodeId, coords.Lon, coords.Lat));
            }
        }

        if (validNodes.Count < 2) return;

        foreach (var node in validNodes)
        {
            if (seenRouteNodeIds.Add(node.Id))
            {
                pendingRouteNodes.Add(new RouteNode
                {
                    Id = node.Id,
                    Location = CreatePoint(node.Lon, node.Lat)
                });
                counters.RouteNodesInserted++;
            }
        }

        for (var i = 0; i < validNodes.Count - 1; i++)
        {
            var from = validNodes[i];
            var to = validNodes[i + 1];

            var fromPoint = CreatePoint(from.Lon, from.Lat);
            var toPoint = CreatePoint(to.Lon, to.Lat);

            var edge = CreateRouteEdgeManual(way, from.Id, to.Id, fromPoint, toPoint);
            pendingRouteEdges.Add(edge);
            counters.RouteEdgesInserted++;

            if (IsBidirectionalForWalking(way))
            {
                pendingRouteEdges.Add(CreateRouteEdgeManual(way, to.Id, from.Id, toPoint, fromPoint));
                counters.RouteEdgesInserted++;
            }
        }
    }

    private RouteEdge CreateRouteEdgeManual(Way way, long fromId, long toId, Point fromPoint, Point toPoint)
    {
        var tags = ToDictionary(way.Tags);
        var surface = GetFirstTag(tags,
            "surface",
            "sidewalk:surface",
            "sidewalk:left:surface",
            "sidewalk:right:surface",
            "left:surface",
            "right:surface") ?? "unknown";
        var smoothness = GetFirstTag(tags,
            "smoothness",
            "sidewalk:smoothness",
            "sidewalk:left:smoothness",
            "sidewalk:right:smoothness",
            "left:smoothness",
            "right:smoothness");
        var lit = tags.GetValueOrDefault("lit");
        var highway = tags.GetValueOrDefault("highway");
        var incline = tags.GetValueOrDefault("incline");
        var barrier = tags.GetValueOrDefault("barrier");
        var width = ParseMetres(GetFirstTag(tags,
            "width",
            "sidewalk:width",
            "sidewalk:left:width",
            "sidewalk:right:width",
            "left:width",
            "right:width"));
        var kerbHeight = ParseKerbHeight(tags);
        var wheelchair = tags.GetValueOrDefault("wheelchair");
        var access = BuildAccessDescriptor(tags);
        var distanceMetres = RiskScoringService.HaversineDistance(fromPoint.Y, fromPoint.X, toPoint.Y, toPoint.X);
        var hasStairs = string.Equals(highway, "steps", StringComparison.OrdinalIgnoreCase);
        var hasBarrier = IsBlockingBarrier(tags, kerbHeight);
        var isSteep = IsSteep(incline);
        var costProfile = RouteEdgeCostModel.Compute(
            distanceMetres,
            surface,
            smoothness,
            hasStairs,
            hasBarrier,
            kerbHeight,
            width,
            isSteep,
            access);

        var line = _geometryFactory.CreateLineString(new[]
        {
            fromPoint.Coordinate,
            toPoint.Coordinate
        });

        return new RouteEdge
        {
            FromNodeId = fromId,
            ToNodeId = toId,
            SourceWayId = way.Id,
            Geometry = line,
            DistanceMetres = distanceMetres,
            BaseSafetyCost = ComputeBaseSafetyCost(surface, smoothness, lit, highway, incline, barrier, kerbHeight, width, wheelchair),
            SurfaceType = surface,
            HasStairs = hasStairs,
            HasCrossing = HasCrossing(tags),
            LightingQuality = lit?.ToLowerInvariant() switch
            {
                "yes" => 0.95,
                "limited" => 0.55,
                "no" => 0.1,
                _ => 0.45
            },
            IsSteep = isSteep,
            IsUnderConstruction = IsUnderConstruction(tags),
            KerbHeight = kerbHeight,
            Smoothness = smoothness,
            WidthMetres = width,
            HasTactilePaving = IsYes(GetFirstTag(tags, "tactile_paving", "sidewalk:tactile_paving")),
            HasBarrier = hasBarrier,
            Access = access,
            AccessibilityCostVersion = costProfile.Version,
            StandardAccessibilityPenaltySeconds = costProfile.StandardAccessibilityPenaltySeconds,
            WheelchairAccessibilityPenaltySeconds = costProfile.WheelchairAccessibilityPenaltySeconds,
            StrollerAccessibilityPenaltySeconds = costProfile.StrollerAccessibilityPenaltySeconds,
            AccessibilityDataQuality = costProfile.AccessibilityDataQuality,
            Tags = ToJsonDocument(tags)
        };
    }

    private bool TryCreateWayInfrastructureAssetManual(Way way, Dictionary<long, (double Lon, double Lat)> nodeCache, out InfrastructureAsset? asset)
    {
        asset = null;
        var tags = ToDictionary(way.Tags);
        var assetType = GetInfrastructureAssetType(tags);
        if (assetType is null || way.Nodes == null) return false;

        var points = new List<Coordinate>();
        foreach (var nodeId in way.Nodes)
        {
            if (nodeCache.TryGetValue(nodeId, out var coords))
            {
                points.Add(new Coordinate(coords.Lon, coords.Lat));
            }
        }

        if (points.Count == 0) return false;

        Geometry geometry = points.Count == 1
            ? _geometryFactory.CreatePoint(points[0])
            : _geometryFactory.CreateLineString(points.ToArray());

        var sourceRecordId = $"way:{way.Id}";
        var lastObservedAt = NormalizeOsmTimestamp(way.TimeStamp);
        asset = new InfrastructureAsset
        {
            AssetType = assetType,
            Name = tags.GetValueOrDefault("name"),
            Geometry = geometry,
            SourceSystem = "osm",
            SourceRecordId = sourceRecordId,
            LastObservedAt = lastObservedAt,
            AccessibilityInfo = ToJsonDocument(tags),
            AccessibilityProfile = BuildAccessibilityProfileDocument(tags, assetType, sourceRecordId, lastObservedAt)
        };

        return true;
    }

    private static OsmStreamSource CreateSource(Stream stream, string filePath)
    {
        if (filePath.EndsWith(".pbf", StringComparison.OrdinalIgnoreCase))
        {
            return new PBFOsmStreamSource(stream);
        }

        return new XmlOsmStreamSource(stream);
    }



    private bool TryCreatePointInfrastructureAsset(Node node, out InfrastructureAsset? asset)
    {
        asset = null;
        if (node.Id is null || node.Latitude is null || node.Longitude is null)
        {
            return false;
        }

        var tags = ToDictionary(node.Tags);
        var assetType = GetInfrastructureAssetType(tags);
        if (assetType is null)
        {
            return false;
        }

        var sourceRecordId = $"node:{node.Id.Value}";
        var lastObservedAt = NormalizeOsmTimestamp(node.TimeStamp);
        asset = new InfrastructureAsset
        {
            AssetType = assetType,
            Name = tags.GetValueOrDefault("name"),
            Geometry = CreatePoint(node.Longitude.Value, node.Latitude.Value),
            SourceSystem = "osm",
            SourceRecordId = sourceRecordId,
            LastObservedAt = lastObservedAt,
            AccessibilityInfo = ToJsonDocument(tags),
            AccessibilityProfile = BuildAccessibilityProfileDocument(tags, assetType, sourceRecordId, lastObservedAt)
        };

        return true;
    }

    private static DateTime? NormalizeOsmTimestamp(DateTime? timestamp)
    {
        if (timestamp is null)
        {
            return null;
        }

        return timestamp.Value.Kind switch
        {
            DateTimeKind.Utc => timestamp.Value,
            DateTimeKind.Local => timestamp.Value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp.Value, DateTimeKind.Utc)
        };
    }

    private static bool IsWalkable(Way way)
    {
        var tags = ToDictionary(way.Tags);
        var highway = tags.GetValueOrDefault("highway");
        if (string.IsNullOrWhiteSpace(highway) || ExcludedHighways.Contains(highway))
        {
            return false;
        }

        if (tags.TryGetValue("foot", out var foot) &&
            string.Equals(foot, "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (tags.TryGetValue("access", out var access) &&
            (string.Equals(access, "no", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(access, "private", StringComparison.OrdinalIgnoreCase)) &&
            !string.Equals(tags.GetValueOrDefault("foot"), "yes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var barrier = tags.GetValueOrDefault("barrier");
        if (!string.IsNullOrWhiteSpace(barrier) &&
            (string.Equals(barrier, "wall", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(barrier, "fence", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return WalkableHighways.Contains(highway)
            || string.Equals(tags.GetValueOrDefault("foot"), "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tags.GetValueOrDefault("foot"), "designated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBidirectionalForWalking(Way way)
    {
        var tags = ToDictionary(way.Tags);
        if (string.Equals(tags.GetValueOrDefault("oneway:foot"), "yes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(tags.GetValueOrDefault("foot:backward"), "no", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string? GetInfrastructureAssetType(IReadOnlyDictionary<string, string> tags)
    {
        if (tags.TryGetValue("amenity", out var amenity))
        {
            return $"amenity:{amenity}";
        }

        if (tags.TryGetValue("public_transport", out var publicTransport))
        {
            return $"public_transport:{publicTransport}";
        }

        if (tags.TryGetValue("railway", out var railway))
        {
            return $"railway:{railway}";
        }

        if (tags.TryGetValue("highway", out var highway) &&
            (string.Equals(highway, "elevator", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(highway, "crossing", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(highway, "bus_stop", StringComparison.OrdinalIgnoreCase)))
        {
            return $"highway:{highway}";
        }

        if (tags.TryGetValue("barrier", out var barrier))
        {
            return $"barrier:{barrier}";
        }

        return null;
    }

    private static string? GetFirstTag(IReadOnlyDictionary<string, string> tags, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? BuildAccessDescriptor(IReadOnlyDictionary<string, string> tags)
    {
        var parts = new List<string>();
        AddAccessPart(parts, tags, "access");
        AddAccessPart(parts, tags, "foot");
        AddAccessPart(parts, tags, "wheelchair");
        return parts.Count == 0 ? null : string.Join(";", parts);
    }

    private static void AddAccessPart(List<string> parts, IReadOnlyDictionary<string, string> tags, string key)
    {
        if (tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{key}={value.Trim().ToLowerInvariant()}");
        }
    }

    private static double? ParseMetres(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().ToLowerInvariant()
            .Replace("meters", "", StringComparison.Ordinal)
            .Replace("metres", "", StringComparison.Ordinal)
            .Replace("meter", "", StringComparison.Ordinal)
            .Replace("metre", "", StringComparison.Ordinal)
            .Replace("m", "", StringComparison.Ordinal)
            .Trim();

        if (normalized.Contains(';', StringComparison.Ordinal))
        {
            normalized = normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        }

        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var metres) && metres >= 0)
        {
            return metres;
        }

        return null;
    }

    private static double ParseKerbHeight(IReadOnlyDictionary<string, string> tags)
    {
        var explicitHeight = ParseMetres(GetFirstTag(
            tags,
            "kerb:height",
            "sidewalk:kerb:height",
            "sidewalk:left:kerb:height",
            "sidewalk:right:kerb:height",
            "sloped_curb:height"));
        if (explicitHeight.HasValue)
        {
            return explicitHeight.Value;
        }

        var kerb = GetFirstTag(tags, "kerb", "sidewalk:kerb", "sidewalk:left:kerb", "sidewalk:right:kerb");
        if (!string.IsNullOrWhiteSpace(kerb))
        {
            return kerb.ToLowerInvariant() switch
            {
                "flush" or "lowered" or "no" => 0.0,
                "rolled" => 0.03,
                "raised" => 0.10,
                _ => 0.05
            };
        }

        var slopedCurb = GetFirstTag(tags, "sloped_curb", "sidewalk:sloped_curb");
        if (!string.IsNullOrWhiteSpace(slopedCurb))
        {
            return IsYes(slopedCurb) ? 0.02 : 0.10;
        }

        return string.Equals(tags.GetValueOrDefault("barrier"), "kerb", StringComparison.OrdinalIgnoreCase)
            ? 0.10
            : 0.0;
    }

    private static bool IsBlockingBarrier(IReadOnlyDictionary<string, string> tags, double kerbHeight)
    {
        var barrier = tags.GetValueOrDefault("barrier");
        if (string.IsNullOrWhiteSpace(barrier))
        {
            return false;
        }

        if (string.Equals(barrier, "kerb", StringComparison.OrdinalIgnoreCase))
        {
            return kerbHeight > 0.05;
        }

        return barrier.ToLowerInvariant() switch
        {
            "wall" or "fence" or "gate" or "stile" or "turnstile" or "cycle_barrier" or "block" or "chain" => true,
            _ => false
        };
    }

    private static bool HasCrossing(IReadOnlyDictionary<string, string> tags) =>
        string.Equals(tags.GetValueOrDefault("highway"), "crossing", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tags.GetValueOrDefault("footway"), "crossing", StringComparison.OrdinalIgnoreCase)
        || tags.ContainsKey("crossing");

    private static bool IsUnderConstruction(IReadOnlyDictionary<string, string> tags) =>
        string.Equals(tags.GetValueOrDefault("highway"), "construction", StringComparison.OrdinalIgnoreCase)
        || tags.ContainsKey("construction")
        || string.Equals(tags.GetValueOrDefault("access"), "no", StringComparison.OrdinalIgnoreCase);

    private static bool IsYes(string? value) =>
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);

    private static double ComputeBaseSafetyCost(
        string surface,
        string? smoothness,
        string? lit,
        string? highway,
        string? incline,
        string? barrier,
        double kerbHeight,
        double? widthMetres,
        string? wheelchair)
    {
        var score = surface.ToLowerInvariant() switch
        {
            "asphalt" => 0.08,
            "paved" => 0.1,
            "paving_stones" => 0.14,
            "concrete" => 0.1,
            "unknown" => 0.22,
            "cobblestone" => 0.35,
            "sett" => 0.35,
            "gravel" => 0.4,
            "unpaved" => 0.45,
            "sand" or "dirt" or "earth" or "grass" => 0.5,
            _ => 0.2
        };

        if (string.IsNullOrWhiteSpace(smoothness))
        {
            score += 0.03;
        }

        score += smoothness?.ToLowerInvariant() switch
        {
            "excellent" or "good" => -0.02,
            "intermediate" => 0.02,
            "bad" => 0.15,
            "very_bad" => 0.25,
            "horrible" or "very_horrible" or "impassable" => 0.35,
            _ => 0
        };

        if (string.Equals(lit, "no", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.12;
        }

        if (string.Equals(highway, "steps", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.25;
        }

        if (IsSteep(incline))
        {
            score += 0.15;
        }

        if (!string.IsNullOrWhiteSpace(barrier))
        {
            score += 0.2;
        }

        if (kerbHeight > 0.03)
        {
            score += Math.Min(kerbHeight * 4.0, 0.25);
        }

        if (widthMetres.HasValue && widthMetres < 0.9)
        {
            score += 0.25;
        }
        else if (!widthMetres.HasValue && IsPedestrianInfrastructure(highway))
        {
            score += 0.06;
        }

        if (string.Equals(wheelchair, "no", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.6;
        }
        else if (string.Equals(wheelchair, "limited", StringComparison.OrdinalIgnoreCase))
        {
            score += 0.2;
        }

        return Math.Clamp(score, 0.01, 0.95);
    }

    private static bool IsPedestrianInfrastructure(string? highway) =>
        !string.IsNullOrWhiteSpace(highway) && WalkableHighways.Contains(highway);

    private static bool IsSteep(string? incline)
    {
        if (string.IsNullOrWhiteSpace(incline))
        {
            return false;
        }

        var trimmed = incline.Trim().TrimEnd('%');
        if (double.TryParse(trimmed, out var percentage))
        {
            return Math.Abs(percentage) >= 8.0;
        }

        return string.Equals(trimmed, "up", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "down", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "steep", StringComparison.OrdinalIgnoreCase);
    }

    private async Task FlushAsync(
        AppDbContext dbContext,
        List<RouteNode> pendingRouteNodes,
        List<RouteEdge> pendingRouteEdges,
        List<InfrastructureAsset> pendingAssets,
        CancellationToken cancellationToken)
    {
        if (pendingRouteNodes.Count == 0 && pendingRouteEdges.Count == 0 && pendingAssets.Count == 0)
        {
            return;
        }

        if (TryGetPostgresConnection(dbContext, out var connection))
        {
            await FlushWithPostgresCopyAsync(
                connection,
                pendingRouteNodes,
                pendingRouteEdges,
                pendingAssets,
                cancellationToken);
            dbContext.ChangeTracker.Clear();
            pendingRouteNodes.Clear();
            pendingRouteEdges.Clear();
            pendingAssets.Clear();
            return;
        }

        if (pendingRouteNodes.Count > 0)
        {
            dbContext.RouteNodes.AddRange(pendingRouteNodes);
        }

        if (pendingRouteEdges.Count > 0)
        {
            dbContext.RouteEdges.AddRange(pendingRouteEdges);
        }

        if (pendingAssets.Count > 0)
        {
            dbContext.InfrastructureAssets.AddRange(pendingAssets);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        pendingRouteNodes.Clear();
        pendingRouteEdges.Clear();
        pendingAssets.Clear();
    }

    private int GetFlushBatchSize(AppDbContext dbContext)
    {
        if (!CanUsePostgresCopy(dbContext))
        {
            return 500;
        }

        return Math.Clamp(_options.Value.BulkCopyBatchSize, 1_000, 100_000);
    }

    private bool TryGetPostgresConnection(AppDbContext dbContext, out NpgsqlConnection connection)
    {
        if (CanUsePostgresCopy(dbContext) &&
            dbContext.Database.GetDbConnection() is NpgsqlConnection npgsqlConnection)
        {
            connection = npgsqlConnection;
            return true;
        }

        connection = null!;
        return false;
    }

    private bool CanUsePostgresCopy(AppDbContext dbContext) =>
        _options.Value.UsePostgresCopy &&
        string.Equals(dbContext.Database.ProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal);

    private static async Task FlushWithPostgresCopyAsync(
        NpgsqlConnection connection,
        IReadOnlyCollection<RouteNode> pendingRouteNodes,
        IReadOnlyCollection<RouteEdge> pendingRouteEdges,
        IReadOnlyCollection<InfrastructureAsset> pendingAssets,
        CancellationToken cancellationToken)
    {
        var openedConnection = connection.State != System.Data.ConnectionState.Open;
        if (openedConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            if (pendingRouteNodes.Count > 0)
            {
                await CopyRouteNodesAsync(connection, pendingRouteNodes, cancellationToken);
            }

            if (pendingRouteEdges.Count > 0)
            {
                await CopyRouteEdgesAsync(connection, pendingRouteEdges, cancellationToken);
            }

            if (pendingAssets.Count > 0)
            {
                await CopyInfrastructureAssetsAsync(connection, pendingAssets, cancellationToken);
            }
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task CopyRouteNodesAsync(
        NpgsqlConnection connection,
        IEnumerable<RouteNode> nodes,
        CancellationToken cancellationToken)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            """
            COPY public.route_nodes ("Id", "Location", "Tags")
            FROM STDIN (FORMAT BINARY)
            """,
            cancellationToken);

        foreach (var node in nodes)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(node.Id, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(node.Location, NpgsqlDbType.Geometry, cancellationToken);
            await writer.WriteAsync(ToJson(node.Tags), NpgsqlDbType.Jsonb, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static async Task CopyRouteEdgesAsync(
        NpgsqlConnection connection,
        IEnumerable<RouteEdge> edges,
        CancellationToken cancellationToken)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            """
            COPY public.route_edges (
                "FromNodeId",
                "ToNodeId",
                "SourceWayId",
                "Geometry",
                "DistanceMetres",
                "BaseSafetyCost",
                "SurfaceType",
                "HasStairs",
                "HasCrossing",
                "IsUnderConstruction",
                "LightingQuality",
                "IsSteep",
                kerb_height,
                smoothness,
                width_metres,
                has_tactile_paving,
                "HasBarrier",
                "Access",
                accessibility_cost_version,
                standard_accessibility_penalty_seconds,
                wheelchair_accessibility_penalty_seconds,
                stroller_accessibility_penalty_seconds,
                accessibility_data_quality,
                "Tags")
            FROM STDIN (FORMAT BINARY)
            """,
            cancellationToken);

        foreach (var edge in edges)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(edge.FromNodeId, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(edge.ToNodeId, NpgsqlDbType.Bigint, cancellationToken);
            if (edge.SourceWayId.HasValue)
            {
                await writer.WriteAsync(edge.SourceWayId.Value, NpgsqlDbType.Bigint, cancellationToken);
            }
            else
            {
                await writer.WriteNullAsync(cancellationToken);
            }

            await writer.WriteAsync(edge.Geometry, NpgsqlDbType.Geometry, cancellationToken);
            await writer.WriteAsync(edge.DistanceMetres, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(edge.BaseSafetyCost, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(edge.SurfaceType, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(edge.HasStairs, NpgsqlDbType.Boolean, cancellationToken);
            await writer.WriteAsync(edge.HasCrossing, NpgsqlDbType.Boolean, cancellationToken);
            await writer.WriteAsync(edge.IsUnderConstruction, NpgsqlDbType.Boolean, cancellationToken);
            await writer.WriteAsync(edge.LightingQuality, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(edge.IsSteep, NpgsqlDbType.Boolean, cancellationToken);
            await writer.WriteAsync(edge.KerbHeight, NpgsqlDbType.Double, cancellationToken);
            await WriteNullableStringAsync(writer, edge.Smoothness, NpgsqlDbType.Varchar, cancellationToken);
            await WriteNullableDoubleAsync(writer, edge.WidthMetres, cancellationToken);
            await writer.WriteAsync(edge.HasTactilePaving, NpgsqlDbType.Boolean, cancellationToken);
            await writer.WriteAsync(edge.HasBarrier, NpgsqlDbType.Boolean, cancellationToken);
            await WriteNullableStringAsync(writer, edge.Access, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(edge.AccessibilityCostVersion, NpgsqlDbType.Integer, cancellationToken);
            await writer.WriteAsync(edge.StandardAccessibilityPenaltySeconds, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(edge.WheelchairAccessibilityPenaltySeconds, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(edge.StrollerAccessibilityPenaltySeconds, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(edge.AccessibilityDataQuality, NpgsqlDbType.Double, cancellationToken);
            await writer.WriteAsync(ToJson(edge.Tags), NpgsqlDbType.Jsonb, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static async Task CopyInfrastructureAssetsAsync(
        NpgsqlConnection connection,
        IEnumerable<InfrastructureAsset> assets,
        CancellationToken cancellationToken)
    {
        await using var writer = await connection.BeginBinaryImportAsync(
            """
            COPY public.infrastructure_assets (
                "AssetType",
                "Name",
                "Geometry",
                "Status",
                "AccessibilityInfo",
                "AccessibilityProfile",
                "SourceSystem",
                "SourceRecordId",
                "LastObservedAt",
                "CreatedAt",
                "UpdatedAt")
            FROM STDIN (FORMAT BINARY)
            """,
            cancellationToken);

        foreach (var asset in assets)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(asset.AssetType, NpgsqlDbType.Varchar, cancellationToken);
            await WriteNullableStringAsync(writer, asset.Name, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(asset.Geometry, NpgsqlDbType.Geometry, cancellationToken);
            await writer.WriteAsync(asset.Status, NpgsqlDbType.Varchar, cancellationToken);
            await writer.WriteAsync(ToJson(asset.AccessibilityInfo), NpgsqlDbType.Jsonb, cancellationToken);
            await writer.WriteAsync(ToJson(asset.AccessibilityProfile), NpgsqlDbType.Jsonb, cancellationToken);
            await writer.WriteAsync(asset.SourceSystem, NpgsqlDbType.Varchar, cancellationToken);
            await WriteNullableStringAsync(writer, asset.SourceRecordId, NpgsqlDbType.Varchar, cancellationToken);
            await WriteNullableDateTimeAsync(writer, asset.LastObservedAt, cancellationToken);
            await writer.WriteAsync(asset.CreatedAt, NpgsqlDbType.TimestampTz, cancellationToken);
            await writer.WriteAsync(asset.UpdatedAt, NpgsqlDbType.TimestampTz, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static async Task WriteNullableStringAsync(
        NpgsqlBinaryImporter writer,
        string? value,
        NpgsqlDbType dbType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            await writer.WriteNullAsync(cancellationToken);
            return;
        }

        await writer.WriteAsync(value, dbType, cancellationToken);
    }

    private static async Task WriteNullableDoubleAsync(
        NpgsqlBinaryImporter writer,
        double? value,
        CancellationToken cancellationToken)
    {
        if (value.HasValue)
        {
            await writer.WriteAsync(value.Value, NpgsqlDbType.Double, cancellationToken);
            return;
        }

        await writer.WriteNullAsync(cancellationToken);
    }

    private static async Task WriteNullableDateTimeAsync(
        NpgsqlBinaryImporter writer,
        DateTime? value,
        CancellationToken cancellationToken)
    {
        if (value.HasValue)
        {
            await writer.WriteAsync(value.Value, NpgsqlDbType.TimestampTz, cancellationToken);
            return;
        }

        await writer.WriteNullAsync(cancellationToken);
    }

    private Point CreatePoint(double longitude, double latitude)
    {
        return _geometryFactory.CreatePoint(new Coordinate(longitude, latitude));
    }

    private static Dictionary<string, string> ToDictionary(TagsCollectionBase? tags)
    {
        if (tags is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return tags.ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static JsonDocument ToJsonDocument(IReadOnlyDictionary<string, string> data)
    {
        return JsonSerializer.SerializeToDocument(data);
    }

    private static JsonDocument BuildAccessibilityProfileDocument(
        IReadOnlyDictionary<string, string> tags,
        string assetType,
        string? sourceRecordId,
        DateTime? lastObservedAt)
    {
        var profile = AccessibilityProfileMapper.BuildFromOsmTags(
            tags,
            assetType,
            "osm",
            sourceRecordId,
            lastObservedAt);

        return AccessibilityProfileMapper.ToJsonDocument(profile);
    }

    private static string ToJson(JsonDocument document)
    {
        return document.RootElement.GetRawText();
    }

    private sealed class ImportCounters
    {
        public int RecordsSeen { get; set; }
        public int RouteNodesInserted { get; set; }
        public int RouteEdgesInserted { get; set; }
        public int InfrastructureAssetsInserted { get; set; }
        public int RecordsFailed { get; set; }
    }
}
