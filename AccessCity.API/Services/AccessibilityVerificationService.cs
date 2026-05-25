using System.Text.Json;
using System.Text.Json.Serialization;
using AccessCity.API.Data;
using AccessCity.API.Models;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Services;

public interface IAccessibilityVerificationService
{
    Task<InfrastructureAccessibilityProfile?> GetProfileAsync(long assetId, CancellationToken cancellationToken);

    Task<AccessibilityVerificationResponse?> SubmitAsync(
        long assetId,
        AccessibilityVerificationRequest request,
        string submittedByUserId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AccessibilityVerificationResponse>> ListAsync(
        long assetId,
        CancellationToken cancellationToken);

    Task<long?> FindNearestAssetIdAsync(
        double latitude,
        double longitude,
        double maxDistanceMetres,
        CancellationToken cancellationToken);

    Task<AccessibilityVerificationResponse?> ApplyAsync(
        Guid submissionId,
        string reviewedByUserId,
        string? notes,
        CancellationToken cancellationToken);

    Task<AccessibilityVerificationResponse?> RejectAsync(
        Guid submissionId,
        string reviewedByUserId,
        string? notes,
        CancellationToken cancellationToken);
}

public sealed class AccessibilityVerificationService : IAccessibilityVerificationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppDbContext _dbContext;

    public AccessibilityVerificationService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<InfrastructureAccessibilityProfile?> GetProfileAsync(
        long assetId,
        CancellationToken cancellationToken)
    {
        var asset = await _dbContext.InfrastructureAssets
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == assetId, cancellationToken);

        return asset is null ? null : AccessibilityProfileMapper.Parse(asset.AccessibilityProfile);
    }

    public async Task<AccessibilityVerificationResponse?> SubmitAsync(
        long assetId,
        AccessibilityVerificationRequest request,
        string submittedByUserId,
        CancellationToken cancellationToken)
    {
        var assetExists = await _dbContext.InfrastructureAssets
            .AsNoTracking()
            .AnyAsync(asset => asset.Id == assetId, cancellationToken);
        if (!assetExists)
        {
            return null;
        }

        var normalized = NormalizeRequest(request);
        var submission = new AccessibilityVerificationSubmission
        {
            InfrastructureAssetId = assetId,
            SubmittedByUserId = string.IsNullOrWhiteSpace(submittedByUserId) ? "anonymous" : submittedByUserId,
            Source = normalized.Source,
            Status = AccessibilityVerificationStatus.Pending,
            SubmittedAtUtc = DateTime.UtcNow,
            ObservedAtUtc = normalized.ObservedAtUtc?.ToUniversalTime(),
            Notes = NormalizeNotes(normalized.Notes),
            Confidence = ComputeSubmissionConfidence(normalized),
            AttributeUpdates = JsonSerializer.SerializeToDocument(normalized, JsonOptions),
            PhotoUrls = JsonSerializer.SerializeToDocument(
                normalized.Photos.Select(photo => photo.Url).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                JsonOptions)
        };

        _dbContext.AccessibilityVerificationSubmissions.Add(submission);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(submission);
    }

    public async Task<IReadOnlyList<AccessibilityVerificationResponse>> ListAsync(
        long assetId,
        CancellationToken cancellationToken)
    {
        var submissions = await _dbContext.AccessibilityVerificationSubmissions
            .AsNoTracking()
            .Where(submission => submission.InfrastructureAssetId == assetId)
            .OrderByDescending(submission => submission.SubmittedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        return submissions.Select(submission => ToResponse(submission)).ToList();
    }

    public async Task<long?> FindNearestAssetIdAsync(
        double latitude,
        double longitude,
        double maxDistanceMetres,
        CancellationToken cancellationToken)
    {
        if (!IsValidLatitude(latitude) || !IsValidLongitude(longitude))
        {
            return null;
        }

        var cappedDistance = Math.Clamp(maxDistanceMetres, 1, 250);
        var point = new Point(longitude, latitude) { SRID = 4326 };
        var envelope = CreateSearchEnvelope(latitude, longitude, cappedDistance);
        var isRelational = _dbContext.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory";

        IQueryable<InfrastructureAsset> query = _dbContext.InfrastructureAssets.AsNoTracking();
        if (isRelational)
        {
            query = query.Where(asset => asset.Geometry.Intersects(envelope));
        }

        var candidates = await query
            .OrderByDescending(asset => asset.LastObservedAt ?? asset.UpdatedAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        return candidates
            .Where(asset => asset.Geometry is not null)
            .Where(asset => !isRelational || asset.Geometry.Intersects(envelope))
            .Select(asset =>
            {
                var centroid = asset.Geometry.Centroid.Coordinate;
                var distance = DistanceMetres(latitude, longitude, centroid.Y, centroid.X);
                return new { asset.Id, Distance = distance };
            })
            .Where(asset => asset.Distance <= cappedDistance)
            .OrderBy(asset => asset.Distance)
            .ThenBy(asset => asset.Id)
            .Select(asset => (long?)asset.Id)
            .FirstOrDefault();
    }

    public async Task<AccessibilityVerificationResponse?> ApplyAsync(
        Guid submissionId,
        string reviewedByUserId,
        string? notes,
        CancellationToken cancellationToken)
    {
        var submission = await _dbContext.AccessibilityVerificationSubmissions
            .SingleOrDefaultAsync(candidate => candidate.Id == submissionId, cancellationToken);
        if (submission is null)
        {
            return null;
        }

        if (submission.Status == AccessibilityVerificationStatus.Applied)
        {
            var profile = await GetProfileAsync(submission.InfrastructureAssetId, cancellationToken);
            return ToResponse(submission, profile);
        }

        if (submission.Status == AccessibilityVerificationStatus.Rejected)
        {
            return ToResponse(submission);
        }

        var asset = await _dbContext.InfrastructureAssets
            .SingleOrDefaultAsync(candidate => candidate.Id == submission.InfrastructureAssetId, cancellationToken);
        if (asset is null)
        {
            return null;
        }

        var request = DeserializeRequest(submission.AttributeUpdates);
        var currentProfile = AccessibilityProfileMapper.Parse(asset.AccessibilityProfile);
        var updatedProfile = MergeProfile(currentProfile, request, submission.Source, submission.ObservedAtUtc, DateTime.UtcNow);

        asset.AccessibilityProfile = AccessibilityProfileMapper.ToJsonDocument(updatedProfile);
        asset.LastObservedAt = MaxDate(asset.LastObservedAt, submission.ObservedAtUtc);
        asset.UpdatedAt = DateTime.UtcNow;

        submission.Status = AccessibilityVerificationStatus.Applied;
        submission.ReviewedAtUtc = DateTime.UtcNow;
        submission.ReviewedByUserId = string.IsNullOrWhiteSpace(reviewedByUserId) ? null : reviewedByUserId;
        submission.AppliedAtUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(notes))
        {
            submission.Notes = string.IsNullOrWhiteSpace(submission.Notes)
                ? notes.Trim()
                : $"{submission.Notes.Trim()} Review: {notes.Trim()}";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(submission, updatedProfile);
    }

    public async Task<AccessibilityVerificationResponse?> RejectAsync(
        Guid submissionId,
        string reviewedByUserId,
        string? notes,
        CancellationToken cancellationToken)
    {
        var submission = await _dbContext.AccessibilityVerificationSubmissions
            .SingleOrDefaultAsync(candidate => candidate.Id == submissionId, cancellationToken);
        if (submission is null)
        {
            return null;
        }

        if (submission.Status == AccessibilityVerificationStatus.Pending)
        {
            submission.Status = AccessibilityVerificationStatus.Rejected;
            submission.ReviewedAtUtc = DateTime.UtcNow;
            submission.ReviewedByUserId = string.IsNullOrWhiteSpace(reviewedByUserId) ? null : reviewedByUserId;
            if (!string.IsNullOrWhiteSpace(notes))
            {
                submission.Notes = string.IsNullOrWhiteSpace(submission.Notes)
                    ? notes.Trim()
                    : $"{submission.Notes.Trim()} Rejection: {notes.Trim()}";
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return ToResponse(submission);
    }

    private static AccessibilityVerificationRequest NormalizeRequest(AccessibilityVerificationRequest request)
    {
        var source = NormalizeToken(request.Source, "field_report");
        var photos = request.Photos
            .Where(photo => Uri.TryCreate(photo.Url, UriKind.Absolute, out var uri)
                            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Select(photo => new AccessibilityPhotoInput
            {
                Source = NormalizeToken(photo.Source, "field_photo"),
                Url = photo.Url.Trim(),
                Caption = NormalizeNotes(photo.Caption),
                TakenAtUtc = photo.TakenAtUtc?.ToUniversalTime()
            })
            .DistinctBy(photo => photo.Url, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        return new AccessibilityVerificationRequest
        {
            ObservedAtUtc = request.ObservedAtUtc?.ToUniversalTime(),
            Source = source,
            Notes = NormalizeNotes(request.Notes),
            Path = SanitizePath(request.Path),
            Entrance = SanitizeEntrance(request.Entrance),
            Restroom = SanitizeRestroom(request.Restroom),
            Photos = photos
        };
    }

    private static AccessibilityPathAttributes? SanitizePath(AccessibilityPathAttributes? path)
    {
        if (path is null)
        {
            return null;
        }

        return new AccessibilityPathAttributes
        {
            Surface = NormalizeTokenOrNull(path.Surface),
            Smoothness = NormalizeTokenOrNull(path.Smoothness),
            WidthMetres = Clamp(path.WidthMetres, 0.3, 10),
            KerbHeightMetres = Clamp(path.KerbHeightMetres, 0, 0.5),
            InclinePercent = Clamp(path.InclinePercent, -30, 30),
            InclineText = NormalizeTokenOrNull(path.InclineText),
            HasTactilePaving = path.HasTactilePaving,
            HasCurbRamp = path.HasCurbRamp,
            HasStepFreeAccess = path.HasStepFreeAccess,
            HasStairs = path.HasStairs,
            HasBarrier = path.HasBarrier,
            WheelchairAccess = NormalizeTokenOrNull(path.WheelchairAccess),
            Lighting = NormalizeTokenOrNull(path.Lighting),
            CrossingType = NormalizeTokenOrNull(path.CrossingType),
            Access = NormalizeTokenOrNull(path.Access)
        };
    }

    private static AccessibilityEntrance? SanitizeEntrance(AccessibilityEntrance? entrance)
    {
        if (entrance is null)
        {
            return null;
        }

        return new AccessibilityEntrance
        {
            Name = NormalizeNotes(entrance.Name),
            EntranceType = NormalizeTokenOrNull(entrance.EntranceType),
            StepFree = entrance.StepFree,
            HasRamp = entrance.HasRamp,
            DoorWidthMetres = Clamp(entrance.DoorWidthMetres, 0.3, 3),
            AutomaticDoor = entrance.AutomaticDoor,
            StepHeightMetres = Clamp(entrance.StepHeightMetres, 0, 0.5)
        };
    }

    private static AccessibilityRestroom? SanitizeRestroom(AccessibilityRestroom? restroom)
    {
        if (restroom is null)
        {
            return null;
        }

        return new AccessibilityRestroom
        {
            WheelchairAccessible = restroom.WheelchairAccessible,
            HasGrabBars = restroom.HasGrabBars,
            DoorWidthMetres = Clamp(restroom.DoorWidthMetres, 0.3, 3),
            TurningSpaceMetres = Clamp(restroom.TurningSpaceMetres, 0.5, 4),
            HasChangingTable = restroom.HasChangingTable,
            RequiresKey = restroom.RequiresKey,
            GenderAccess = NormalizeTokenOrNull(restroom.GenderAccess)
        };
    }

    private static InfrastructureAccessibilityProfile MergeProfile(
        InfrastructureAccessibilityProfile current,
        AccessibilityVerificationRequest request,
        string source,
        DateTime? observedAtUtc,
        DateTime nowUtc)
    {
        var updatedPath = MergePath(current.Path, request.Path);
        var updatedEntrances = MergeEntrance(current.Entrances, request.Entrance);
        var updatedRestrooms = MergeRestroom(current.Restrooms, request.Restroom);
        var updatedPhotos = MergePhotos(current.Photos, request.Photos, source, observedAtUtc);
        var lastVerifiedAt = MaxDate(current.LastVerifiedAtUtc, observedAtUtc) ?? nowUtc;
        var missingFields = ComputeMissingFields(updatedPath, updatedEntrances, updatedRestrooms, lastVerifiedAt);
        var confidence = ComputeProfileConfidence(updatedPath, updatedEntrances, updatedRestrooms, updatedPhotos.Count, missingFields.Count, lastVerifiedAt, nowUtc);

        return new InfrastructureAccessibilityProfile
        {
            SchemaVersion = current.SchemaVersion,
            SourceSystem = current.SourceSystem,
            SourceRecordId = current.SourceRecordId,
            ProfileGeneratedAtUtc = nowUtc,
            LastVerifiedAtUtc = lastVerifiedAt,
            VerificationStatus = confidence >= 0.75 ? "verified" : "partial",
            Confidence = Math.Max(current.Confidence, confidence),
            Path = updatedPath,
            Entrances = updatedEntrances,
            Restrooms = updatedRestrooms,
            Photos = updatedPhotos,
            MissingFields = missingFields,
            EvidenceTags = current.EvidenceTags,
            RawTagCount = current.RawTagCount
        };
    }

    private static Polygon CreateSearchEnvelope(double latitude, double longitude, double radiusMetres)
    {
        var latDelta = radiusMetres / 111_320d;
        var cosLat = Math.Cos(latitude * Math.PI / 180);
        var lngDelta = Math.Abs(cosLat) < 0.01
            ? latDelta
            : radiusMetres / (111_320d * Math.Abs(cosLat));
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        return factory.CreatePolygon(
        [
            new Coordinate(longitude - lngDelta, latitude - latDelta),
            new Coordinate(longitude + lngDelta, latitude - latDelta),
            new Coordinate(longitude + lngDelta, latitude + latDelta),
            new Coordinate(longitude - lngDelta, latitude + latDelta),
            new Coordinate(longitude - lngDelta, latitude - latDelta)
        ]);
    }

    private static bool IsValidLatitude(double latitude) =>
        !double.IsNaN(latitude) && !double.IsInfinity(latitude) && latitude is >= -90 and <= 90;

    private static bool IsValidLongitude(double longitude) =>
        !double.IsNaN(longitude) && !double.IsInfinity(longitude) && longitude is >= -180 and <= 180;

    private static double DistanceMetres(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadiusMetres = 6_371_000;
        var dLat = ToRadians(lat2 - lat1);
        var dLng = ToRadians(lng2 - lng1);
        var rLat1 = ToRadians(lat1);
        var rLat2 = ToRadians(lat2);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(rLat1) * Math.Cos(rLat2) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusMetres * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    private static AccessibilityPathAttributes MergePath(
        AccessibilityPathAttributes current,
        AccessibilityPathAttributes? update)
    {
        if (update is null)
        {
            return current;
        }

        return new AccessibilityPathAttributes
        {
            Surface = update.Surface ?? current.Surface,
            Smoothness = update.Smoothness ?? current.Smoothness,
            WidthMetres = update.WidthMetres ?? current.WidthMetres,
            KerbHeightMetres = update.KerbHeightMetres ?? current.KerbHeightMetres,
            InclinePercent = update.InclinePercent ?? current.InclinePercent,
            InclineText = update.InclineText ?? current.InclineText,
            HasTactilePaving = update.HasTactilePaving ?? current.HasTactilePaving,
            HasCurbRamp = update.HasCurbRamp ?? current.HasCurbRamp,
            HasStepFreeAccess = update.HasStepFreeAccess ?? current.HasStepFreeAccess,
            HasStairs = update.HasStairs ?? current.HasStairs,
            HasBarrier = update.HasBarrier ?? current.HasBarrier,
            WheelchairAccess = update.WheelchairAccess ?? current.WheelchairAccess,
            Lighting = update.Lighting ?? current.Lighting,
            CrossingType = update.CrossingType ?? current.CrossingType,
            Access = update.Access ?? current.Access
        };
    }

    private static List<AccessibilityEntrance> MergeEntrance(
        IReadOnlyList<AccessibilityEntrance> current,
        AccessibilityEntrance? update)
    {
        if (update is null)
        {
            return current.ToList();
        }

        var existing = current.FirstOrDefault() ?? new AccessibilityEntrance();
        var merged = new AccessibilityEntrance
        {
            Name = update.Name ?? existing.Name,
            EntranceType = update.EntranceType ?? existing.EntranceType,
            StepFree = update.StepFree ?? existing.StepFree,
            HasRamp = update.HasRamp ?? existing.HasRamp,
            DoorWidthMetres = update.DoorWidthMetres ?? existing.DoorWidthMetres,
            AutomaticDoor = update.AutomaticDoor ?? existing.AutomaticDoor,
            StepHeightMetres = update.StepHeightMetres ?? existing.StepHeightMetres
        };

        return [merged, .. current.Skip(1)];
    }

    private static List<AccessibilityRestroom> MergeRestroom(
        IReadOnlyList<AccessibilityRestroom> current,
        AccessibilityRestroom? update)
    {
        if (update is null)
        {
            return current.ToList();
        }

        var existing = current.FirstOrDefault() ?? new AccessibilityRestroom();
        var merged = new AccessibilityRestroom
        {
            WheelchairAccessible = update.WheelchairAccessible ?? existing.WheelchairAccessible,
            HasGrabBars = update.HasGrabBars ?? existing.HasGrabBars,
            DoorWidthMetres = update.DoorWidthMetres ?? existing.DoorWidthMetres,
            TurningSpaceMetres = update.TurningSpaceMetres ?? existing.TurningSpaceMetres,
            HasChangingTable = update.HasChangingTable ?? existing.HasChangingTable,
            RequiresKey = update.RequiresKey ?? existing.RequiresKey,
            GenderAccess = update.GenderAccess ?? existing.GenderAccess
        };

        return [merged, .. current.Skip(1)];
    }

    private static List<AccessibilityPhoto> MergePhotos(
        IReadOnlyList<AccessibilityPhoto> current,
        IReadOnlyList<AccessibilityPhotoInput> updates,
        string source,
        DateTime? observedAtUtc)
    {
        var photos = current.ToList();
        var seen = photos
            .Where(photo => !string.IsNullOrWhiteSpace(photo.Url))
            .Select(photo => photo.Url!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var update in updates)
        {
            if (!seen.Add(update.Url))
            {
                continue;
            }

            photos.Add(new AccessibilityPhoto
            {
                Source = string.IsNullOrWhiteSpace(update.Source) ? source : update.Source,
                Url = update.Url,
                Caption = update.Caption,
                TakenAtUtc = update.TakenAtUtc ?? observedAtUtc,
                VerificationStatus = "field-verified"
            });
        }

        return photos;
    }

    private static List<string> ComputeMissingFields(
        AccessibilityPathAttributes path,
        IReadOnlyList<AccessibilityEntrance> entrances,
        IReadOnlyList<AccessibilityRestroom> restrooms,
        DateTime? lastVerifiedAtUtc)
    {
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (lastVerifiedAtUtc is null)
        {
            missing.Add("last_verified_at");
        }

        AddIfNull(missing, "surface", path.Surface);
        AddIfNull(missing, "smoothness", path.Smoothness);
        AddIfNull(missing, "width_metres", path.WidthMetres);
        AddIfNull(missing, "kerb", path.KerbHeightMetres ?? (path.HasCurbRamp.HasValue ? 0 : null));
        AddIfNull(missing, "tactile_paving", path.HasTactilePaving);
        AddIfNull(missing, "incline_percent", path.InclinePercent ?? (path.InclineText is not null ? 0 : null));

        if (entrances.Count > 0)
        {
            var entrance = entrances[0];
            AddIfNull(missing, "door_width_metres", entrance.DoorWidthMetres);
            AddIfNull(missing, "wheelchair_access", entrance.StepFree);
        }

        if (restrooms.Count > 0)
        {
            var restroom = restrooms[0];
            AddIfNull(missing, "toilets_wheelchair_access", restroom.WheelchairAccessible);
            AddIfNull(missing, "toilets_door_width_metres", restroom.DoorWidthMetres);
            AddIfNull(missing, "toilets_grab_bars", restroom.HasGrabBars);
            AddIfNull(missing, "changing_table", restroom.HasChangingTable);
        }

        return missing.ToList();
    }

    private static void AddIfNull<T>(ISet<string> missing, string field, T? value)
    {
        if (value is null)
        {
            missing.Add(field);
        }
    }

    private static double ComputeProfileConfidence(
        AccessibilityPathAttributes path,
        IReadOnlyList<AccessibilityEntrance> entrances,
        IReadOnlyList<AccessibilityRestroom> restrooms,
        int photoCount,
        int missingCount,
        DateTime? lastVerifiedAt,
        DateTime nowUtc)
    {
        var knownSignals = 0;
        knownSignals += path.Surface is not null ? 1 : 0;
        knownSignals += path.Smoothness is not null ? 1 : 0;
        knownSignals += path.WidthMetres.HasValue ? 1 : 0;
        knownSignals += path.KerbHeightMetres.HasValue || path.HasCurbRamp.HasValue ? 1 : 0;
        knownSignals += path.HasTactilePaving.HasValue ? 1 : 0;
        knownSignals += path.InclinePercent.HasValue || path.InclineText is not null ? 1 : 0;
        knownSignals += entrances.Count > 0 ? 2 : 0;
        knownSignals += restrooms.Count > 0 ? 3 : 0;

        var denominator = knownSignals + missingCount;
        var completeness = denominator == 0 ? 0.0 : knownSignals / (double)denominator;
        var freshness = lastVerifiedAt.HasValue && nowUtc - lastVerifiedAt.Value <= TimeSpan.FromDays(365) ? 0.20 : 0.05;
        var photos = Math.Min(0.10, photoCount * 0.05);

        return Math.Round(Math.Clamp(0.15 + completeness * 0.65 + freshness + photos, 0.05, 0.99), 3);
    }

    private static double ComputeSubmissionConfidence(AccessibilityVerificationRequest request)
    {
        var signals = 0;
        signals += CountPathSignals(request.Path);
        signals += request.Entrance is null ? 0 : 2;
        signals += request.Restroom is null ? 0 : 3;
        signals += request.Photos.Count > 0 ? 2 : 0;
        signals += request.ObservedAtUtc.HasValue ? 1 : 0;

        return Math.Round(Math.Clamp(0.25 + signals * 0.055, 0.25, 0.95), 3);
    }

    private static int CountPathSignals(AccessibilityPathAttributes? path)
    {
        if (path is null)
        {
            return 0;
        }

        var count = 0;
        count += path.Surface is not null ? 1 : 0;
        count += path.Smoothness is not null ? 1 : 0;
        count += path.WidthMetres.HasValue ? 1 : 0;
        count += path.KerbHeightMetres.HasValue || path.HasCurbRamp.HasValue ? 1 : 0;
        count += path.InclinePercent.HasValue || path.InclineText is not null ? 1 : 0;
        count += path.HasTactilePaving.HasValue ? 1 : 0;
        count += path.HasStepFreeAccess.HasValue ? 1 : 0;
        return count;
    }

    private static AccessibilityVerificationRequest DeserializeRequest(JsonDocument document) =>
        document.RootElement.Deserialize<AccessibilityVerificationRequest>(JsonOptions) ?? new AccessibilityVerificationRequest();

    private static AccessibilityVerificationResponse ToResponse(
        AccessibilityVerificationSubmission submission,
        InfrastructureAccessibilityProfile? profile = null)
    {
        var request = DeserializeRequest(submission.AttributeUpdates);
        return new AccessibilityVerificationResponse
        {
            Id = submission.Id,
            InfrastructureAssetId = submission.InfrastructureAssetId,
            Status = submission.Status,
            Source = submission.Source,
            SubmittedAtUtc = submission.SubmittedAtUtc,
            ObservedAtUtc = submission.ObservedAtUtc,
            AppliedAtUtc = submission.AppliedAtUtc,
            Confidence = submission.Confidence,
            Notes = submission.Notes,
            UpdatedFields = ListUpdatedFields(request),
            PhotoUrls = request.Photos.Select(photo => photo.Url).ToList(),
            AccessibilityProfile = profile
        };
    }

    private static List<string> ListUpdatedFields(AccessibilityVerificationRequest request)
    {
        var fields = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request.Path is not null)
        {
            AddPresent(fields, "surface", request.Path.Surface);
            AddPresent(fields, "smoothness", request.Path.Smoothness);
            AddPresent(fields, "width_metres", request.Path.WidthMetres);
            AddPresent(fields, "kerb_height_metres", request.Path.KerbHeightMetres);
            AddPresent(fields, "curb_ramp", request.Path.HasCurbRamp);
            AddPresent(fields, "incline_percent", request.Path.InclinePercent);
            AddPresent(fields, "tactile_paving", request.Path.HasTactilePaving);
            AddPresent(fields, "step_free_access", request.Path.HasStepFreeAccess);
        }

        if (request.Entrance is not null)
        {
            AddPresent(fields, "entrance", request.Entrance.EntranceType);
            AddPresent(fields, "door_width_metres", request.Entrance.DoorWidthMetres);
            AddPresent(fields, "automatic_door", request.Entrance.AutomaticDoor);
        }

        if (request.Restroom is not null)
        {
            AddPresent(fields, "toilets_wheelchair_access", request.Restroom.WheelchairAccessible);
            AddPresent(fields, "toilets_door_width_metres", request.Restroom.DoorWidthMetres);
            AddPresent(fields, "toilets_grab_bars", request.Restroom.HasGrabBars);
            AddPresent(fields, "changing_table", request.Restroom.HasChangingTable);
        }

        if (request.Photos.Count > 0)
        {
            fields.Add("photos");
        }

        return fields.ToList();
    }

    private static void AddPresent<T>(ISet<string> fields, string field, T? value)
    {
        if (value is not null)
        {
            fields.Add(field);
        }
    }

    private static DateTime? MaxDate(DateTime? left, DateTime? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left.Value >= right.Value ? left.Value : right.Value;
    }

    private static double? Clamp(double? value, double min, double max) =>
        value.HasValue ? Math.Round(Math.Clamp(value.Value, min, max), 3) : null;

    private static string NormalizeToken(string? value, string fallback)
    {
        var normalized = NormalizeTokenOrNull(value);
        return normalized ?? fallback;
    }

    private static string? NormalizeTokenOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is ':' or '_' or '-' ? character : '_')
            .ToArray();
        var normalized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var normalized = string.Join(' ', notes.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 1000 ? normalized : normalized[..1000];
    }
}
