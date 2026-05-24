using AccessCity.API.Configuration;
using AccessCity.API.Models;
using AccessCity.API.Services;

namespace AccessCity.Tests;

public sealed class RouteGraphProfileQualityTests
{
    [Fact]
    public void Finalize_marks_profile_unhealthy_when_city_artifacts_exceed_thresholds()
    {
        var response = new RouteGraphProfileResponse
        {
            SourceType = "osm-extract-offline",
            SourceShardCount = 4,
            SourceIsTruncated = true,
            MaxArtifactBytes = 40_000_000,
            MaxColdLoadMilliseconds = 2_500,
            MaxHotLoadMilliseconds = 140,
            MaxArtifactStoreReadMilliseconds = 160,
            MaxArtifactUnpackMilliseconds = 175,
            Routes =
            {
                new RouteGraphProfileRouteResult
                {
                    SourceShardCount = 9,
                    WouldCacheDistributedPayload = false,
                    RedisPayloadBytes = 9_000_000,
                    ArtifactBytes = 40_000_000,
                    ColdLoadMilliseconds = 2_500,
                    HotLoadMilliseconds = 140,
                    ArtifactPackMilliseconds = 900,
                    ArtifactStoreReadMilliseconds = 160,
                    ArtifactUnpackMilliseconds = 175,
                    EdgeCount = 2_000
                }
            }
        };

        var finalized = RouteGraphProfileQualityEvaluator.Finalize(
            response,
            new RoutingOptions
            {
                RouteGraphProfileMaxRedisPayloadBytes = 8_000_000,
                RouteGraphProfileMaxArtifactBytes = 32_000_000,
                RouteGraphProfileMaxColdLoadMilliseconds = 2_000,
                RouteGraphProfileMaxHotLoadMilliseconds = 100,
                RouteGraphProfileMaxArtifactPackMilliseconds = 750,
                RouteGraphProfileMaxArtifactStoreReadMilliseconds = 150,
                RouteGraphProfileMaxArtifactUnpackMilliseconds = 150,
                RouteGraphProfileMaxShardReferencesPerRoute = 8,
                RouteGraphAltPreprocessingEnabled = false
            });

        Assert.False(finalized.QualityGatePassed);
        Assert.Equal(9_000_000, finalized.MaxRedisPayloadBytes);
        Assert.Equal(9, finalized.MaxSourceShardCountPerRoute);
        Assert.Equal(9.0, finalized.AverageShardReferencesPerRoute);
        Assert.Equal(140.0, finalized.P95HotLoadMilliseconds);
        Assert.Equal(175.0, finalized.P95ArtifactUnpackMilliseconds);
        Assert.Contains(finalized.QualityGateWarnings, warning => warning.Contains("source graph was truncated", StringComparison.Ordinal));
        Assert.Contains(finalized.QualityGateWarnings, warning => warning.Contains("max Redis payload", StringComparison.Ordinal));
        Assert.Contains(finalized.QualityGateWarnings, warning => warning.Contains("max source shards per route", StringComparison.Ordinal));
        Assert.Contains(finalized.QualityGateWarnings, warning => warning.Contains("will not be written to distributed cache", StringComparison.Ordinal));
    }

    [Fact]
    public void Finalize_passes_profile_when_artifacts_are_within_thresholds()
    {
        var response = new RouteGraphProfileResponse
        {
            Routes =
            {
                new RouteGraphProfileRouteResult
                {
                    SourceShardCount = 2,
                    RedisPayloadBytes = 64_000,
                    ArtifactBytes = 128_000,
                    ColdLoadMilliseconds = 35,
                    HotLoadMilliseconds = 4,
                    ArtifactPackMilliseconds = 6,
                    ArtifactStoreReadMilliseconds = 3,
                    ArtifactUnpackMilliseconds = 5,
                    HasAltPreprocessing = true,
                    EdgeCount = 120
                }
            }
        };

        var finalized = RouteGraphProfileQualityEvaluator.Finalize(response, new RoutingOptions());

        Assert.True(finalized.QualityGatePassed);
        Assert.Empty(finalized.QualityGateWarnings);
        Assert.Equal(64_000, finalized.MaxRedisPayloadBytes);
        Assert.Equal(2, finalized.MaxSourceShardCountPerRoute);
    }
}
