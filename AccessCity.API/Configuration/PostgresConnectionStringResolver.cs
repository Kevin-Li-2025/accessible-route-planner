using Npgsql;

namespace AccessCity.API.Configuration;

public static class PostgresConnectionStringResolver
{
    public static string Resolve(IConfiguration configuration)
    {
        var databaseUrl = configuration["DATABASE_URL"]
            ?? configuration[$"{PostgresOptions.SectionName}:DatabaseUrl"]
            ?? Environment.GetEnvironmentVariable("DATABASE_URL");

        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            return FromDatabaseUrl(databaseUrl);
        }

        return configuration[$"{PostgresOptions.SectionName}:ConnectionString"]
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("No PostgreSQL connection string or DATABASE_URL is configured.");
    }

    public static string? GetPrimarySearchPath(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var searchPath = builder.SearchPath;
        if (string.IsNullOrWhiteSpace(searchPath))
        {
            return null;
        }

        return searchPath
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
    }

    private static string FromDatabaseUrl(string databaseUrl)
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.Trim('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            SslMode = GetSslMode(uri)
        };

        var queryParams = ParseQuery(uri.Query);

        foreach (var (key, value) in queryParams)
        {
            switch (key.ToLowerInvariant())
            {
                case "sslmode":
                    if (Enum.TryParse<Npgsql.SslMode>(value, ignoreCase: true, out var sslMode))
                    {
                        builder.SslMode = sslMode;
                    }
                    break;
                case "search_path":
                case "searchpath":
                    builder.SearchPath = value;
                    break;
                case "pooling":
                    if (bool.TryParse(value, out var pooling))
                    {
                        builder.Pooling = pooling;
                    }
                    break;
                default:
                    break;
            }
        }

        return builder.ConnectionString;
    }

    private static Npgsql.SslMode GetSslMode(Uri uri)
    {
        var queryParams = ParseQuery(uri.Query);
        if (queryParams.TryGetValue("sslmode", out var value) &&
            Enum.TryParse<Npgsql.SslMode>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return uri.Scheme.Equals("postgres", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
                ? Npgsql.SslMode.Prefer
                : Npgsql.SslMode.Disable;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }
}
