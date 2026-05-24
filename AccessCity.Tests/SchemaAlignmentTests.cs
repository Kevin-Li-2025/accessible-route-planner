using Npgsql;

namespace AccessCity.Tests;

public class SchemaAlignmentTests : IClassFixture<AccessCityApiFactory>
{
    private readonly AccessCityApiFactory _factory;

    public SchemaAlignmentTests(AccessCityApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Startup_Migrations_Create_RefreshToken_Table_And_Hazard_PhotoUrl()
    {
        using var client = _factory.CreateClient();

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();

        Assert.True(await TableExistsAsync(connection, "refresh_token"));
        Assert.True(await ColumnExistsAsync(connection, "refresh_token", "token"));
        Assert.True(await ColumnExistsAsync(connection, "refresh_token", "user_id"));
        Assert.True(await ColumnExistsAsync(connection, "refresh_token", "expires_at"));
        Assert.True(await ColumnExistsAsync(connection, "refresh_token", "revoked"));

        Assert.True(await TableExistsAsync(connection, "hazard_report"));
        Assert.True(await ColumnExistsAsync(connection, "hazard_report", "photo_url"));
        Assert.True(await IndexExistsAsync(connection, "IX_hazard_report_geom_gist"));
        Assert.True(await IndexExistsAsync(connection, "IX_hazard_report_status_reported_at"));
        Assert.True(await IndexExistsAsync(connection, "IX_infrastructure_assets_geometry_gist"));
        Assert.True(await IndexExistsAsync(connection, "IX_route_edges_geometry_gist"));
        Assert.True(await IndexExistsAsync(connection, "IX_route_nodes_location_gist"));
        Assert.True(await ColumnExistsAsync(connection, "infrastructure_assets", "AccessibilityProfile"));
        Assert.True(await IndexExistsAsync(connection, "IX_infrastructure_assets_accessibility_profile_gin"));
        Assert.True(await IndexExistsAsync(connection, "IX_infrastructure_assets_last_observed_at"));
        Assert.True(await TableExistsAsync(connection, "accessibility_verification_submissions"));
        Assert.True(await IndexExistsAsync(connection, "IX_accessibility_verifications_asset_status_submitted"));
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = @tableName
            );
            """;
        command.Parameters.AddWithValue("tableName", tableName);

        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = @tableName
                  AND column_name = @columnName
            );
            """;
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("columnName", columnName);

        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection connection, string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_class index_class
                JOIN pg_namespace index_namespace
                  ON index_namespace.oid = index_class.relnamespace
                WHERE index_namespace.nspname = 'public'
                  AND index_class.relkind = 'i'
                  AND index_class.relname = @indexName
            );
            """;
        command.Parameters.AddWithValue("indexName", indexName);

        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }
}
