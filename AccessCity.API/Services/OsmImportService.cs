using System.Text.Json;
using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using OsmSharp;
using OsmSharp.Complete;
using OsmSharp.Streams;
using OsmSharp.Streams.Complete;
using OsmSharp.Tags;

namespace AccessCity.API.Services;

public interface IOsmImportService
{
    Task<OsmImportResult> ImportConfiguredAsync(CancellationToken cancellationToken = default);
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
    private readonly ILogger<OsmImportService> _logger;
    private readonly GeometryFactory _geometryFactory = new(new PrecisionModel(), 4326);

    public OsmImportService(
        AppDbContext dbContext,
        IOptions<OsmImportOptions> options,
        ILogger<OsmImportService> logger)
    {
        _dbContext = dbContext;
        _options = options;
        _logger = logger;
    }

    public async Task<OsmImportResult> ImportConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var filePathConfig = _options.Value.FilePath;
        if (string.IsNullOrWhiteSpace(filePathConfig))
        {
            throw new InvalidOperationException("OsmImport:FilePath is not configured.");
        }

        var filePaths = filePathConfig.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
                var fullPath = Path.GetFullPath(filePath);
                if (!File.Exists(fullPath))
                {
                    _logger.LogWarning("OSM import file not found: {FullPath}", fullPath);
                    continue;
                }

                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                var fileCounters = await ImportFileAsync(_dbContext, fullPath, globalSeenNodes, cancellationToken);
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
            var run = new FeedIngestionRun
            {
                SourceType = "osm",
                SourceName = string.Join(';', filePaths),
                Status = "completed",
                StartedAt = startedAt,
                FinishedAt = finishedAt,
                RecordsSeen = combinedCounters.RecordsSeen,
                RecordsInserted = combinedCounters.RouteNodesInserted + combinedCounters.RouteEdgesInserted + combinedCounters.InfrastructureAssetsInserted,
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

    private async Task<ImportCounters> ImportFileAsync(
        AppDbContext dbContext,
        string filePath,
        HashSet<long> seenRouteNodeIds,
        CancellationToken cancellationToken)
    {
        var nodeCache = new Dictionary<long, (double Lon, double Lat)>();
        var seenAssetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingRouteNodes = new List<RouteNode>(250);
        var pendingRouteEdges = new List<RouteEdge>(250);
        var pendingAssets = new List<InfrastructureAsset>(250);
        var counters = new ImportCounters();

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

                if (pendingAssets.Count >= 500)
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

                if (pendingRouteNodes.Count >= 500 || pendingRouteEdges.Count >= 1000 || pendingAssets.Count >= 500)
                {
                    await FlushAsync(dbContext, pendingRouteNodes, pendingRouteEdges, pendingAssets, cancellationToken);
                }
            }
            await FlushAsync(dbContext, pendingRouteNodes, pendingRouteEdges, pendingAssets, cancellationToken);
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
        var surface = tags.GetValueOrDefault("surface", "asphalt");
        var lit = tags.GetValueOrDefault("lit");
        var highway = tags.GetValueOrDefault("highway");
        var incline = tags.GetValueOrDefault("incline");
        var barrier = tags.GetValueOrDefault("barrier");
        var access = tags.GetValueOrDefault("access");

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
            DistanceMetres = RiskScoringService.HaversineDistance(fromPoint.Y, fromPoint.X, toPoint.Y, toPoint.X),
            BaseSafetyCost = ComputeBaseSafetyCost(surface, lit, highway, incline, barrier),
            SurfaceType = surface,
            HasStairs = string.Equals(highway, "steps", StringComparison.OrdinalIgnoreCase),
            LightingQuality = lit?.ToLowerInvariant() switch
            {
                "yes" => 0.95,
                "limited" => 0.55,
                "no" => 0.1,
                _ => 0.45
            },
            IsSteep = IsSteep(incline),
            HasBarrier = !string.IsNullOrWhiteSpace(barrier),
            Access = access,
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

        asset = new InfrastructureAsset
        {
            AssetType = assetType,
            Name = tags.GetValueOrDefault("name"),
            Geometry = geometry,
            SourceSystem = "osm",
            SourceRecordId = $"way:{way.Id}",
            LastObservedAt = way.TimeStamp,
            AccessibilityInfo = ToJsonDocument(tags)
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

        asset = new InfrastructureAsset
        {
            AssetType = assetType,
            Name = tags.GetValueOrDefault("name"),
            Geometry = CreatePoint(node.Longitude.Value, node.Latitude.Value),
            SourceSystem = "osm",
            SourceRecordId = $"node:{node.Id.Value}",
            LastObservedAt = node.TimeStamp,
            AccessibilityInfo = ToJsonDocument(tags)
        };

        return true;
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

    private static double ComputeBaseSafetyCost(string surface, string? lit, string? highway, string? incline, string? barrier)
    {
        var score = surface.ToLowerInvariant() switch
        {
            "asphalt" => 0.08,
            "paving_stones" => 0.14,
            "concrete" => 0.1,
            "cobblestone" => 0.35,
            "gravel" => 0.4,
            "unpaved" => 0.45,
            _ => 0.2
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
            score += 0.3; // Obstacle penalty
        }

        return Math.Clamp(score, 0.01, 0.95);
    }

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
        pendingRouteNodes.Clear();
        pendingRouteEdges.Clear();
        pendingAssets.Clear();
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

    private sealed class ImportCounters
    {
        public int RecordsSeen { get; set; }
        public int RouteNodesInserted { get; set; }
        public int RouteEdgesInserted { get; set; }
        public int InfrastructureAssetsInserted { get; set; }
        public int RecordsFailed { get; set; }
    }
}
