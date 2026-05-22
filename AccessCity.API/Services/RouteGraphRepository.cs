using AccessCity.API.Configuration;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services;

public interface IRouteGraphRepository
{
    Task<RouteGraphData> LoadGraphAsync(Coordinate start, Coordinate end, CancellationToken cancellationToken = default);
}

public sealed class RouteGraphRepository : IRouteGraphRepository
{
    private readonly AppDbContext _dbContext;
    private readonly RoutingOptions _options;

    public RouteGraphRepository(AppDbContext dbContext, IOptions<RoutingOptions> options)
    {
        _dbContext = dbContext;
        _options = options.Value;
    }

    public async Task<RouteGraphData> LoadGraphAsync(
        Coordinate start,
        Coordinate end,
        CancellationToken cancellationToken = default)
    {
        var padding = ComputePaddingDegrees(start, end);
        var minLon = Math.Min(start.X, end.X) - padding;
        var maxLon = Math.Max(start.X, end.X) + padding;
        var minLat = Math.Min(start.Y, end.Y) - padding;
        var maxLat = Math.Max(start.Y, end.Y) + padding;
        var edgeLimit = Math.Max(100, _options.MaxRouteGraphEdges);

        var edges = await _dbContext.RouteEdges
            .FromSqlInterpolated($"""
                SELECT *
                FROM route_edges
                WHERE ST_Intersects(
                    "Geometry",
                    ST_MakeEnvelope({minLon}, {minLat}, {maxLon}, {maxLat}, 4326))
                ORDER BY "Geometry" <-> ST_SetSRID(ST_MakePoint({start.X}, {start.Y}), 4326)
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

        return new RouteGraphData { Nodes = graph, IsTruncated = isTruncated };
    }

    private static double ComputePaddingDegrees(Coordinate start, Coordinate end)
    {
        var latitudeDelta = Math.Abs(start.Y - end.Y);
        var longitudeDelta = Math.Abs(start.X - end.X);
        return Math.Max(0.01, Math.Max(latitudeDelta, longitudeDelta) * 0.35);
    }
}
