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
        var filePath = _options.Value.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new InvalidOperationException("OsmImport:FilePath is not configured.");
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Configured OSM import file was not found.", fullPath);
        }

        var executionStrategy = _dbContext.Database.CreateExecutionStrategy();

        return await executionStrategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var startedAt = DateTime.UtcNow;
            var run = new FeedIngestionRun
            {
                SourceType = "osm",
                SourceName = fullPath,
                Status = "running",
                StartedAt = startedAt,
                Metadata = JsonSerializer.SerializeToDocument(new
                {
                    filePath = fullPath,
                    replaceExisting = _options.Value.ReplaceExisting
                })
            };

            _dbContext.FeedIngestionRuns.Add(run);
            await _dbContext.SaveChangesAsync(cancellationToken);

            try
            {
                if (_options.Value.ReplaceExisting)
                {
                    await _dbContext.RouteEdges.ExecuteDeleteAsync(cancellationToken);
                    await _dbContext.RouteNodes.ExecuteDeleteAsync(cancellationToken);
                    await _dbContext.InfrastructureAssets
                        .Where(asset => asset.SourceSystem == "osm")
                        .ExecuteDeleteAsync(cancellationToken);
                }

                var counters = await ImportFileAsync(_dbContext, fullPath, cancellationToken);

                run.Status = "completed";
                run.FinishedAt = DateTime.UtcNow;
                run.RecordsSeen = counters.RecordsSeen;
                run.RecordsInserted = counters.RouteNodesInserted + counters.RouteEdgesInserted + counters.InfrastructureAssetsInserted;
                run.RecordsUpdated = 0;
                run.RecordsFailed = counters.RecordsFailed;
                run.Metadata = JsonSerializer.SerializeToDocument(new
                {
                    filePath = fullPath,
                    routeNodesInserted = counters.RouteNodesInserted,
                    routeEdgesInserted = counters.RouteEdgesInserted,
                    infrastructureAssetsInserted = counters.InfrastructureAssetsInserted
                });

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new OsmImportResult
                {
                    RunId = run.Id,
                    SourceName = fullPath,
                    Status = run.Status,
                    RecordsSeen = counters.RecordsSeen,
                    RouteNodesInserted = counters.RouteNodesInserted,
                    RouteEdgesInserted = counters.RouteEdgesInserted,
                    InfrastructureAssetsInserted = counters.InfrastructureAssetsInserted,
                    RecordsFailed = counters.RecordsFailed,
                    Duration = run.FinishedAt.GetValueOrDefault(startedAt) - startedAt
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OSM import failed for {FilePath}", fullPath);

                run.Status = "failed";
                run.FinishedAt = DateTime.UtcNow;
                run.ErrorSummary = ex.Message;
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.RollbackAsync(cancellationToken);

                throw;
            }
        });
    }

    private async Task<ImportCounters> ImportFileAsync(
        AppDbContext dbContext,
        string filePath,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(filePath);
        var source = CreateSource(stream, filePath);
        var completeSource = new OsmSimpleCompleteStreamSource(source);

        var seenRouteNodeIds = new HashSet<long>();
        var seenAssetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingRouteNodes = new List<RouteNode>();
        var pendingRouteEdges = new List<RouteEdge>();
        var pendingAssets = new List<InfrastructureAsset>();
        var counters = new ImportCounters();

        foreach (var osmGeo in completeSource)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counters.RecordsSeen++;

            switch (osmGeo)
            {
                case Node node:
                    if (TryCreatePointInfrastructureAsset(node, out var pointAsset) &&
                        pointAsset is not null &&
                        seenAssetKeys.Add(pointAsset.SourceRecordId!))
                    {
                        pendingAssets.Add(pointAsset);
                        counters.InfrastructureAssetsInserted++;
                    }
                    break;

                case CompleteWay way:
                    AddWalkableWay(way, seenRouteNodeIds, pendingRouteNodes, pendingRouteEdges, counters);

                    if (TryCreateWayInfrastructureAsset(way, out var wayAsset) &&
                        wayAsset is not null &&
                        seenAssetKeys.Add(wayAsset.SourceRecordId!))
                    {
                        pendingAssets.Add(wayAsset);
                        counters.InfrastructureAssetsInserted++;
                    }
                    break;
            }

            if (pendingRouteNodes.Count >= 2000 || pendingRouteEdges.Count >= 4000 || pendingAssets.Count >= 1000)
            {
                await FlushAsync(dbContext, pendingRouteNodes, pendingRouteEdges, pendingAssets, cancellationToken);
            }
        }

        await FlushAsync(dbContext, pendingRouteNodes, pendingRouteEdges, pendingAssets, cancellationToken);
        return counters;
    }

    private static OsmStreamSource CreateSource(Stream stream, string filePath)
    {
        if (filePath.EndsWith(".pbf", StringComparison.OrdinalIgnoreCase))
        {
            return new PBFOsmStreamSource(stream);
        }

        return new XmlOsmStreamSource(stream);
    }

    private void AddWalkableWay(
        CompleteWay way,
        HashSet<long> seenRouteNodeIds,
        List<RouteNode> pendingRouteNodes,
        List<RouteEdge> pendingRouteEdges,
        ImportCounters counters)
    {
        if (!IsWalkable(way))
        {
            return;
        }

        var orderedNodes = way.Nodes
            .Where(node => node?.Id is not null && node.Latitude is not null && node.Longitude is not null)
            .ToArray();

        if (orderedNodes.Length < 2)
        {
            return;
        }

        foreach (var node in orderedNodes)
        {
            var nodeId = node.Id!.Value;
            if (seenRouteNodeIds.Add(nodeId))
            {
                pendingRouteNodes.Add(new RouteNode
                {
                    Id = nodeId,
                    Location = CreatePoint(node.Longitude!.Value, node.Latitude!.Value),
                    Tags = ToJsonDocument(ToDictionary(node.Tags))
                });
                counters.RouteNodesInserted++;
            }
        }

        for (var i = 0; i < orderedNodes.Length - 1; i++)
        {
            var from = orderedNodes[i];
            var to = orderedNodes[i + 1];

            if (from.Id is null || to.Id is null)
            {
                continue;
            }

            var fromPoint = CreatePoint(from.Longitude!.Value, from.Latitude!.Value);
            var toPoint = CreatePoint(to.Longitude!.Value, to.Latitude!.Value);
            var edge = CreateRouteEdge(way, from.Id.Value, to.Id.Value, fromPoint, toPoint);
            pendingRouteEdges.Add(edge);
            counters.RouteEdgesInserted++;

            if (IsBidirectionalForWalking(way))
            {
                pendingRouteEdges.Add(CreateRouteEdge(way, to.Id.Value, from.Id.Value, toPoint, fromPoint));
                counters.RouteEdgesInserted++;
            }
        }
    }

    private RouteEdge CreateRouteEdge(CompleteWay way, long fromNodeId, long toNodeId, Point fromPoint, Point toPoint)
    {
        var tags = ToDictionary(way.Tags);
        var surface = tags.GetValueOrDefault("surface", "asphalt");
        var lit = tags.GetValueOrDefault("lit");
        var highway = tags.GetValueOrDefault("highway");
        var incline = tags.GetValueOrDefault("incline");
        var crossing = tags.GetValueOrDefault("crossing");
        var footway = tags.GetValueOrDefault("footway");

        var line = _geometryFactory.CreateLineString(new[]
        {
            fromPoint.Coordinate,
            toPoint.Coordinate
        });

        return new RouteEdge
        {
            FromNodeId = fromNodeId,
            ToNodeId = toNodeId,
            SourceWayId = way.Id,
            Geometry = line,
            DistanceMetres = RiskScoringService.HaversineDistance(fromPoint.Y, fromPoint.X, toPoint.Y, toPoint.X),
            BaseSafetyCost = ComputeBaseSafetyCost(surface, lit, highway, incline),
            SurfaceType = surface,
            HasStairs = string.Equals(highway, "steps", StringComparison.OrdinalIgnoreCase),
            HasCrossing = string.Equals(crossing, "marked", StringComparison.OrdinalIgnoreCase)
                || string.Equals(footway, "crossing", StringComparison.OrdinalIgnoreCase)
                || string.Equals(highway, "crossing", StringComparison.OrdinalIgnoreCase),
            IsUnderConstruction = string.Equals(highway, "construction", StringComparison.OrdinalIgnoreCase)
                || tags.ContainsKey("construction"),
            LightingQuality = string.Equals(lit, "yes", StringComparison.OrdinalIgnoreCase) ? 0.95 : 0.45,
            IsSteep = IsSteep(incline),
            Tags = ToJsonDocument(tags)
        };
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

    private bool TryCreateWayInfrastructureAsset(CompleteWay way, out InfrastructureAsset? asset)
    {
        asset = null;

        var tags = ToDictionary(way.Tags);
        var assetType = GetInfrastructureAssetType(tags);
        if (assetType is null)
        {
            return false;
        }

        var points = way.Nodes
            .Where(node => node?.Latitude is not null && node.Longitude is not null)
            .Select(node => new Coordinate(node!.Longitude!.Value, node.Latitude!.Value))
            .ToArray();

        if (points.Length == 0)
        {
            return false;
        }

        Geometry geometry = points.Length == 1
            ? _geometryFactory.CreatePoint(points[0])
            : _geometryFactory.CreateLineString(points);

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

    private static bool IsWalkable(CompleteWay way)
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
            string.Equals(access, "private", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(tags.GetValueOrDefault("foot"), "yes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return WalkableHighways.Contains(highway)
            || string.Equals(tags.GetValueOrDefault("foot"), "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tags.GetValueOrDefault("foot"), "designated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBidirectionalForWalking(CompleteWay way)
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

    private static double ComputeBaseSafetyCost(string surface, string? lit, string? highway, string? incline)
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
