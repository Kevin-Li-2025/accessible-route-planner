using AccessCity.API.Services;

namespace AccessCity.Tests;

public class AccessibilityProfileMapperTests
{
    [Fact]
    public void BuildFromOsmTags_Normalizes_Path_Restroom_Entrance_Photo_And_Verification_Data()
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["amenity"] = "toilets",
            ["wheelchair"] = "yes",
            ["toilets:wheelchair"] = "yes",
            ["toilets:door:width"] = "90 cm",
            ["toilets:grab_bar"] = "yes",
            ["changing_table"] = "no",
            ["entrance"] = "main",
            ["door:width"] = "0.9 m",
            ["surface"] = "concrete",
            ["smoothness"] = "good",
            ["width"] = "1.8",
            ["kerb"] = "lowered",
            ["tactile_paving"] = "yes",
            ["incline"] = "3%",
            ["check_date"] = "2026-01-15",
            ["mapillary"] = "123456"
        };

        var profile = AccessibilityProfileMapper.BuildFromOsmTags(
            tags,
            "amenity:toilets",
            "osm",
            "node:42",
            observedAtUtc: null,
            generatedAtUtc: new DateTime(2026, 05, 24, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(AccessibilityProfileMapper.SchemaVersion, profile.SchemaVersion);
        Assert.Equal("verified", profile.VerificationStatus);
        Assert.True(profile.Confidence >= 0.75);
        Assert.Equal(new DateTime(2026, 01, 15, 0, 0, 0, DateTimeKind.Utc), profile.LastVerifiedAtUtc);
        Assert.Equal("concrete", profile.Path.Surface);
        Assert.Equal(1.8, profile.Path.WidthMetres.GetValueOrDefault(), precision: 1);
        Assert.Equal(0, profile.Path.KerbHeightMetres.GetValueOrDefault(), precision: 3);
        Assert.Equal(3, profile.Path.InclinePercent.GetValueOrDefault(), precision: 1);
        Assert.True(profile.Path.HasCurbRamp);
        Assert.True(profile.Path.HasTactilePaving);
        Assert.True(profile.Path.HasStepFreeAccess);

        var entrance = Assert.Single(profile.Entrances);
        Assert.Equal("main", entrance.EntranceType);
        Assert.Equal(0.9, entrance.DoorWidthMetres.GetValueOrDefault(), precision: 1);

        var restroom = Assert.Single(profile.Restrooms);
        Assert.True(restroom.WheelchairAccessible);
        Assert.True(restroom.HasGrabBars);
        Assert.Equal(0.9, restroom.DoorWidthMetres.GetValueOrDefault(), precision: 1);
        Assert.False(restroom.HasChangingTable);

        var photo = Assert.Single(profile.Photos);
        Assert.Equal("mapillary", photo.Source);
        Assert.Contains("123456", photo.Url);
        Assert.DoesNotContain("last_verified_at", profile.MissingFields);
        Assert.Equal(tags.Count, profile.RawTagCount);
    }
}
