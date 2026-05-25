using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task AiAssist_AccessibilityCandidates_Generates_Draft_Without_Route_Authority()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);
        var assetId = await FindAssetIdAsync("node:1002");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai-assist/infrastructure/{assetId}/accessibility-candidates",
            new AccessibilityAiInferenceRequest
            {
                ObservationText = "Raised kerb, no ramp, narrow crossing, gravel edge and uneven broken pavement.",
                IncludeDraftVerification = true,
                Photos =
                [
                    new AccessibilityPhotoInput
                    {
                        Source = "field_photo",
                        Url = "https://example.com/crossing-raised-kerb.jpg",
                        Caption = "Raised kerb at crossing"
                    }
                ]
            });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AccessibilityAiInferenceResult>();
        Assert.NotNull(result);
        Assert.False(result!.ForRouteDecision);
        Assert.Equal("local-rules", result.Provider);
        Assert.Contains(result.Guardrails, guardrail => guardrail.Contains("cannot change routing graph edge costs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "curb_ramp" && candidate.Value == "false");
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "width_metres");
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "surface" && candidate.Value == "gravel");
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "photos");
        Assert.All(result.AttributeCandidates, candidate => Assert.False(candidate.CanAutoApply));
        Assert.NotNull(result.DraftVerification);
        Assert.Equal("ai_candidate_review", result.DraftVerification!.Source);
        Assert.Equal("gravel", result.DraftVerification.Path!.Surface);
        Assert.False(result.DraftVerification.Path.HasCurbRamp);
        Assert.Single(result.DraftVerification.Photos);
    }

    [Fact]
    public async Task AiAssist_HazardPhotoAnalysis_Queues_Nearest_Asset_Verification()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        await _factory.ImportOsmAsync(client);
        var asset = await FindAssetCentroidAsync("node:1002");

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/hazards",
            new
            {
                type = "missing_curb_ramp",
                description = "Raised kerb, no ramp, narrow pavement, uneven concrete surface.",
                photoUrl = "/api/v1/hazards/photos/hazard-review.jpg",
                location = new { x = asset.Longitude, y = asset.Latitude }
            });
        createResponse.EnsureSuccessStatusCode();
        await using var createdStream = await createResponse.Content.ReadAsStreamAsync();
        using var createdJson = await JsonDocument.ParseAsync(createdStream);
        var hazardId = createdJson.RootElement.GetProperty("id").GetGuid();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/ai-assist/hazards/{hazardId}/photo-analysis",
            new
            {
                photoUrl = "/api/v1/hazards/photos/hazard-review.jpg",
                observationText = "Wheelchair cannot cross because there is no dropped kerb.",
                includeDraftVerification = true,
                submitForReview = true,
                maxAssetDistanceMetres = 80
            });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<HazardPhotoAiAnalysisResult>();
        Assert.NotNull(result);
        Assert.False(result!.ForRouteDecision);
        Assert.Equal(asset.Id, result.LinkedInfrastructureAssetId);
        Assert.Equal("queued_for_review", result.ReviewStatus);
        Assert.NotNull(result.ReviewSubmission);
        Assert.Equal(AccessibilityVerificationStatus.Pending, result.ReviewSubmission!.Status);
        Assert.Contains(result.ReviewSubmission.PhotoUrls, url => url.EndsWith("/api/v1/hazards/photos/hazard-review.jpg", StringComparison.Ordinal));
        Assert.Contains(result.AttributeCandidates, candidate => candidate.Attribute == "curb_ramp" && candidate.Value == "false");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queued = await dbContext.AccessibilityVerificationSubmissions
            .CountAsync(submission => submission.InfrastructureAssetId == asset.Id
                                      && submission.Source == "ai_hazard_photo_review");
        Assert.True(queued >= 1);
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

    private async Task<(long Id, double Latitude, double Longitude)> FindAssetCentroidAsync(string sourceRecordId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await dbContext.InfrastructureAssets
            .Where(candidate => candidate.SourceRecordId == sourceRecordId)
            .SingleAsync();
        var centroid = asset.Geometry.Centroid.Coordinate;
        return (asset.Id, centroid.Y, centroid.X);
    }

    private async Task<InfrastructureAccessibilityProfile> GetProfileAsync(long assetId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var asset = await dbContext.InfrastructureAssets.SingleAsync(candidate => candidate.Id == assetId);
        return AccessibilityProfileMapper.Parse(asset.AccessibilityProfile);
    }
}
