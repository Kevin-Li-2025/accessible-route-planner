using System.Net;
using System.Net.Http.Json;
using AccessCity.API.Data;
using AccessCity.API.Models;
using AccessCity.API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AccessCity.Tests;

public sealed class AccessibilityVerificationTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;

    public AccessibilityVerificationTests(AccessCityApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Submit_And_Apply_Verification_Updates_Profile_With_Audit_Record()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);
        var assetId = await FindAssetIdAsync("node:1004");

        var request = new AccessibilityVerificationRequest
        {
            ObservedAtUtc = new DateTime(2026, 05, 20, 12, 0, 0, DateTimeKind.Utc),
            Source = "field_survey",
            Notes = "Measured accessible toilet and entrance during field survey.",
            Path = new AccessibilityPathAttributes
            {
                Surface = "concrete",
                Smoothness = "good",
                WidthMetres = 1.7,
                KerbHeightMetres = 0,
                HasCurbRamp = true,
                HasTactilePaving = true,
                InclinePercent = 1.5,
                HasStepFreeAccess = true,
                WheelchairAccess = "yes"
            },
            Entrance = new AccessibilityEntrance
            {
                EntranceType = "main",
                StepFree = true,
                DoorWidthMetres = 0.95,
                AutomaticDoor = true
            },
            Restroom = new AccessibilityRestroom
            {
                WheelchairAccessible = true,
                HasGrabBars = true,
                DoorWidthMetres = 0.95,
                TurningSpaceMetres = 1.5,
                HasChangingTable = false,
                RequiresKey = false,
                GenderAccess = "unisex"
            },
            Photos =
            [
                new AccessibilityPhotoInput
                {
                    Source = "field_photo",
                    Url = "https://example.com/verified-accessible-toilet.jpg",
                    Caption = "Entrance and accessible toilet door"
                }
            ]
        };

        var submitResponse = await client.PostAsJsonAsync(
            $"/api/v1/spatial/infrastructure/{assetId}/accessibility-verifications",
            request);

        Assert.Equal(HttpStatusCode.Accepted, submitResponse.StatusCode);
        var submitted = await submitResponse.Content.ReadFromJsonAsync<AccessibilityVerificationResponse>();
        Assert.NotNull(submitted);
        Assert.Equal(AccessibilityVerificationStatus.Pending, submitted!.Status);
        Assert.Contains("width_metres", submitted.UpdatedFields);
        Assert.Contains("photos", submitted.UpdatedFields);
        Assert.Null(submitted.AccessibilityProfile);

        var applyResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/accessibility-verifications/{submitted.Id}/apply",
            new AccessibilityVerificationReviewRequest { Notes = "Reviewed field evidence." });

        applyResponse.EnsureSuccessStatusCode();
        var applied = await applyResponse.Content.ReadFromJsonAsync<AccessibilityVerificationResponse>();
        Assert.NotNull(applied);
        Assert.Equal(AccessibilityVerificationStatus.Applied, applied!.Status);
        Assert.NotNull(applied.AccessibilityProfile);
        Assert.Equal("concrete", applied.AccessibilityProfile!.Path.Surface);
        Assert.Equal(1.7, applied.AccessibilityProfile.Path.WidthMetres.GetValueOrDefault(), precision: 1);
        Assert.True(applied.AccessibilityProfile.Path.HasCurbRamp);
        Assert.True(applied.AccessibilityProfile.Restrooms[0].WheelchairAccessible);
        Assert.Equal(0.95, applied.AccessibilityProfile.Entrances[0].DoorWidthMetres.GetValueOrDefault(), precision: 2);
        Assert.Contains(applied.AccessibilityProfile.Photos, photo => photo.Url == "https://example.com/verified-accessible-toilet.jpg");
        Assert.DoesNotContain("surface", applied.AccessibilityProfile.MissingFields);
        Assert.DoesNotContain("width_metres", applied.AccessibilityProfile.MissingFields);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var submissionCount = await dbContext.AccessibilityVerificationSubmissions
            .CountAsync(submission => submission.InfrastructureAssetId == assetId);
        Assert.True(submissionCount >= 1);

        var asset = await dbContext.InfrastructureAssets.SingleAsync(candidate => candidate.Id == assetId);
        var persistedProfile = AccessibilityProfileMapper.Parse(asset.AccessibilityProfile);
        Assert.Equal("verified", persistedProfile.VerificationStatus);
        Assert.Equal("concrete", persistedProfile.Path.Surface);
    }

    [Fact]
    public async Task AiAssist_AccessibilityReview_Returns_Advisory_Candidates_For_Incomplete_Profile()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);
        var assetId = await FindAssetIdAsync("node:1002");

        var response = await client.GetAsync($"/api/v1/ai-assist/infrastructure/{assetId}/accessibility-review");

        response.EnsureSuccessStatusCode();
        var review = await response.Content.ReadFromJsonAsync<AccessibilityAiReviewResult>();
        Assert.NotNull(review);
        Assert.False(review!.ForRouteDecision);
        Assert.Equal(assetId, review.InfrastructureAssetId);
        Assert.NotEmpty(review.MissingAttributeCandidates);
        Assert.All(review.MissingAttributeCandidates, candidate => Assert.False(candidate.CanAutoApply));
        Assert.Contains(review.Guardrails, guardrail => guardrail.Contains("advisory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reject_Verification_Leaves_Profile_Unchanged()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);
        var assetId = await FindAssetIdAsync("node:1005");
        var before = await GetProfileAsync(assetId);

        var submitResponse = await client.PostAsJsonAsync(
            $"/api/v1/spatial/infrastructure/{assetId}/accessibility-verifications",
            new AccessibilityVerificationRequest
            {
                ObservedAtUtc = DateTime.UtcNow,
                Source = "field_survey",
                Path = new AccessibilityPathAttributes
                {
                    Surface = "gravel",
                    Smoothness = "bad"
                }
            });

        submitResponse.EnsureSuccessStatusCode();
        var submitted = await submitResponse.Content.ReadFromJsonAsync<AccessibilityVerificationResponse>();
        Assert.NotNull(submitted);

        var rejectResponse = await client.PostAsJsonAsync(
            $"/api/v1/admin/accessibility-verifications/{submitted!.Id}/reject",
            new AccessibilityVerificationReviewRequest { Notes = "Evidence did not match asset." });

        rejectResponse.EnsureSuccessStatusCode();
        var rejected = await rejectResponse.Content.ReadFromJsonAsync<AccessibilityVerificationResponse>();
        Assert.NotNull(rejected);
        Assert.Equal(AccessibilityVerificationStatus.Rejected, rejected!.Status);

        var after = await GetProfileAsync(assetId);
        Assert.Equal(before.Path.Surface, after.Path.Surface);
        Assert.Equal(before.Path.Smoothness, after.Path.Smoothness);
    }

    private async Task<long> FindAssetIdAsync(string sourceRecordId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.InfrastructureAssets
            .Where(asset => asset.SourceRecordId == sourceRecordId)
            .Select(asset => asset.Id)
            .SingleAsync();
    }

    private async Task<InfrastructureAccessibilityProfile> GetProfileAsync(long assetId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await dbContext.InfrastructureAssets.SingleAsync(candidate => candidate.Id == assetId);
        return AccessibilityProfileMapper.Parse(asset.AccessibilityProfile);
    }
}
