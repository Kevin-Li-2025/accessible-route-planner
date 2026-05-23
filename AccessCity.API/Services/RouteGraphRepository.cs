using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using System.Globalization;

namespace AccessCity.API.Services;

public interface IRouteGraphRepository
{
    Task<RouteGraphData> LoadGraphAsync(Coordinate start, Coordinate end, CancellationToken cancellationToken = default);
}

public sealed class RouteGraphRepository : IRouteGraphRepository
{
    private readonly AppDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RouteGraphRepository> _logger;
    private readonly RoutingOptions _options;

    public RouteGraphRepository(
        AppDbContext dbContext,
        IMemoryCache cache,
        IOptions<RoutingOptions> options,
        ILogger<RouteGraphRepository> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RouteGraphData> LoadGraphAsync(
        Coordinate start,
        Coordinate end,
        CancellationToken cancellationToken = default)
    {
        var edgeLimit = Math.Max(100, _options.MaxRouteGraphEdges);
        var region = ComputeShardRegion(start, end);
        var cacheKey = BuildCacheKey(region, edgeLimit);

        if (_cache.TryGetValue(cacheKey, out RouteGraphData? cached) && cached is not null)
        {
            return cached;
        }

        var graphData = await LoadGraphRegionAsync(region, edgeLimit, cacheKey, cancellationToken);
        var ttl = TimeSpan.FromSeconds(Math.Max(30, _options.RouteGraphCacheTtlSeconds));
        _cache.Set(cacheKey, graphData, ttl);
        return graphData;
    }

    private async Task<RouteGraphData> LoadGraphRegionAsync(
        GraphShardRegion region,
        int edgeLimit,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var edges = await _dbContext.RouteEdges
            .FromSqlInterpolated($"""
                SELECT *
                FROM route_edges
                WHERE ST_Intersects(
                    "Geometry",
                    ST_MakeEnvelope({region.MinLon}, {region.MinLat}, {region.MaxLon}, {region.MaxLat}, 4326))
                ORDER BY "Geometry" <-> ST_Centroid(ST_MakeEnvelope({region.MinLon}, {region.MinLat}, {region.MaxLon}, {region.MaxLat}, 4326))
                LIMIT {edgeLimit + 1}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (edges.Count == 0)
        {
            return new RouteGraphData();
        }

        var isTruncated = edges.Count > edgeLimit;
        if (isTruncated)
        {
            edges = edges.Take(edgeLimit).ToList();
        }

        var nodeIds = edges
            .SelectMany(edge => new[] { edge.FromNodeId, edge.ToNodeId })
            .Distinct()
            .ToList();

        var nodes = await _dbContext.RouteNodes
            .Where(node => nodeIds.Contains(node.Id))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var graph = nodes.ToDictionary(
            node => node.Id,
            node => new GraphNode
            {
                Id = node.Id,
                Location = node.Location.Coordinate
            });

        foreach (var edge in edges)
        {
            if (!graph.TryGetValue(edge.FromNodeId, out var fromNode) ||
                !graph.ContainsKey(edge.ToNodeId))
            {
                continue;
            }

            fromNode.Edges[edge.ToNodeId] = new GraphEdge
            {
                TargetNodeId = edge.ToNodeId,
                DistanceMetres = edge.DistanceMetres,
                BaseSafetyCost = edge.BaseSafetyCost,
                SurfaceType = edge.SurfaceType,
                HasStairs = edge.HasStairs,
                HasCrossing = edge.HasCrossing,
                IsUnderConstruction = edge.IsUnderConstruction,
                LightingQuality = edge.LightingQuality,
                IsSteep = edge.IsSteep,
                KerbHeight = edge.KerbHeight,
                Smoothness = edge.Smoothness,
                WidthMetres = edge.WidthMetres,
                HasTactilePaving = edge.HasTactilePaving,
                HasBarrier = edge.HasBarrier,
                Access = edge.Access,
                Geometry = edge.Geometry?.Coordinates
            };
        }

        var graphData = new RouteGraphData
        {
            Nodes = graph,
            IsTruncated = isTruncated,
            ShardKey = cacheKey,
            LoadedEdgeCount = edges.Count,
            SpatialBucketSizeDegrees = 0.001
        };
        BuildSpatialBuckets(graphData);

        _logger.LogDebug(
            "Loaded route graph shard {ShardKey}: {NodeCount} nodes, {EdgeCount} edges, truncated={IsTruncated}",
            cacheKey,
            graph.Count,
            edges.Count,
            isTruncated);

        return graphData;
    }

    private static double ComputePaddingDegrees(Coordinate start, Coordinate end)
    {
        var latitudeDelta = Math.Abs(start.Y - end.Y);
        var longitudeDelta = Math.Abs(start.X - end.X);
        return Math.Max(0.01, Math.Max(latitudeDelta, longitudeDelta) * 0.35);
    }

    private GraphShardRegion ComputeShardRegion(Coordinate start, Coordinate end)
    {
        var padding = ComputePaddingDegrees(start, end);
        var minLon = Math.Min(start.X, end.X) - padding;
        var maxLon = Math.Max(start.X, end.X) + padding;
        var minLat = Math.Min(start.Y, end.Y) - padding;
        var maxLat = Math.Max(start.Y, end.Y) + padding;
        var shardSize = Math.Clamp(_options.RouteGraphShardSizeDegrees, 0.002, 0.05);

        return new GraphShardRegion(
            Math.Floor(minLon / shardSize) * shardSize,
            Math.Floor(minLat / shardSize) * shardSize,
            Math.Ceiling(maxLon / shardSize) * shardSize,
            Math.Ceiling(maxLat / shardSize) * shardSize);
    }

    private static string BuildCacheKey(GraphShardRegion region, int edgeLimit) =>
        string.Create(CultureInfo.InvariantCulture,
            $"route_graph:v3:{edgeLimit}:{region.MinLon:F4}:{region.MinLat:F4}:{region.MaxLon:F4}:{region.MaxLat:F4}");

    private static void BuildSpatialBuckets(RouteGraphData graphData)
    {
        var bucketSize = graphData.SpatialBucketSizeDegrees;
        foreach (var node in graphData.Nodes.Values)
        {
            var bucket = (
                X: (int)Math.Floor(node.Location.X / bucketSize),
                Y: (int)Math.Floor(node.Location.Y / bucketSize));
            if (!graphData.SpatialBuckets.TryGetValue(bucket, out var nodeIds))
            {
                nodeIds = new List<long>();
                graphData.SpatialBuckets[bucket] = nodeIds;
            }

            nodeIds.Add(node.Id);
        }
    }

    private sealed record GraphShardRegion(double MinLon, double MinLat, double MaxLon, double MaxLat);
}
