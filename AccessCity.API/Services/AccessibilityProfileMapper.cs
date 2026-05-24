using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AccessCity.API.Models;

namespace AccessCity.API.Services;

public static class AccessibilityProfileMapper
{
    public const string SchemaVersion = "accessibility-profile.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] ImportantTagKeys =
    [
        "access",
        "amenity",
        "automatic_door",
        "barrier",
        "changing_table",
        "check_date",
        "crossing",
        "door:width",
        "entrance",
        "footway",
        "grab_bar",
        "highway",
        "image",
        "incline",
        "kerb",
        "kerb:height",
        "lit",
        "mapillary",
        "ramp",
        "sloped_curb",
        "smoothness",
        "surface",
        "tactile_paving",
        "toilets:door:width",
        "toilets:grab_bar",
        "toilets:wheelchair",
        "wheelchair",
        "width",
        "wikimedia_commons"
    ];

    public static InfrastructureAccessibilityProfile BuildFromOsmTags(
        IReadOnlyDictionary<string, string> tags,
        string assetType,
        string sourceSystem,
        string? sourceRecordId,
        DateTime? observedAtUtc,
        DateTime? generatedAtUtc = null)
    {
        var normalizedTags = NormalizeTags(tags);
        var generatedAt = generatedAtUtc ?? DateTime.UtcNow;
        var lastVerifiedAt = GetLastVerifiedAt(normalizedTags, observedAtUtc);
        var path = BuildPath(normalizedTags);
        var entrances = BuildEntrances(normalizedTags, assetType, path);
        var restrooms = BuildRestrooms(normalizedTags, assetType);
        var photos = BuildPhotos(normalizedTags, lastVerifiedAt);
        var missingFields = BuildMissingFields(normalizedTags, assetType, lastVerifiedAt);
        var confidence = ComputeConfidence(normalizedTags, missingFields, photos.Count, lastVerifiedAt, generatedAt);

        return new InfrastructureAccessibilityProfile
        {
            SchemaVersion = SchemaVersion,
            SourceSystem = sourceSystem,
            SourceRecordId = sourceRecordId,
            ProfileGeneratedAtUtc = generatedAt,
            LastVerifiedAtUtc = lastVerifiedAt,
            VerificationStatus = ResolveVerificationStatus(confidence, lastVerifiedAt, generatedAt),
            Confidence = confidence,
            Path = path,
            Entrances = entrances,
            Restrooms = restrooms,
            Photos = photos,
            MissingFields = missingFields,
            EvidenceTags = BuildEvidenceTags(normalizedTags),
            RawTagCount = normalizedTags.Count
        };
    }

    public static JsonDocument ToJsonDocument(InfrastructureAccessibilityProfile profile) =>
        JsonSerializer.SerializeToDocument(profile, JsonOptions);

    public static InfrastructureAccessibilityProfile Parse(JsonDocument? document)
    {
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new InfrastructureAccessibilityProfile();
        }

        return document.RootElement.Deserialize<InfrastructureAccessibilityProfile>(JsonOptions)
            ?? new InfrastructureAccessibilityProfile();
    }

    private static AccessibilityPathAttributes BuildPath(IReadOnlyDictionary<string, string> tags)
    {
        var wheelchair = NormalizeValue(GetFirstTag(tags, "wheelchair", "wheelchair:access"));
        var kerbHeight = ParseKerbHeight(tags);
        var hasStairs = IsStairs(tags);
        var hasBarrier = IsBlockingBarrier(tags, kerbHeight);
        var curbRamp = ParseCurbRamp(tags, kerbHeight);

        return new AccessibilityPathAttributes
        {
            Surface = NormalizeValue(GetFirstTag(tags, "surface", "sidewalk:surface")),
            Smoothness = NormalizeValue(GetFirstTag(tags, "smoothness", "sidewalk:smoothness")),
            WidthMetres = ParseMetres(GetFirstTag(tags, "width", "sidewalk:width", "est_width")),
            KerbHeightMetres = kerbHeight,
            InclinePercent = ParseInclinePercent(GetFirstTag(tags, "incline", "ramp:incline")),
            InclineText = NormalizeValue(GetFirstTag(tags, "incline", "ramp:incline")),
            HasTactilePaving = ParseBool(GetFirstTag(tags, "tactile_paving", "sidewalk:tactile_paving")),
            HasCurbRamp = curbRamp,
            HasStepFreeAccess = ParseStepFreeAccess(tags, wheelchair, hasStairs, hasBarrier, kerbHeight),
            HasStairs = hasStairs,
            HasBarrier = hasBarrier,
            WheelchairAccess = wheelchair,
            Lighting = NormalizeValue(GetFirstTag(tags, "lit")),
            CrossingType = NormalizeValue(GetFirstTag(tags, "crossing", "crossing:island", "footway")),
            Access = NormalizeValue(GetFirstTag(tags, "access", "foot"))
        };
    }

    private static List<AccessibilityEntrance> BuildEntrances(
        IReadOnlyDictionary<string, string> tags,
        string assetType,
        AccessibilityPathAttributes path)
    {
        if (!HasAnyTag(tags, "entrance", "door:width", "entrance:width", "automatic_door", "ramp", "step_count", "step:height") &&
            !assetType.StartsWith("amenity:", StringComparison.OrdinalIgnoreCase) &&
            !assetType.StartsWith("public_transport:", StringComparison.OrdinalIgnoreCase) &&
            !assetType.StartsWith("railway:", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(assetType, "highway:elevator", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            new AccessibilityEntrance
            {
                Name = GetFirstTag(tags, "name", "entrance:name"),
                EntranceType = NormalizeValue(GetFirstTag(tags, "entrance", "highway")),
                StepFree = path.HasStepFreeAccess,
                HasRamp = ParseBool(GetFirstTag(tags, "ramp", "wheelchair:ramp")),
                DoorWidthMetres = ParseMetres(GetFirstTag(tags, "door:width", "entrance:width", "width")),
                AutomaticDoor = ParseBool(GetFirstTag(tags, "automatic_door")),
                StepHeightMetres = ParseMetres(GetFirstTag(tags, "step:height", "kerb:height"))
            }
        ];
    }

    private static List<AccessibilityRestroom> BuildRestrooms(IReadOnlyDictionary<string, string> tags, string assetType)
    {
        if (!string.Equals(tags.GetValueOrDefault("amenity"), "toilets", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(assetType, "amenity:toilets", StringComparison.OrdinalIgnoreCase) &&
            !HasAnyTag(tags, "toilets:wheelchair", "toilets:door:width", "toilets:grab_bar", "toilets:grab_rails"))
        {
            return [];
        }

        return
        [
            new AccessibilityRestroom
            {
                WheelchairAccessible = ParseBool(GetFirstTag(tags, "toilets:wheelchair", "wheelchair")),
                HasGrabBars = ParseBool(GetFirstTag(tags, "toilets:grab_bar", "toilets:grab_rails", "grab_bar")),
                DoorWidthMetres = ParseMetres(GetFirstTag(tags, "toilets:door:width", "door:width")),
                TurningSpaceMetres = ParseMetres(GetFirstTag(tags, "toilets:turning_space", "toilets:turning_circle")),
                HasChangingTable = ParseBool(GetFirstTag(tags, "changing_table", "toilets:changing_table")),
                RequiresKey = ParseRequiresKey(tags),
                GenderAccess = BuildGenderAccess(tags)
            }
        ];
    }

    private static List<AccessibilityPhoto> BuildPhotos(
        IReadOnlyDictionary<string, string> tags,
        DateTime? lastVerifiedAt)
    {
        var photos = new List<AccessibilityPhoto>();
        AddPhoto(photos, tags, "image", "osm-image", lastVerifiedAt);
        AddPhoto(photos, tags, "wikimedia_commons", "wikimedia-commons", lastVerifiedAt);

        var mapillary = GetFirstTag(tags, "mapillary", "mapillary:image");
        if (!string.IsNullOrWhiteSpace(mapillary))
        {
            photos.Add(new AccessibilityPhoto
            {
                Source = "mapillary",
                Url = mapillary.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? mapillary
                    : $"https://www.mapillary.com/app/?pKey={Uri.EscapeDataString(mapillary)}",
                Caption = GetFirstTag(tags, "description", "note"),
                TakenAtUtc = lastVerifiedAt,
                VerificationStatus = lastVerifiedAt.HasValue ? "osm-tagged" : "unverified"
            });
        }

        return photos;
    }

    private static List<string> BuildMissingFields(
        IReadOnlyDictionary<string, string> tags,
        string assetType,
        DateTime? lastVerifiedAt)
    {
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        if (lastVerifiedAt is null)
        {
            missing.Add("last_verified_at");
        }

        if (IsPedestrianAsset(tags, assetType))
        {
            AddMissingIfAbsent(missing, tags, "surface", "sidewalk:surface");
            AddMissingIfAbsent(missing, tags, "smoothness", "sidewalk:smoothness");
            AddMissingIfAbsent(missing, tags, "width_metres", "width", "sidewalk:width", "est_width");
            AddMissingIfAbsent(missing, tags, "kerb", "kerb:height", "sidewalk:kerb", "sloped_curb");
            AddMissingIfAbsent(missing, tags, "tactile_paving", "sidewalk:tactile_paving");
            AddMissingIfAbsent(missing, tags, "incline_percent", "incline", "ramp:incline");
        }

        if (IsEntranceAsset(tags, assetType))
        {
            AddMissingIfAbsent(missing, tags, "entrance", "entrance");
            AddMissingIfAbsent(missing, tags, "door_width_metres", "door:width", "entrance:width");
            AddMissingIfAbsent(missing, tags, "wheelchair_access", "wheelchair");
        }

        if (IsRestroomAsset(tags, assetType))
        {
            AddMissingIfAbsent(missing, tags, "toilets_wheelchair_access", "toilets:wheelchair", "wheelchair");
            AddMissingIfAbsent(missing, tags, "toilets_door_width_metres", "toilets:door:width", "door:width");
            AddMissingIfAbsent(missing, tags, "toilets_grab_bars", "toilets:grab_bar", "toilets:grab_rails", "grab_bar");
            AddMissingIfAbsent(missing, tags, "changing_table", "changing_table", "toilets:changing_table");
        }

        return missing.ToList();
    }

    private static double ComputeConfidence(
        IReadOnlyDictionary<string, string> tags,
        IReadOnlyCollection<string> missingFields,
        int photoCount,
        DateTime? lastVerifiedAt,
        DateTime generatedAt)
    {
        var targetCount = Math.Max(ImportantTagKeys.Count(tags.ContainsKey), missingFields.Count + ImportantTagKeys.Count(tags.ContainsKey));
        var knownRatio = targetCount == 0 ? 0.0 : Math.Clamp((targetCount - missingFields.Count) / (double)targetCount, 0.0, 1.0);
        var freshnessBoost = lastVerifiedAt switch
        {
            null => 0.0,
            var verified when generatedAt - verified.Value <= TimeSpan.FromDays(365) => 0.20,
            var verified when generatedAt - verified.Value <= TimeSpan.FromDays(1095) => 0.10,
            _ => 0.04
        };
        var photoBoost = photoCount > 0 ? 0.10 : 0.0;
        var rawTagBoost = Math.Min(0.10, tags.Count / 100.0);

        return Math.Round(Math.Clamp(0.10 + (knownRatio * 0.65) + freshnessBoost + photoBoost + rawTagBoost, 0.05, 0.99), 3);
    }

    private static string ResolveVerificationStatus(double confidence, DateTime? lastVerifiedAt, DateTime generatedAt)
    {
        if (lastVerifiedAt.HasValue && generatedAt - lastVerifiedAt.Value > TimeSpan.FromDays(1095))
        {
            return "stale";
        }

        if (lastVerifiedAt.HasValue && confidence >= 0.75)
        {
            return "verified";
        }

        return confidence >= 0.40 ? "partial" : "unverified";
    }

    private static Dictionary<string, string> NormalizeTags(IReadOnlyDictionary<string, string> tags)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in tags)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                normalized[key.Trim()] = value.Trim();
            }
        }

        return normalized;
    }

    private static Dictionary<string, string> BuildEvidenceTags(IReadOnlyDictionary<string, string> tags)
    {
        var evidence = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in ImportantTagKeys)
        {
            if (tags.TryGetValue(key, out var value))
            {
                evidence[key] = value;
            }
        }

        return evidence;
    }

    private static DateTime? GetLastVerifiedAt(IReadOnlyDictionary<string, string> tags, DateTime? observedAtUtc)
    {
        var dateTag = GetFirstTag(tags, "check_date", "survey:date", "lastcheck", "last_checked", "source:date");
        return ParseDate(dateTag) ?? observedAtUtc;
    }

    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        string[] formats = ["yyyy-MM-dd", "yyyy-MM", "yyyy"];
        if (DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var exact))
        {
            return DateTime.SpecifyKind(exact, DateTimeKind.Utc);
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static void AddPhoto(
        List<AccessibilityPhoto> photos,
        IReadOnlyDictionary<string, string> tags,
        string key,
        string source,
        DateTime? lastVerifiedAt)
    {
        var url = GetFirstTag(tags, key);
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        photos.Add(new AccessibilityPhoto
        {
            Source = source,
            Url = url,
            Caption = GetFirstTag(tags, "description", "note"),
            TakenAtUtc = lastVerifiedAt,
            VerificationStatus = lastVerifiedAt.HasValue ? "osm-tagged" : "unverified"
        });
    }

    private static bool? ParseStepFreeAccess(
        IReadOnlyDictionary<string, string> tags,
        string? wheelchair,
        bool hasStairs,
        bool hasBarrier,
        double? kerbHeight)
    {
        var wheelchairAccess = ParseBool(wheelchair);
        if (wheelchairAccess.HasValue)
        {
            return wheelchairAccess.Value;
        }

        if (hasStairs || hasBarrier || kerbHeight > 0.03)
        {
            return false;
        }

        var stepCount = GetFirstTag(tags, "step_count", "steps");
        if (int.TryParse(stepCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return count == 0;
        }

        return null;
    }

    private static bool? ParseCurbRamp(IReadOnlyDictionary<string, string> tags, double? kerbHeight)
    {
        var slopedCurb = GetFirstTag(tags, "sloped_curb", "sidewalk:sloped_curb");
        var parsed = ParseBool(slopedCurb);
        if (parsed.HasValue)
        {
            return parsed.Value;
        }

        var kerb = NormalizeValue(GetFirstTag(tags, "kerb", "sidewalk:kerb", "sidewalk:left:kerb", "sidewalk:right:kerb"));
        if (kerb is "flush" or "lowered" or "rolled" or "no")
        {
            return true;
        }

        if (kerb is "raised")
        {
            return false;
        }

        return kerbHeight > 0.03 ? false : null;
    }

    private static double? ParseKerbHeight(IReadOnlyDictionary<string, string> tags)
    {
        var explicitHeight = ParseMetres(GetFirstTag(
            tags,
            "kerb:height",
            "sidewalk:kerb:height",
            "sidewalk:left:kerb:height",
            "sidewalk:right:kerb:height",
            "sloped_curb:height"));
        if (explicitHeight.HasValue)
        {
            return explicitHeight.Value;
        }

        var kerb = NormalizeValue(GetFirstTag(tags, "kerb", "sidewalk:kerb", "sidewalk:left:kerb", "sidewalk:right:kerb"));
        return kerb switch
        {
            "flush" or "lowered" or "no" => 0.0,
            "rolled" => 0.03,
            "raised" => 0.10,
            _ => string.Equals(tags.GetValueOrDefault("barrier"), "kerb", StringComparison.OrdinalIgnoreCase) ? 0.10 : null
        };
    }

    private static bool IsBlockingBarrier(IReadOnlyDictionary<string, string> tags, double? kerbHeight)
    {
        var barrier = NormalizeValue(tags.GetValueOrDefault("barrier"));
        if (string.IsNullOrWhiteSpace(barrier))
        {
            return false;
        }

        if (barrier == "kerb")
        {
            return kerbHeight.GetValueOrDefault(0.10) > 0.05;
        }

        return barrier is "wall" or "fence" or "gate" or "stile" or "turnstile" or "cycle_barrier" or "block" or "chain";
    }

    private static bool IsStairs(IReadOnlyDictionary<string, string> tags) =>
        string.Equals(tags.GetValueOrDefault("highway"), "steps", StringComparison.OrdinalIgnoreCase)
        || tags.ContainsKey("step_count")
        || tags.ContainsKey("steps");

    private static bool? ParseRequiresKey(IReadOnlyDictionary<string, string> tags)
    {
        var access = NormalizeValue(GetFirstTag(tags, "toilets:access", "access"));
        if (access is "key" or "customers" or "private")
        {
            return true;
        }

        var fee = ParseBool(GetFirstTag(tags, "fee", "toilets:fee"));
        return fee == true ? true : null;
    }

    private static string? BuildGenderAccess(IReadOnlyDictionary<string, string> tags)
    {
        if (ParseBool(GetFirstTag(tags, "unisex", "toilets:unisex")) == true)
        {
            return "unisex";
        }

        var parts = new List<string>();
        if (ParseBool(GetFirstTag(tags, "male", "toilets:male")) == true)
        {
            parts.Add("male");
        }

        if (ParseBool(GetFirstTag(tags, "female", "toilets:female")) == true)
        {
            parts.Add("female");
        }

        return parts.Count == 0 ? null : string.Join(";", parts);
    }

    private static bool IsPedestrianAsset(IReadOnlyDictionary<string, string> tags, string assetType) =>
        assetType.StartsWith("highway:", StringComparison.OrdinalIgnoreCase)
        || assetType.StartsWith("barrier:", StringComparison.OrdinalIgnoreCase)
        || HasAnyTag(tags, "highway", "footway", "crossing", "kerb", "sidewalk", "surface", "width", "incline", "tactile_paving");

    private static bool IsEntranceAsset(IReadOnlyDictionary<string, string> tags, string assetType) =>
        string.Equals(assetType, "highway:elevator", StringComparison.OrdinalIgnoreCase)
        || assetType.StartsWith("amenity:", StringComparison.OrdinalIgnoreCase)
        || assetType.StartsWith("public_transport:", StringComparison.OrdinalIgnoreCase)
        || assetType.StartsWith("railway:", StringComparison.OrdinalIgnoreCase)
        || HasAnyTag(tags, "entrance", "door:width", "automatic_door", "ramp");

    private static bool IsRestroomAsset(IReadOnlyDictionary<string, string> tags, string assetType) =>
        string.Equals(assetType, "amenity:toilets", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tags.GetValueOrDefault("amenity"), "toilets", StringComparison.OrdinalIgnoreCase)
        || HasAnyTag(tags, "toilets:wheelchair", "toilets:door:width", "toilets:grab_bar", "toilets:grab_rails");

    private static void AddMissingIfAbsent(
        ISet<string> missing,
        IReadOnlyDictionary<string, string> tags,
        string fieldName,
        params string[] tagKeys)
    {
        if (!HasAnyTag(tags, tagKeys))
        {
            missing.Add(fieldName);
        }
    }

    private static bool HasAnyTag(IReadOnlyDictionary<string, string> tags, params string[] keys) =>
        keys.Any(key => tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value));

    private static string? GetFirstTag(IReadOnlyDictionary<string, string> tags, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? NormalizeValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static bool? ParseBool(string? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            "yes" or "true" or "1" or "designated" or "permissive" => true,
            "no" or "false" or "0" or "none" => false,
            _ => null
        };
    }

    private static double? ParseInclinePercent(string? raw)
    {
        var normalized = NormalizeValue(raw);
        if (string.IsNullOrWhiteSpace(normalized) || normalized is "up" or "down")
        {
            return null;
        }

        normalized = normalized.Replace("%", "", StringComparison.Ordinal).Trim();
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static double? ParseMetres(string? raw)
    {
        var normalized = NormalizeValue(raw);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Contains(';', StringComparison.Ordinal))
        {
            normalized = normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        }

        normalized = normalized.Replace(',', '.');
        var multiplier = 1.0;
        if (normalized.Contains("mm", StringComparison.Ordinal))
        {
            multiplier = 0.001;
            normalized = normalized.Replace("millimeters", "", StringComparison.Ordinal)
                .Replace("millimetres", "", StringComparison.Ordinal)
                .Replace("millimeter", "", StringComparison.Ordinal)
                .Replace("millimetre", "", StringComparison.Ordinal)
                .Replace("mm", "", StringComparison.Ordinal);
        }
        else if (normalized.Contains("cm", StringComparison.Ordinal))
        {
            multiplier = 0.01;
            normalized = normalized.Replace("centimeters", "", StringComparison.Ordinal)
                .Replace("centimetres", "", StringComparison.Ordinal)
                .Replace("centimeter", "", StringComparison.Ordinal)
                .Replace("centimetre", "", StringComparison.Ordinal)
                .Replace("cm", "", StringComparison.Ordinal);
        }
        else
        {
            normalized = normalized.Replace("meters", "", StringComparison.Ordinal)
                .Replace("metres", "", StringComparison.Ordinal)
                .Replace("meter", "", StringComparison.Ordinal)
                .Replace("metre", "", StringComparison.Ordinal)
                .Replace("m", "", StringComparison.Ordinal);
        }

        return double.TryParse(normalized.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var metres) && metres >= 0
            ? metres * multiplier
            : null;
    }
}
