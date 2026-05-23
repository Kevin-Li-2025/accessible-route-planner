using System.Text.Json;
using System.Text.Json.Serialization;
using NetTopologySuite.Geometries;

namespace AccessCity.API.Serialization;

public sealed class CoordinateJsonConverter : JsonConverter<Coordinate>
{
    public override Coordinate? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        return reader.TokenType switch
        {
            JsonTokenType.StartObject => ReadObject(ref reader),
            JsonTokenType.StartArray => ReadArray(ref reader),
            _ => throw new JsonException("Coordinate must be an object with x/y or an array [x,y].")
        };
    }

    public override void Write(Utf8JsonWriter writer, Coordinate value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        EnsureFinite(value.X, "x");
        EnsureFinite(value.Y, "y");

        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteEndObject();
    }

    private static Coordinate ReadObject(ref Utf8JsonReader reader)
    {
        double? x = null;
        double? y = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (x is null || y is null)
                {
                    throw new JsonException("Coordinate requires both x/y values.");
                }

                return new Coordinate(x.Value, y.Value);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Invalid coordinate object.");
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("Invalid coordinate object.");
            }

            if (string.Equals(propertyName, "coordinates", StringComparison.OrdinalIgnoreCase))
            {
                var coordinate = ReadArray(ref reader);
                x = coordinate.X;
                y = coordinate.Y;
                continue;
            }

            if (string.Equals(propertyName, "type", StringComparison.OrdinalIgnoreCase))
            {
                reader.Skip();
                continue;
            }

            if (!IsCoordinateScalarProperty(propertyName))
            {
                reader.Skip();
                continue;
            }

            var value = ReadFiniteNumber(ref reader, propertyName ?? "coordinate");
            if (IsLongitudeProperty(propertyName))
            {
                x = value;
            }
            else if (IsLatitudeProperty(propertyName))
            {
                y = value;
            }
        }

        throw new JsonException("Invalid coordinate object.");
    }

    private static Coordinate ReadArray(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new JsonException("Invalid coordinate array.");
        }

        var x = ReadFiniteNumber(ref reader, "x");
        if (!reader.Read())
        {
            throw new JsonException("Invalid coordinate array.");
        }

        var y = ReadFiniteNumber(ref reader, "y");
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return new Coordinate(x, y);
            }
        }

        throw new JsonException("Invalid coordinate array.");
    }

    private static double ReadFiniteNumber(ref Utf8JsonReader reader, string fieldName)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var value))
        {
            throw new JsonException($"Coordinate field '{fieldName}' must be a number.");
        }

        EnsureFinite(value, fieldName);
        return value;
    }

    private static void EnsureFinite(double value, string fieldName)
    {
        if (!double.IsFinite(value))
        {
            throw new JsonException($"Coordinate field '{fieldName}' must be finite.");
        }
    }

    private static bool IsCoordinateScalarProperty(string? propertyName) =>
        IsLongitudeProperty(propertyName) || IsLatitudeProperty(propertyName);

    private static bool IsLongitudeProperty(string? propertyName) =>
        string.Equals(propertyName, "x", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "lon", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "lng", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "longitude", StringComparison.OrdinalIgnoreCase);

    private static bool IsLatitudeProperty(string? propertyName) =>
        string.Equals(propertyName, "y", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "lat", StringComparison.OrdinalIgnoreCase)
        || string.Equals(propertyName, "latitude", StringComparison.OrdinalIgnoreCase);
}
