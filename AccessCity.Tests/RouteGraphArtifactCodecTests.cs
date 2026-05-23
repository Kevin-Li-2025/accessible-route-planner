using AccessCity.API.Models;
using AccessCity.API.Services;
using NetTopologySuite.Geometries;

namespace AccessCity.Tests;

public sealed class RouteGraphArtifactCodecTests
{
    [Fact]
    public void Packed_route_graph_artifact_round_trips_static_edge_weights()
    {
        var graphData = new RouteGraphData
        {
            ShardKey = "test-shard",
            LoadedEdgeCount = 1,
            Nodes = new Dictionary<long, GraphNode>
            {
                [1] = new()
                {
                    Id = 1,
                    Location = new Coordinate(-1.8904, 52.4862),
                    Edges =
                    {
                        [2] = new GraphEdge
                        {
                            TargetNodeId = 2,
                            DistanceMetres = 90,
                            BaseSafetyCost = 0.2,
                            SurfaceType = "unknown",
                            AccessibilityCostVersion = RouteEdgeCostModel.Version,
                            StandardAccessibilityPenaltySeconds = 10,
                            WheelchairAccessibilityPenaltySeconds = 120,
                            StrollerAccessibilityPenaltySeconds = 90
                        }
                    }
                },
                [2] = new()
                {
                    Id = 2,
                    Location = new Coordinate(-1.8894, 52.4862)
                }
            }
        };
        RouteGraphSpatialIndex.BuildSpatialBuckets(graphData);

        var artifact = RouteGraphArtifactCodec.Pack(graphData);
        var unpacked = RouteGraphArtifactCodec.Unpack(artifact);

        Assert.Equal(RouteGraphArtifactCodec.SchemaVersion, artifact.SchemaVersion);
        Assert.Equal(RouteEdgeCostModel.EdgeWeightVersion, artifact.EdgeWeightVersion);
        Assert.Single(artifact.Edges);
        Assert.Equal(0, artifact.Nodes.Single(node => node.Id == 1).FirstEdgeIndex);

        var edge = unpacked.Nodes[1].Edges[2];
        Assert.Equal(RouteEdgeCostModel.EdgeWeightVersion, edge.EdgeWeightVersion);
        Assert.True(edge.WheelchairTraversalSeconds > edge.StandardTraversalSeconds);
        Assert.True(unpacked.SpatialBuckets.Count > 0);
    }
}
