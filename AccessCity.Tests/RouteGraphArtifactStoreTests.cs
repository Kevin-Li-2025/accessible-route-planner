using AccessCity.API.Configuration;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace AccessCity.Tests;

public sealed class RouteGraphArtifactStoreTests
{
    [Fact]
    public async Task File_artifact_store_round_trips_packed_route_graph_payload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"accesscity-artifacts-{Guid.NewGuid():N}");
        var store = new RouteGraphArtifactStore(
            Options.Create(new RoutingOptions
            {
                RouteGraphPackedArtifactsEnabled = true,
                RouteGraphFileArtifactStoreEnabled = true,
                RouteGraphFileArtifactDirectory = directory
            }),
            NullLogger<RouteGraphArtifactStore>.Instance);

        var graphData = new RouteGraphData
        {
            ShardKey = "route-graph:test",
            SourceShardKeys = new[] { "route-graph:test:cell" },
            LoadedEdgeCount = 1,
            Nodes = new Dictionary<long, GraphNode>
            {
                [1] = new()
                {
                    Id = 1,
                    Location = new Coordinate(-1.8904, 52.4862),
                    Edges =
                    {
                        [2] = new GraphEdge { TargetNodeId = 2, DistanceMetres = 90 }
                    }
                },
                [2] = new()
                {
                    Id = 2,
                    Location = new Coordinate(-1.8894, 52.4862)
                }
            }
        };
        RouteGraphPreprocessor.TryAttachPreprocessing(graphData, new RoutingOptions
        {
            RouteGraphAltPreprocessingEnabled = true,
            RouteGraphAltLandmarkCount = 2,
            RouteGraphMaxAltPreprocessedNodes = 10
        });

        var artifact = RouteGraphArtifactCodec.Pack(graphData);
        var payload = RouteGraphArtifactCodec.SerializeRedisPayload(artifact);

        var written = await store.WriteAsync("route-graph:test", artifact, payload, "unit-test");
        Assert.NotNull(written);
        Assert.True(File.Exists(written!.ArtifactPath));

        var read = await store.TryReadAsync("route-graph:test");
        Assert.NotNull(read);
        Assert.Equal(payload.Length, read!.PayloadBytes);

        var restored = RouteGraphArtifactCodec.Unpack(read.Artifact);
        Assert.True(restored.HasCoverage);
        Assert.Equal(graphData.LoadedEdgeCount, restored.LoadedEdgeCount);
        Assert.True(restored.Preprocessing?.HasLandmarks);
    }

    [Fact]
    public async Task File_artifact_store_round_trips_manifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"accesscity-artifacts-{Guid.NewGuid():N}");
        var store = new RouteGraphArtifactStore(
            Options.Create(new RoutingOptions
            {
                RouteGraphPackedArtifactsEnabled = true,
                RouteGraphFileArtifactStoreEnabled = true,
                RouteGraphFileArtifactDirectory = directory,
                RouteGraphFileArtifactManifestEnabled = true
            }),
            NullLogger<RouteGraphArtifactStore>.Instance);

        var manifest = new RouteGraphArtifactManifest(
            RouteGraphArtifactCodec.SchemaVersion,
            RouteEdgeCostModel.Version,
            RouteEdgeCostModel.EdgeWeightVersion,
            RouteGraphPreprocessor.AltAlgorithmVersion,
            0.01,
            "fixture.osm",
            DateTime.UtcNow,
            new[]
            {
                new RouteGraphArtifactManifestShard(
                    "route-graph:test",
                    -1.90,
                    52.48,
                    -1.89,
                    52.49,
                    2,
                    1,
                    128,
                    DateTime.UtcNow,
                    "unit-test",
                    "route-graph-test.acrg")
            });

        var written = await store.WriteManifestAsync(manifest);
        Assert.NotNull(written);
        var bytes = await File.ReadAllBytesAsync(written!.ArtifactPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        var restored = await store.TryReadManifestAsync();
        Assert.NotNull(restored);
        Assert.Single(restored!.Shards);
        Assert.Equal("route-graph:test", restored.Shards[0].CacheKey);
    }

    [Fact]
    public async Task File_artifact_store_rejects_metadata_key_mismatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"accesscity-artifacts-{Guid.NewGuid():N}");
        var store = new RouteGraphArtifactStore(
            Options.Create(new RoutingOptions
            {
                RouteGraphPackedArtifactsEnabled = true,
                RouteGraphFileArtifactStoreEnabled = true,
                RouteGraphFileArtifactDirectory = directory
            }),
            NullLogger<RouteGraphArtifactStore>.Instance);

        var graphData = new RouteGraphData
        {
            ShardKey = "route-graph:test",
            LoadedEdgeCount = 0,
            Nodes = new Dictionary<long, GraphNode>
            {
                [1] = new() { Id = 1, Location = new Coordinate(-1.8904, 52.4862) },
                [2] = new() { Id = 2, Location = new Coordinate(-1.8894, 52.4862) }
            }
        };
        var artifact = RouteGraphArtifactCodec.Pack(graphData);
        var payload = RouteGraphArtifactCodec.SerializeRedisPayload(artifact);

        var written = await store.WriteAsync("route-graph:test", artifact, payload, "unit-test");
        Assert.NotNull(written);

        var metadataPath = Path.ChangeExtension(written!.ArtifactPath, ".json");
        var metadataJson = await File.ReadAllTextAsync(metadataPath);
        await File.WriteAllTextAsync(
            metadataPath,
            metadataJson.Replace("route-graph:test", "route-graph:tampered", StringComparison.Ordinal));

        Assert.Null(await store.TryReadAsync("route-graph:test"));
    }
}
